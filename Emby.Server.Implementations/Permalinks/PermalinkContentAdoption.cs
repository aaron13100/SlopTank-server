// SlopTank modification notice: added or changed by SlopTank on 2026-09-14.
using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
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
    /// <param name="timeProvider">The time provider.</param>
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
        TimeProvider timeProvider,
        ILogger<PermalinkContentAdoption> logger)
    {
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
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
        var binding = ResolveSoleBinding(
            item,
            await _bindings.FindItemBindingsAsync(item.Id, cancellationToken).ConfigureAwait(false));
        if (await _journal.HasPendingAsync(item.Id, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "identity-mutation-pending",
                $"Item '{item.Id}' still has a pending durable identity mutation.");
        }

        var reservation = await _authority.FindByCapsuleAsync(
            binding.CapsuleId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Conflict(
                "capsule-missing",
                $"Protected capsule '{binding.CapsuleId}' is unknown.");
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

        if (string.Equals(
                request.RecordedContentRoot,
                request.ObservedContentRoot,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "content-unchanged",
                "Recorded and observed content roots are equal; a predecessor is never burned on a no-op.");
        }

        if (!string.Equals(
                request.Confirmation,
                $"adopt-content:{item.Id:D}:{request.ObservedContentRoot}",
                StringComparison.Ordinal))
        {
            throw Conflict(
                "adoption-confirmation-mismatch",
                "The confirmation phrase does not repeat the exact item id and observed content root.");
        }

        var storeRequest = new PermalinkStoreRequest(
            item.Id,
            reservation.ItemKind,
            item.Path,
            observed.ContentRoot,
            observed.Leaves,
            true,
            null);
        var successor = _documents.CreateAdoptedContentSuccessor(storeRequest, snapshot, operationId);
        var bytes = CanonicalJson.Serialize(successor);
        await _transitions.AuthorizeContentTransitionAsync(
            reservation.CapsuleId,
            snapshot.ContentHead.EventId,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendContentAsync(capsulePath, successor, bytes, cancellationToken)
            .ConfigureAwait(false);
        await _bindings.UpdateContentRootAsync(
            item.Id,
            observed.ContentRoot,
            cancellationToken).ConfigureAwait(false);
        _evidence.InvalidateContentDigest(item.Path);
        await WriteJournalRecordAsync(
            item,
            binding,
            request,
            operationId,
            observed.ContentRoot,
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
        return new PermalinkMutationResult(operationId, PermalinkPhase.Committed.Name);
    }

    /// <summary>
    /// Writes the adoption's terminal-on-creation journal record.
    /// </summary>
    /// <remarks>
    /// The record holds operation.json plus exactly one committed phase file, so
    /// PermalinkOperationJournal.GetSettledPhase sees it settled: the reconciler
    /// walks only unsettled directories and never fences the adopted item, and a
    /// crash between the two writes self-heals to an aborted record through the
    /// normal recovery gate. The CreatedAt must stay parseable RFC3339, because
    /// any operation the reconciler does visit with an unparseable timestamp is
    /// logged and left fenced. Auditable where every other operation is.
    /// </remarks>
    /// <param name="item">The adopted library item.</param>
    /// <param name="binding">The sole capsule binding adopted against.</param>
    /// <param name="request">The administrator-confirmed adoption evidence.</param>
    /// <param name="operationId">The derived adoption operation identifier.</param>
    /// <param name="observedContentRoot">The adopted live content root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task WriteJournalRecordAsync(
        BaseItem item,
        PermalinkResolutionBinding binding,
        PermalinkContentAdoptionRequest request,
        Guid operationId,
        string observedContentRoot,
        CancellationToken cancellationToken)
    {
        var createdAt = UtcNow();
        var operation = new PermalinkOperationDocument(
            operationId,
            item.Id,
            binding.CapsuleId,
            "adoption",
            item.Path,
            null,
            request.RecordedContentRoot,
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PermalinkIdentitySnapshot.Capture(item),
            null,
            createdAt);
        await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operationId,
            PermalinkPhase.Committed,
            new PermalinkOperationPhase(
                operationId,
                PermalinkPhase.Committed.Name,
                observedContentRoot,
                createdAt)
            {
                ObservedState =
                    "reason=operator-confirmed-adoption"
                    + $";recorded={request.RecordedContentRoot}"
                    + $";observed={request.ObservedContentRoot}"
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the one binding an adoption runs against.
    /// </summary>
    /// <remarks>
    /// Same contract as PermalinkMutationCoordinator.ResolveVanishedContentBinding:
    /// an item with several aliases must agree on the capsule, or the inconsistency
    /// is reported rather than resolved by choosing arbitrarily.
    /// </remarks>
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

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
