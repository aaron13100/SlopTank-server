// SlopTank modification notice: added or changed by SlopTank on 2026-09-14, 2026-09-23, 2026-10-03.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Publishes an administrator-authorized content successor for an already-fenced leaf.
/// </summary>
/// <remarks>
/// A leaf whose bytes changed outside the guarded Prepare/Commit protocol can never
/// recover through it: Prepare ensures identity first and throws content-mismatch
/// before anything is recorded, and Recover and Resolve both need an operation the
/// fenced leaf does not have. This service is the one elevated exit. Its load-bearing
/// check is the operator restating the live content root, which an attacker who
/// controls the bytes cannot satisfy before the administrator types it. It must never
/// call PermalinkStore.EnsureAsync; that call is what fences the leaf.
/// </remarks>
internal sealed class PermalinkContentAdoption
{
    private static readonly Guid _operationNamespace = Uuid5(
        new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"),
        "sloptank.permalink.content-adoption");

    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkAdoptionJournalRecord _record;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly ILogger<PermalinkContentAdoption> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkContentAdoption"/> class.
    /// </summary>
    /// <param name="evidence">The canonical content evidence computer.</param>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="journal">The durable operation journal.</param>
    /// <param name="capsules">The capsule publication store.</param>
    /// <param name="transitions">The transition claim store.</param>
    /// <param name="documents">The immutable document factory.</param>
    /// <param name="bindings">The rebuildable binding index.</param>
    /// <param name="fileSystem">The durable permalink filesystem.</param>
    /// <param name="logger">The logger.</param>
    public PermalinkContentAdoption(
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkCapsuleStore capsules,
        PermalinkTransitionStore transitions,
        PermalinkDocumentFactory documents,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem,
        ILogger<PermalinkContentAdoption> logger)
    {
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _record = new PermalinkAdoptionJournalRecord(journal);
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Derives the deterministic adoption operation id for one item and observed root.
    /// </summary>
    /// <param name="itemId">The fenced library item.</param>
    /// <param name="observedContentRoot">The operator-restated live content root.</param>
    /// <returns>The RFC 4122 version-5 operation id.</returns>
    /// <remarks>
    /// The id is derived, never caller-supplied: a caller-chosen id colliding with an
    /// existing terminal journal record would replay it (the E03 lesson from
    /// t_260904_113604_449), so the operator has no id to choose.
    /// </remarks>
    internal static Guid DeriveOperationId(Guid itemId, string observedContentRoot)
    {
        return Uuid5(_operationNamespace, $"{itemId:D}|{observedContentRoot}");
    }

    /// <summary>Adopts live leaf content after every fail-closed check passes.</summary>
    /// <remarks>
    /// The authority claim on the capsule head is the commit point. Before it exists the call is a
    /// fresh adoption; once it exists every later call with the same derived id (a client retry,
    /// or an exact replay of a finished adoption) resumes from the claim's own immutable evidence
    /// and never authorizes a second successor. The journal record is published last, terminal on
    /// creation, from that same evidence.
    /// </remarks>
    /// <param name="item">The fenced library item.</param>
    /// <param name="request">The administrator-confirmed adoption evidence.</param>
    /// <param name="operationId">The derived adoption operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded committed adoption state.</returns>
    public async Task<PermalinkMutationResult> AdoptContentAsync(
        BaseItem item,
        PermalinkContentAdoptionRequest request,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var capsuleId = await ResolveCapsuleIdAsync(item, cancellationToken).ConfigureAwait(false);
        if (await _journal.HasPendingExceptAsync(item.Id, operationId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Conflict(
                "identity-mutation-pending",
                $"Item '{item.Id}' still has a pending durable identity mutation.");
        }

        var reservation = await _authority.FindByCapsuleAsync(
            capsuleId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Conflict(
                "capsule-missing",
                $"Protected capsule '{capsuleId}' is unknown.");
        var liveAnchor = _fileSystem.ReadAnchorToken(item.Path);
        if (liveAnchor is null
            || !string.Equals(liveAnchor, reservation.AnchorToken, StringComparison.Ordinal))
        {
            // A NULL anchor is refused with the same code. Unlike a controlled
            // mutation, adoption must never assign a missing anchor: the anchor is
            // not evidence the administrator confirmed.
            throw Conflict(
                "anchor-mismatch",
                $"Live media has anchor '{liveAnchor ?? "none"}', not reservation anchor '{reservation.AnchorToken}'.");
        }

        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            reservation.RootPath,
            item.Path,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);

        var claims = await _transitions.FindContentTransitionsByOperationAsync(
            reservation.CapsuleId,
            operationId,
            cancellationToken).ConfigureAwait(false);
        if (claims.Count > 0)
        {
            return await ResumeAsync(
                item,
                request,
                operationId,
                reservation,
                capsulePath,
                snapshot,
                claims,
                cancellationToken).ConfigureAwait(false);
        }

        if (await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            // A record under this id with no authority claim behind it is not this adoption's.
            throw ReusedOperation(
                operationId,
                "a journal record exists for it but the authority holds no claim for it");
        }

        if (!string.Equals(
                snapshot.ContentHead.ContentRoot,
                request.RecordedContentRoot,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "adoption-predecessor-mismatch",
                $"Capsule '{reservation.CapsuleId}' head '{snapshot.ContentHead.ContentRoot}' "
                + $"is not the recorded predecessor '{request.RecordedContentRoot}'.");
        }

        var observed = await RequireObservedAsync(item, request, cancellationToken).ConfigureAwait(false);
        if (string.Equals(
                request.RecordedContentRoot,
                request.ObservedContentRoot,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "content-unchanged",
                "Recorded and observed content roots are equal; a predecessor is never burned on a no-op.");
        }

        RequireConfirmation(item, request);
        var successor = _documents.CreateAdoptedContentSuccessor(
            ToStoreRequest(item, reservation, observed),
            snapshot,
            operationId);

        // The successor's creation time is published as the claim's, the event's and both journal
        // files' time, so it must be one the reconciler can age: refuse before anything is written.
        PermalinkAdoptionJournalRecord.RequireCreatedAt(successor.CreatedAt, "successor");
        var bytes = CanonicalJson.Serialize(successor);
        await _transitions.AuthorizeContentTransitionAsync(
            reservation.CapsuleId,
            snapshot.ContentHead.EventId,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendContentAsync(capsulePath, successor, bytes, cancellationToken)
            .ConfigureAwait(false);
        var result = await CompleteAsync(
            item,
            new PermalinkAdoptionEvidence(
                operationId,
                item.Id,
                reservation.CapsuleId,
                request.RecordedContentRoot,
                request.ObservedContentRoot,
                successor.PathAssignment.Path,
                successor.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Permalink content adoption: item {ItemId} capsule {CapsuleId} moved from predecessor "
            + "{RecordedContentRoot} to observed {ObservedContentRoot} as event {EventId} under "
            + "operation {OperationId}, confirmed by administrator {AdministratorUserId}.",
            item.Id,
            reservation.CapsuleId,
            request.RecordedContentRoot,
            request.ObservedContentRoot,
            successor.EventId,
            operationId,
            request.AdministratorUserId);
        return result;
    }

    /// <summary>
    /// Settles an adoption whose journal record is missing or unsettled, from the operation
    /// record itself.
    /// </summary>
    /// <remarks>
    /// The authority claim already holds the successor, and the record is only ever published after
    /// the successor was appended, so an unsettled adoption is always completed as committed. It is
    /// never aborted: an abort would contradict the capsule and the claim.
    /// </remarks>
    /// <param name="operation">The unsettled adoption operation record.</param>
    /// <param name="item">The adopted library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The committed adoption state.</returns>
    /// <exception cref="PermalinkException">
    /// Thrown with code manual-intervention when the authority and capsule evidence does not
    /// describe this operation's successor.
    /// </exception>
    public async Task<PermalinkMutationResult> RecoverAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var reservation = await _authority.FindByCapsuleAsync(
            operation.CapsuleId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Conflict(
                "capsule-missing",
                $"Protected capsule '{operation.CapsuleId}' is unknown.");
        var capsulePath = _capsules.GetCapsulePath(
            reservation.RootPath,
            item.Path,
            reservation.ItemKind,
            reservation.CapsuleId);
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        var claims = await _transitions.FindContentTransitionsByOperationAsync(
            reservation.CapsuleId,
            operation.OperationId,
            cancellationToken).ConfigureAwait(false);
        var claim = FindOwnClaim(snapshot, claims, operation.OldContentRoot)
            ?? throw Conflict(
                "manual-intervention",
                $"Adoption '{operation.OperationId}' has no authority claim on its recorded predecessor.");
        var appended = FindAppended(snapshot, claim)
            ?? throw Conflict(
                "manual-intervention",
                $"Adoption '{operation.OperationId}' was authorized but its successor "
                + $"'{claim.EventId}' is not in capsule '{reservation.CapsuleId}'.");
        if (!DeriveOperationId(item.Id, appended.ContentRoot).Equals(operation.OperationId))
        {
            throw Conflict(
                "manual-intervention",
                $"Adoption '{operation.OperationId}' does not derive from the item and the "
                + "content root its authorized successor adopted.");
        }

        RequireAppendedMatchesClaim(appended, claim, operation.OperationId);
        return await CompleteAsync(
            item,
            new PermalinkAdoptionEvidence(
                operation.OperationId,
                item.Id,
                reservation.CapsuleId,
                operation.OldContentRoot,
                appended.ContentRoot,
                appended.PathAssignment.Path,
                claim.CreatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues an adoption the authority already claimed: finishes publication when the crash
    /// came before the successor was appended, and always settles the journal record.
    /// </summary>
    private async Task<PermalinkMutationResult> ResumeAsync(
        BaseItem item,
        PermalinkContentAdoptionRequest request,
        Guid operationId,
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        IReadOnlyList<PermalinkContentTransitionClaim> claims,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(item, request);
        var claim = FindOwnClaim(snapshot, claims, request.RecordedContentRoot)
            ?? throw ReusedOperation(
                operationId,
                $"it was authorized over a different recorded predecessor than '{request.RecordedContentRoot}'");
        var appended = FindAppended(snapshot, claim);
        var evidence = new PermalinkAdoptionEvidence(
            operationId,
            item.Id,
            reservation.CapsuleId,
            request.RecordedContentRoot,
            request.ObservedContentRoot,
            (appended ?? snapshot.ContentHead).PathAssignment.Path,
            claim.CreatedAt);
        var settled = await _record.ValidateExistingAsync(evidence, cancellationToken)
            .ConfigureAwait(false);
        if (appended is null && settled is not null)
        {
            throw EvidenceMismatch(
                operationId,
                $"the journal settled as '{settled.Name}' before the claimed successor "
                + $"'{claim.EventId}' was published");
        }

        if (appended is null)
        {
            appended = await PublishClaimedSuccessorAsync(
                item,
                request,
                operationId,
                reservation,
                capsulePath,
                snapshot,
                claim,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!string.Equals(appended.ContentRoot, request.ObservedContentRoot, StringComparison.Ordinal))
            {
                throw ReusedOperation(
                    operationId,
                    $"its authorized successor adopted '{appended.ContentRoot}', not '{request.ObservedContentRoot}'");
            }

            RequireAppendedMatchesClaim(appended, claim, operationId);
        }

        var result = await CompleteAsync(
            item,
            evidence,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Permalink content adoption: item {ItemId} capsule {CapsuleId} operation {OperationId} "
            + "resumed from its authority claim and settled committed.",
            item.Id,
            reservation.CapsuleId,
            operationId);
        return result;
    }

    /// <summary>
    /// Appends the successor an authority claim names when the crash came before the append,
    /// rebuilding it from the claim and refusing if the rebuilt event is not the claimed one.
    /// </summary>
    private async Task<PermalinkEventDocument> PublishClaimedSuccessorAsync(
        BaseItem item,
        PermalinkContentAdoptionRequest request,
        Guid operationId,
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        PermalinkContentTransitionClaim claim,
        CancellationToken cancellationToken)
    {
        if (!snapshot.ContentHead.EventId.ToString("D").Equals(claim.Predecessor, StringComparison.Ordinal))
        {
            throw Conflict(
                "manual-intervention",
                $"Adoption '{operationId}' claimed predecessor '{claim.Predecessor}' but its "
                + $"successor '{claim.EventId}' is absent and the capsule head moved on.");
        }

        PermalinkAdoptionJournalRecord.RequireCreatedAt(claim.CreatedAt, "authority claim");
        var observed = await RequireObservedAsync(item, request, cancellationToken).ConfigureAwait(false);
        var successor = _documents.CreateAdoptedContentSuccessor(
            ToStoreRequest(item, reservation, observed),
            snapshot,
            operationId,
            claim.EventId,
            claim.CreatedAt);
        var bytes = CanonicalJson.Serialize(successor);
        if (!string.Equals(CanonicalJson.Digest(bytes), claim.EventDigest, StringComparison.Ordinal))
        {
            throw EvidenceMismatch(
                operationId,
                $"the live content no longer rebuilds the successor '{claim.EventId}' the authority claimed");
        }

        await _capsules.AppendContentAsync(capsulePath, successor, bytes, cancellationToken)
            .ConfigureAwait(false);
        return successor;
    }

    /// <summary>
    /// Binds the item to the adopted root (when the record is not already settled) and makes the
    /// terminal journal record exist.
    /// </summary>
    private async Task<PermalinkMutationResult> CompleteAsync(
        BaseItem item,
        PermalinkAdoptionEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (_journal.GetSettledPhase(evidence.OperationId) is null)
        {
            await _bindings.UpdateContentRootAsync(
                item.Id,
                evidence.ObservedContentRoot,
                cancellationToken).ConfigureAwait(false);
            _evidence.InvalidateContentDigest(item.Path);
        }

        await _record.EnsureCommittedAsync(evidence, cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(evidence.OperationId, PermalinkPhase.Committed.Name);
    }

    private async Task<PermalinkEvidenceResult> RequireObservedAsync(
        BaseItem item,
        PermalinkContentAdoptionRequest request,
        CancellationToken cancellationToken)
    {
        var observed = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(observed.ContentRoot, request.ObservedContentRoot, StringComparison.Ordinal))
        {
            // The load-bearing anti-TOCTOU check: the operator restated a root the
            // live bytes no longer hash to, so the evidence is stale and the
            // adoption refuses without touching anything.
            throw Conflict(
                "adoption-observed-mismatch",
                $"Live content root '{observed.ContentRoot}' is not the operator-observed "
                + $"'{request.ObservedContentRoot}'.");
        }

        return observed;
    }

    private static void RequireConfirmation(BaseItem item, PermalinkContentAdoptionRequest request)
    {
        if (!string.Equals(
                request.Confirmation,
                $"adopt-content:{item.Id:D}:{request.ObservedContentRoot}",
                StringComparison.Ordinal))
        {
            throw Conflict(
                "adoption-confirmation-mismatch",
                "The confirmation phrase does not repeat the exact item id and observed content root.");
        }
    }

    private static PermalinkStoreRequest ToStoreRequest(
        BaseItem item,
        PermalinkGenesisReservation reservation,
        PermalinkEvidenceResult observed)
    {
        return new PermalinkStoreRequest(
            item.Id,
            reservation.ItemKind,
            item.Path,
            observed.ContentRoot,
            observed.Leaves,
            true,
            null);
    }

    /// <summary>
    /// Picks the claim this adoption owns: the one placed on a predecessor event whose content
    /// root is the recorded one. A derived id can own several claims when an earlier release let
    /// the same root be adopted over a different predecessor.
    /// </summary>
    private static PermalinkContentTransitionClaim? FindOwnClaim(
        PermalinkCapsuleSnapshot snapshot,
        IReadOnlyList<PermalinkContentTransitionClaim> claims,
        string recordedContentRoot)
    {
        var owned = claims
            .Where(claim => snapshot.Events.Any(candidate =>
                candidate.EventId.ToString("D").Equals(claim.Predecessor, StringComparison.Ordinal)
                && string.Equals(candidate.ContentRoot, recordedContentRoot, StringComparison.Ordinal)))
            .ToArray();
        return owned.Length == 1 ? owned[0] : null;
    }

    private static PermalinkEventDocument? FindAppended(
        PermalinkCapsuleSnapshot snapshot,
        PermalinkContentTransitionClaim claim)
    {
        return snapshot.Events.FirstOrDefault(candidate => candidate.EventId.Equals(claim.EventId));
    }

    /// <summary>
    /// Requires the appended event to be exactly the one the authority claimed: same operation,
    /// kind, predecessor, creation time and canonical digest.
    /// </summary>
    private static void RequireAppendedMatchesClaim(
        PermalinkEventDocument appended,
        PermalinkContentTransitionClaim claim,
        Guid operationId)
    {
        if (!appended.OperationId.Equals(operationId)
            || !string.Equals(appended.Kind, "operator-confirmed-adoption", StringComparison.Ordinal)
            || appended.PreviousContentEventId is not { } previous
            || !previous.ToString("D").Equals(claim.Predecessor, StringComparison.Ordinal)
            || !string.Equals(appended.CreatedAt, claim.CreatedAt, StringComparison.Ordinal)
            || !string.Equals(
                CanonicalJson.Digest(CanonicalJson.Serialize(appended)),
                claim.EventDigest,
                StringComparison.Ordinal))
        {
            throw EvidenceMismatch(
                operationId,
                $"the capsule's event '{appended.EventId}' is not the successor the authority claimed");
        }
    }

    /// <summary>
    /// Picks the one capsule an adoption runs against.
    /// </summary>
    /// <remarks>
    /// Same contract as PermalinkMutationCoordinator.ResolveVanishedContentBinding:
    /// an item with several aliases must agree on the capsule, or the inconsistency
    /// is reported rather than resolved by choosing arbitrarily.
    /// </remarks>
    private async Task<Guid> ResolveCapsuleIdAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var bindings = await _bindings.FindItemBindingsAsync(item.Id, cancellationToken)
            .ConfigureAwait(false);
        if (bindings.Count > 0)
        {
            return ResolveSoleBinding(item, bindings).CapsuleId;
        }

        // A leaf seeded by an aggregate ensure (a series ensure walks its
        // seasons with publishAlias false) carries a capsule and an anchor
        // but no alias or binding row. The fence still refuses its ensure,
        // the ensure is the only writer of bindings, and the binding index
        // has no rebuild pass -- so an already-fenced seeded leaf can stay
        // unbound forever while its content drifts, which is exactly the
        // state this service exists to resolve (production 2026-09-21, The
        // Pitt Season 02). The live anchor token the fence itself trusts is
        // the discovery here: the authority's anchor election is unique per
        // capsule, and every adoption check after this (anchor agreement,
        // predecessor root, observed root, confirmation phrase) still
        // applies unchanged.
        var liveAnchor = _fileSystem.ReadAnchorToken(item.Path);
        if (liveAnchor is not null
            && await _authority.FindByAnchorAsync(liveAnchor, cancellationToken)
                .ConfigureAwait(false) is { } anchored)
        {
            return anchored.CapsuleId;
        }

        throw Conflict(
            "binding-missing",
            $"Item '{item.Id}' has no durable binding to adopt against.");
    }

    private static PermalinkResolutionBinding ResolveSoleBinding(
        BaseItem item,
        IReadOnlyList<PermalinkResolutionBinding> bindings)
    {
        if (bindings.Count == 0)
        {
            throw Conflict(
                "binding-missing",
                $"Item '{item.Id}' has no durable binding to adopt against.");
        }

        var capsules = bindings.Select(value => value.CapsuleId).Distinct().Count();
        if (capsules > 1)
        {
            throw Conflict(
                "binding-capsule-ambiguous",
                $"Item '{item.Id}' has {capsules} capsules across its aliases; "
                + "an adoption cannot choose between them.");
        }

        return bindings[0];
    }

    private static Guid Uuid5(Guid namespaceId, string name)
    {
        // RFC 4122 names SHA-1 as the version-5 derivation hash. This is identifier
        // derivation inside a private namespace, not a security primitive, and the
        // incremental form computes it without constructing a standalone SHA-1.
        var ordered = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        WriteBigEndian(namespaceId, ordered);
        Encoding.UTF8.GetBytes(name, ordered.AsSpan(16));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(ordered);
        var digest = hash.GetHashAndReset();
        digest[6] = (byte)((digest[6] & 0x0F) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(
        [
            digest[3], digest[2], digest[1], digest[0],
            digest[5], digest[4],
            digest[7], digest[6],
            digest[8], digest[9], digest[10], digest[11],
            digest[12], digest[13], digest[14], digest[15]
        ]);
    }

    private static void WriteBigEndian(Guid value, byte[] destination)
    {
        var little = value.ToByteArray();
        destination[0] = little[3];
        destination[1] = little[2];
        destination[2] = little[1];
        destination[3] = little[0];
        destination[4] = little[5];
        destination[5] = little[4];
        destination[6] = little[7];
        destination[7] = little[6];
        for (var index = 8; index < 16; index++)
        {
            destination[index] = little[index];
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    private static PermalinkException ReusedOperation(Guid operationId, string reason)
    {
        return Conflict(
            "operation-id-reused",
            $"Adoption operation '{operationId}' has different input: {reason}.");
    }

    private static PermalinkException EvidenceMismatch(Guid operationId, string reason)
    {
        return Conflict(
            "adoption-evidence-mismatch",
            $"Adoption operation '{operationId}' cannot be completed from its authority claim: {reason}.");
    }
}
