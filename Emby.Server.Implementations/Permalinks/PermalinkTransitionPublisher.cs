// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-01.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Claims, publishes, and verifies immutable capsule successors.
/// </summary>
internal sealed class PermalinkTransitionPublisher
{
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkAuthorityStore? _authority;
    private readonly IPermalinkAtomicFileSystem? _fileSystem;
    private readonly PermalinkBindingIndex? _bindings;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkTransitionPublisher"/> class.
    /// </summary>
    /// <param name="transitions">The transitions.</param>
    /// <param name="capsules">The capsules.</param>
    /// <param name="documents">The documents.</param>
    public PermalinkTransitionPublisher(
        PermalinkTransitionStore transitions,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents)
    {
        _transitions = transitions;
        _capsules = capsules;
        _documents = documents;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkTransitionPublisher"/> class.
    /// </summary>
    /// <remarks>Authenticates aggregate dependency evolution through the private authority.</remarks>
    /// <param name="transitions">The immutable authority elections.</param>
    /// <param name="capsules">The capsule storage boundary.</param>
    /// <param name="documents">The canonical document factory.</param>
    /// <param name="authority">The private capsule reservation authority.</param>
    /// <param name="fileSystem">The stable-anchor storage boundary.</param>
    public PermalinkTransitionPublisher(
        PermalinkTransitionStore transitions,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem)
        : this(transitions, capsules, documents)
    {
        _authority = authority;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkTransitionPublisher"/> class.
    /// </summary>
    /// <remarks>Also settles derived ancestor bindings.</remarks>
    /// <param name="transitions">The immutable authority elections.</param>
    /// <param name="capsules">The capsule storage boundary.</param>
    /// <param name="documents">The canonical document factory.</param>
    /// <param name="authority">The private reservation authority.</param>
    /// <param name="fileSystem">The stable-anchor storage boundary.</param>
    /// <param name="bindings">The rebuildable alias bindings.</param>
    public PermalinkTransitionPublisher(
        PermalinkTransitionStore transitions,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkBindingIndex bindings)
        : this(transitions, capsules, documents, authority, fileSystem)
    {
        _bindings = bindings;
    }

    /// <summary>
    /// Verifies exact content or publishes one authorized strict-superset successor.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="capsulePath">The durable capsule path.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkCapsuleSnapshot> ReconcileContentAsync(
        PermalinkStoreRequest request,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_authority is not null && request.ItemKind is ("Series" or "Season" or "BoxSet"))
        {
            snapshot = await CompleteAggregatePublicationAsync(
                capsulePath, snapshot, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(
                request.ContentRoot,
                snapshot.ContentHead.ContentRoot,
                StringComparison.Ordinal))
        {
            return snapshot;
        }

        if (request.ItemKind is not ("Series" or "Season" or "BoxSet"))
        {
            throw Conflict(
                "content-mismatch",
                $"Current content for '{request.Path}' does not match capsule '{snapshot.Header.CapsuleId}'.");
        }

        PermalinkEventDocument successor;
        if (_authority is null)
        {
            // Preserve the existing construction contract without granting an
            // unsupported lineage proof to callers lacking the authority.
            if (request.ItemKind == "BoxSet"
                || !IsStrictSuperset(snapshot.ContentHead.Leaves, request.Leaves))
            {
                throw Conflict("aggregate-proof-unavailable", "Aggregate evolution requires the private authority.");
            }

            successor = _documents.CreateContentSuccessor(request, snapshot);
        }
        else
        {
            var causes = await VerifyAggregateEvolutionAsync(
                snapshot.ContentHead.Leaves, request.Leaves, cancellationToken).ConfigureAwait(false);
            if (PermalinkEvidence.Build(request.Leaves).ContentRoot != request.ContentRoot)
            {
                throw Conflict("aggregate-root-mismatch", "Aggregate leaves do not reproduce their requested root.");
            }

            successor = _documents.CreateAggregateSuccessor(request, snapshot, causes);
        }

        var bytes = CanonicalJson.Serialize(successor);
        await _capsules.StageContentAsync(capsulePath, successor, bytes, cancellationToken).ConfigureAwait(false);
        await _transitions.AuthorizeContentTransitionAsync(
            snapshot.Header.CapsuleId,
            snapshot.ContentHead.EventId,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendContentAsync(
            capsulePath,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        return await _capsules.ReadValidatedAsync(
            capsulePath,
            snapshot.Header.CapsuleId,
            snapshot.Header.ItemKind,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revalidates one frozen ancestor dependency immediately before publication.</summary>
    /// <param name="claim">The immutable dependency.</param>
    /// <param name="targetCapsuleId">The unchanged prepared-old leaf.</param>
    /// <param name="cancellationToken">The durable operation lifetime.</param>
    /// <param name="authorizedRemovalOperationId">An exact membership-removal replay, when applicable.</param>
    /// <returns>A task representing the asynchronous verification.</returns>
    public async Task VerifyDependencyAsync(
        PermalinkMutationClaim claim,
        Guid targetCapsuleId,
        CancellationToken cancellationToken,
        Guid? authorizedRemovalOperationId = null)
    {
        var (reservation, snapshot) = await ReadCurrentCapsuleAsync(
            claim.CapsuleId, cancellationToken).ConfigureAwait(false);
        if (claim.Kind != "aggregate" || !reservation.BindingId.Equals(claim.BindingInstanceId)
            || !snapshot.ContentHead.BindingInstanceId.Equals(claim.BindingInstanceId))
        {
            throw Conflict("aggregate-binding-mismatch", $"Dependency '{claim.CapsuleId}' changed binding identity.");
        }

        var previous = await VerifyContentLineageAsync(
            claim.CapsuleId, snapshot, claim.Predecessor, cancellationToken).ConfigureAwait(false);
        if (!previous.EventId.Equals(snapshot.ContentHead.EventId))
        {
            var target = targetCapsuleId.ToString("D");
            var oldLeaf = FindLeaf(previous.Leaves, target);
            var currentLeaf = FindLeaf(snapshot.ContentHead.Leaves, target);
            if (oldLeaf != currentLeaf)
            {
                var removalEvents = snapshot.Events.Where(value =>
                    authorizedRemovalOperationId.HasValue
                    && value.OperationId.Equals(authorizedRemovalOperationId.Value)).ToArray();
                if (currentLeaf is not null || authorizedRemovalOperationId is null || removalEvents.Length != 1
                    || !await _transitions.IsAuthorizedContentEventAsync(
                        claim.CapsuleId, removalEvents[0], cancellationToken).ConfigureAwait(false))
                {
                    throw Conflict("aggregate-target-mismatch", $"Dependency '{claim.CapsuleId}' changed the prepared target leaf.");
                }
            }
        }
    }

    /// <summary>Settles only the exact membership removal frozen by a guarded removal operation.</summary>
    /// <param name="operation">The persisted removal and ancestor closure.</param>
    /// <param name="cancellationToken">The durable operation lifetime.</param>
    /// <returns>A task representing deterministic ancestor publication.</returns>
    public async Task SettleRemovalAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var removal = operation.Removal
            ?? throw Conflict("removal-intent-missing", "Aggregate retirement requires exact removal evidence.");
        if (operation.Bundle is null)
        {
            // Older removals did not freeze ancestor membership. Their
            // existing terminal outcome is not permission to infer a deletion.
            return;
        }

        if (_bindings is null)
        {
            throw Conflict("aggregate-proof-unavailable", "Removal ancestor settlement requires derived binding publication.");
        }

        var dependencies = new List<(PermalinkMutationClaim Claim, string Kind)>();
        foreach (var dependency in operation.Bundle.Claims.Where(value => value.Kind == "aggregate"))
        {
            var (ancestor, _) = await ReadCurrentCapsuleAsync(dependency.CapsuleId, cancellationToken).ConfigureAwait(false);
            dependencies.Add((dependency, ancestor.ItemKind));
        }

        foreach (var dependency in dependencies
                     .OrderBy(value => value.Kind == "Season" ? 0 : value.Kind == "Series" ? 1 : 2)
                     .ThenBy(value => value.Claim.CapsuleId))
        {
            var claim = dependency.Claim;
            var (reservation, snapshot) = await ReadCurrentCapsuleAsync(
                claim.CapsuleId, cancellationToken).ConfigureAwait(false);
            if (!reservation.BindingId.Equals(claim.BindingInstanceId)
                || !snapshot.ContentHead.BindingInstanceId.Equals(claim.BindingInstanceId))
            {
                throw Conflict("aggregate-binding-mismatch", $"Removal ancestor '{claim.CapsuleId}' changed binding.");
            }

            _ = await VerifyContentLineageAsync(
                claim.CapsuleId, snapshot, claim.Predecessor, cancellationToken).ConfigureAwait(false);
            var sourcePath = removal.SourceCapsuleId.ToString("D");
            var source = FindLeaf(snapshot.ContentHead.Leaves, sourcePath);
            IReadOnlyList<PermalinkLeaf> desiredLeaves;
            IReadOnlyList<PermalinkEventDocument> causes = [];
            if (source is null)
            {
                var prior = snapshot.Events.Where(value => value.OperationId.Equals(operation.OperationId)).ToArray();
                if (prior.Length == 1
                    && await _transitions.IsAuthorizedContentEventAsync(
                        claim.CapsuleId, prior[0], cancellationToken).ConfigureAwait(false))
                {
                    await _bindings.UpdateContentRootAsync(
                        claim.ItemId, snapshot.ContentHead.ContentRoot, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (reservation.ItemKind != "BoxSet" || prior.Length != 0)
                {
                    throw Conflict("aggregate-removal-mismatch", "Absent membership has no exact authorized removal successor.");
                }

                (desiredLeaves, causes) = await RefreshRemovalMembersAsync(
                    snapshot.ContentHead.Leaves, operation.OperationId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!IsDescendant(source) || source.Digest != removal.SourceContentRoot)
                {
                    throw Conflict("aggregate-removal-mismatch", "Removal membership differs from the frozen source capsule.");
                }

                desiredLeaves = snapshot.ContentHead.Leaves.Where(value => value != source).ToArray();
            }

            var evidence = PermalinkEvidence.Build(desiredLeaves);
            if (evidence.ContentRoot == snapshot.ContentHead.ContentRoot)
            {
                continue;
            }

            var request = new PermalinkStoreRequest(
                claim.ItemId,
                reservation.ItemKind,
                reservation.CurrentPath,
                evidence.ContentRoot,
                evidence.Leaves,
                false,
                null);
            var successor = _documents.CreateAggregateSuccessor(request, snapshot, causes, operation);
            var bytes = CanonicalJson.Serialize(successor);
            await _capsules.StageContentAsync(
                reservation.CapsulePath, successor, bytes, cancellationToken).ConfigureAwait(false);
            await _transitions.AuthorizeContentTransitionAsync(
                claim.CapsuleId, snapshot.ContentHead.EventId, successor, bytes, cancellationToken).ConfigureAwait(false);
            await _capsules.AppendContentAsync(
                reservation.CapsulePath, successor, bytes, cancellationToken).ConfigureAwait(false);
            await _bindings.UpdateContentRootAsync(
                claim.ItemId, evidence.ContentRoot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(IReadOnlyList<PermalinkLeaf> Leaves, IReadOnlyList<PermalinkEventDocument> Causes)> RefreshRemovalMembersAsync(
        IReadOnlyList<PermalinkLeaf> leaves,
        Guid removalOperationId,
        CancellationToken cancellationToken)
    {
        var desired = new List<PermalinkLeaf>();
        var causes = new List<PermalinkEventDocument>();
        foreach (var leaf in leaves)
        {
            if (!IsDescendant(leaf) || !Guid.TryParseExact(leaf.Path, "D", out var capsuleId))
            {
                desired.Add(leaf);
                continue;
            }

            var (_, child) = await ReadCurrentCapsuleAsync(capsuleId, cancellationToken).ConfigureAwait(false);
            if (child.ContentHead.ContentRoot == leaf.Digest)
            {
                desired.Add(leaf);
                continue;
            }

            _ = await VerifyContentLineageAsync(capsuleId, child, leaf.Digest, cancellationToken).ConfigureAwait(false);
            var removalEvents = child.Events.Where(value => value.OperationId.Equals(removalOperationId)).ToArray();
            if (removalEvents.Length != 1
                || !await _transitions.IsAuthorizedContentEventAsync(
                    capsuleId, removalEvents[0], cancellationToken).ConfigureAwait(false))
            {
                throw Conflict("aggregate-removal-mismatch", "Changed collection membership lacks this exact descendant removal cause.");
            }

            desired.Add(leaf with { Digest = child.ContentHead.ContentRoot });
            causes.Add(child.ContentHead);
        }

        return (desired, causes);
    }

    private async Task<IReadOnlyList<PermalinkEventDocument>> VerifyAggregateEvolutionAsync(
        IReadOnlyList<PermalinkLeaf> previous,
        IReadOnlyList<PermalinkLeaf> current,
        CancellationToken cancellationToken)
    {
        if (previous.GroupBy(LeafIdentity).Any(group => group.Count() != 1)
            || current.GroupBy(LeafIdentity).Any(group => group.Count() != 1))
        {
            throw Conflict("aggregate-leaf-ambiguous", "Aggregate membership contains duplicate identities.");
        }

        var oldLeaves = previous.ToDictionary(LeafIdentity);
        var newLeaves = current.ToDictionary(LeafIdentity);
        if (oldLeaves.Keys.Any(key => !newLeaves.ContainsKey(key)))
        {
            throw Conflict("content-mismatch", "Aggregate membership removal requires an exact guarded removal operation.");
        }

        var causes = new List<PermalinkEventDocument>();
        foreach (var leaf in current)
        {
            if (!IsDescendant(leaf))
            {
                if (!oldLeaves.TryGetValue(LeafIdentity(leaf), out var oldRoot) || oldRoot != leaf
                    || leaf.Kind != "boxset-node" || leaf.Role != "root")
                {
                    throw Conflict("content-mismatch", "Aggregate root identity cannot be replaced through descendant reconciliation.");
                }

                continue;
            }

            if (!Guid.TryParseExact(leaf.Path, "D", out var capsuleId)
                || leaf.ByteLength != 0 || leaf.Count != 1)
            {
                throw Conflict("aggregate-leaf-invalid", "A descendant must identify exactly one capsule.");
            }

            var (reservation, child) = await ReadCurrentCapsuleAsync(capsuleId, cancellationToken).ConfigureAwait(false);
            if (reservation.ItemKind != leaf.Role || child.ContentHead.ContentRoot != leaf.Digest)
            {
                throw Conflict("aggregate-leaf-mismatch", $"Descendant '{capsuleId}' does not match its current authorized capsule.");
            }

            if (oldLeaves.TryGetValue(LeafIdentity(leaf), out var oldLeaf) && oldLeaf != leaf)
            {
                _ = await VerifyContentLineageAsync(
                    capsuleId, child, oldLeaf.Digest, cancellationToken).ConfigureAwait(false);
            }

            causes.Add(child.ContentHead);
        }

        return causes;
    }

    private async Task<(PermalinkGenesisReservation Reservation, PermalinkCapsuleSnapshot Snapshot)> ReadCurrentCapsuleAsync(
        Guid capsuleId,
        CancellationToken cancellationToken)
    {
        if (_authority is null || _fileSystem is null)
        {
            throw Conflict("aggregate-proof-unavailable", "Aggregate evolution requires authority and stable-anchor verification.");
        }

        var reservation = await _authority.FindByCapsuleAsync(capsuleId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("capsule-missing", $"Dependency capsule '{capsuleId}' has no authority reservation.");
        var current = await _authority.FindByCurrentPathAsync(reservation.CurrentPath, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.CapsuleId.Equals(capsuleId)
            || _fileSystem.ReadAnchorToken(reservation.CurrentPath) != reservation.AnchorToken)
        {
            throw Conflict("aggregate-anchor-mismatch", $"Dependency capsule '{capsuleId}' no longer owns its live path/anchor.");
        }

        var snapshot = await _capsules.ReadValidatedAsync(
            reservation.CapsulePath, capsuleId, reservation.ItemKind, cancellationToken).ConfigureAwait(false);
        if (reservation.ItemKind is "Series" or "Season" or "BoxSet")
        {
            snapshot = await CompleteAggregatePublicationAsync(
                reservation.CapsulePath, snapshot, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.ContentHead.Kind is "mint" or "identity_seed")
        {
            if (CanonicalJson.Digest(reservation.EventJson) != CanonicalJson.Digest(CanonicalJson.Serialize(snapshot.ContentHead)))
            {
                throw Conflict("aggregate-genesis-mismatch", $"Capsule '{capsuleId}' changed its authority genesis.");
            }
        }
        else if (!await _transitions.IsAuthorizedContentEventAsync(
                     capsuleId, snapshot.ContentHead, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("aggregate-lineage-unproved", $"Capsule '{capsuleId}' has an unauthorized current event.");
        }

        return (reservation, snapshot);
    }

    private async Task<PermalinkCapsuleSnapshot> CompleteAggregatePublicationAsync(
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // The durable staged bytes precede election. Even if descendants evolve
        // again after a crash, finish the actual winner before deriving another
        // successor; never regenerate an elected event from mutable inputs.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var claim = await _transitions.FindContentTransitionAsync(
                snapshot.Header.CapsuleId, snapshot.ContentHead.EventId, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                return snapshot;
            }

            var winner = await _capsules.ReadPublicationAsync(
                capsulePath, claim.EventId, cancellationToken).ConfigureAwait(false);
            if (!winner.PreviousContentEventId.HasValue
                || !winner.PreviousContentEventId.Value.Equals(snapshot.ContentHead.EventId)
                || !await _transitions.IsAuthorizedContentEventAsync(
                    snapshot.Header.CapsuleId, winner, cancellationToken).ConfigureAwait(false))
            {
                throw Conflict("aggregate-publication-mismatch", "Staged aggregate successor does not match its exact authority winner.");
            }

            await _capsules.AppendContentAsync(
                capsulePath, winner, CanonicalJson.Serialize(winner), cancellationToken).ConfigureAwait(false);
            snapshot = await _capsules.ReadValidatedAsync(
                capsulePath, snapshot.Header.CapsuleId, snapshot.Header.ItemKind, cancellationToken).ConfigureAwait(false);
        }

        throw Conflict("aggregate-publication-busy", "Aggregate publication advanced eight times; retry against its current head.");
    }

    private async Task<PermalinkEventDocument> VerifyContentLineageAsync(
        Guid capsuleId,
        PermalinkCapsuleSnapshot snapshot,
        string predecessorRoot,
        CancellationToken cancellationToken)
    {
        var events = snapshot.Events.ToDictionary(value => value.EventId);
        var visited = new HashSet<Guid>();
        var cursor = snapshot.ContentHead;
        while (cursor.ContentRoot != predecessorRoot)
        {
            if (!visited.Add(cursor.EventId)
                || cursor.Kind is not ("controlled-mutation" or "operator-confirmed-adoption" or "append-only-discovery")
                || !await _transitions.IsAuthorizedContentEventAsync(capsuleId, cursor, cancellationToken).ConfigureAwait(false)
                || cursor.PreviousContentEventId is not { } parent
                || !events.TryGetValue(parent, out var prior)
                || !prior.BindingInstanceId.Equals(cursor.BindingInstanceId))
            {
                throw Conflict("aggregate-lineage-unproved", $"Capsule '{capsuleId}' has no authority-proved path from '{predecessorRoot}'.");
            }

            cursor = prior;
        }

        return cursor;
    }

    private static bool IsDescendant(PermalinkLeaf leaf) => leaf.Kind is "descendant" or "boxset-member";

    private static PermalinkLeaf? FindLeaf(IReadOnlyList<PermalinkLeaf> leaves, string path)
    {
        var matches = leaves.Where(value => value.Path == path).ToArray();
        if (matches.Length > 1)
        {
            throw Conflict("aggregate-leaf-ambiguous", $"Capsule '{path}' occurs more than once in the aggregate manifest.");
        }

        return matches.Length == 0 ? null : matches[0];
    }

    private static (string Kind, string Role, string? Path) LeafIdentity(PermalinkLeaf leaf) => (leaf.Kind, leaf.Role, leaf.Path);

    /// <summary>
    /// Returns existing aliases or elects, publishes, and verifies the first alias.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="reservation">The reservation.</param>
    /// <param name="capsulePath">The durable capsule path.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<IReadOnlyList<string>> GetOrPublishAliasesAsync(
        PermalinkStoreRequest request,
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var aliases = await _transitions.GetAliasesAsync(
            reservation.CapsuleId,
            cancellationToken).ConfigureAwait(false);
        if (aliases.Count == 0 && request.PublishAlias)
        {
            aliases = request.PreferredAlias is null
                ? await PublishFallbackAliasAsync(
                    reservation,
                    capsulePath,
                    snapshot,
                    cancellationToken).ConfigureAwait(false)
                : await PublishExternalAliasAsync(
                    reservation,
                    capsulePath,
                    snapshot,
                    request.PreferredAlias,
                    cancellationToken).ConfigureAwait(false);
        }

        if (reservation.IssuedId is not null && reservation.IssuanceNonce.HasValue)
        {
            await _transitions.ValidateIssuanceAsync(
                reservation.IssuedId,
                reservation.IssuanceNonce.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return aliases;
    }

    private async Task<IReadOnlyList<string>> PublishFallbackAliasAsync(
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var issuance = await _transitions.AllocateAsync(cancellationToken).ConfigureAwait(false);
        var aliasEvent = _documents.CreateAliasSuccessor(
            reservation,
            snapshot,
            issuance);
        var bytes = CanonicalJson.Serialize(aliasEvent);
        var claim = await _transitions.ClaimFirstAliasAsync(
            reservation.CapsuleId,
            issuance,
            aliasEvent,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendAliasAsync(
            capsulePath,
            claim,
            cancellationToken).ConfigureAwait(false);
        await _transitions.ValidateIssuanceAsync(
            claim.PermalinkId,
            claim.IssuanceNonce
                ?? throw Conflict(
                    "issuance-missing",
                    $"Fallback alias '{claim.PermalinkId}' has no issuance nonce."),
            cancellationToken).ConfigureAwait(false);
        return [claim.PermalinkId];
    }

    private async Task<IReadOnlyList<string>> PublishExternalAliasAsync(
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        string alias,
        CancellationToken cancellationToken)
    {
        var aliasEvent = _documents.CreateExternalAliasSuccessor(
            reservation,
            snapshot,
            alias);
        var bytes = CanonicalJson.Serialize(aliasEvent);
        var claim = await _transitions.ClaimExternalAliasAsync(
            reservation.CapsuleId,
            alias,
            aliasEvent,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendAliasAsync(
            capsulePath,
            claim,
            cancellationToken).ConfigureAwait(false);
        if (claim.IssuanceNonce.HasValue)
        {
            await _transitions.ValidateIssuanceAsync(
                claim.PermalinkId,
                claim.IssuanceNonce.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return [claim.PermalinkId];
    }

    private static bool IsStrictSuperset(
        IReadOnlyList<PermalinkLeaf> previous,
        IReadOnlyList<PermalinkLeaf> current)
    {
        return current.Count > previous.Count
            && previous.All(oldLeaf => current.Contains(oldLeaf));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
