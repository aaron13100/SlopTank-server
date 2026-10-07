// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-08-01, 2026-09-02, 2026-09-07, 2026-09-09, 2026-10-01, 2026-10-03, 2026-10-07.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns recoverable staging, quarantine, publication, and capsule advancement for media bytes.
/// </summary>
internal sealed class PermalinkMediaMutation
{
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkMutationClaimStore _claims;
    private readonly PermalinkPathLeaseFactory _leases;
    private readonly PermalinkAggregateSuccessorPlanner _successors;

    public PermalinkMediaMutation(
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkCapsuleStore capsules,
        PermalinkTransitionStore transitions,
        PermalinkDocumentFactory documents,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkMutationClaimStore claims,
        PermalinkPathLeaseFactory leases,
        PermalinkAggregateSuccessorPlanner successors)
    {
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _claims = claims;
        _leases = leases;
        _successors = successors;
    }

    /// <summary>
    /// Stages, claims, publishes and finalizes the replacement under one item-path lease. Keeping
    /// the lease through the possible anchor re-assignment lets an external admission be redeemed
    /// once against the original inode and remain exact for every later step in that publication.
    /// </summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="request">The commit request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mutation result.</returns>
    public async Task<PermalinkMutationResult> CommitAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        await using (var leases = await _leases.AcquireWriterSetAsync([item.Path], cancellationToken)
            .ConfigureAwait(false))
        {
            var staging = await StageAsync(
                operation,
                item,
                request.StagedPath,
                cancellationToken).ConfigureAwait(false);
            await VerifyPreparedOldAsync(operation, item, claimed: false, cancellationToken)
                .ConfigureAwait(false);
            await _claims.ClaimAsync(RequireBundle(operation), operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            await VerifyPreparedOldAsync(operation, item, claimed: true, cancellationToken)
                .ConfigureAwait(false);
            var permit = await SwapInStagedAsync(
                leases,
                operation,
                item,
                staging,
                cancellationToken,
                request.WriterAdmissionId)
                .ConfigureAwait(false);
            return await FinalizeAsync(operation, item, cancellationToken, leases, permit)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves the live bytes to operation quarantine (when not already there) and publishes the
    /// operation's staged bytes at the item path. The pair is one publication unit under one
    /// lease, so the key is rechecked once, before the first move that will actually run; a lost
    /// recheck refuses before anything moved and leaves the staged output where it is.
    /// </summary>
    /// <param name="leases">The writer's leases; must cover the item path.</param>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="staging">The staged desired bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="writerAdmissionId">The external writer admission, when this commit owns one.</param>
    /// <returns>The redeemed permit to retain for later steps under the same lease.</returns>
    /// <exception cref="PermalinkException">Conflict when the item path's key changed under the lease.</exception>
    internal async Task<PermalinkWriterAdmissionPermit?> SwapInStagedAsync(
        PermalinkWriterLeaseSet leases,
        PermalinkOperationDocument operation,
        BaseItem item,
        string staging,
        CancellationToken cancellationToken,
        Guid? writerAdmissionId = null)
    {
        var quarantine = GetQuarantinePath(operation);
        var quarantineNeeded = !File.Exists(quarantine);
        PermalinkWriterAdmissionPermit? permit = null;
        if (writerAdmissionId.HasValue)
        {
            permit = await leases.RedeemAdmissionAsync(
                item.Path,
                writerAdmissionId.Value,
                "replace-media",
                cancellationToken).ConfigureAwait(false);
        }
        else if (quarantineNeeded || !File.Exists(item.Path))
        {
            await leases.RecheckAsync(item.Path, cancellationToken).ConfigureAwait(false);
        }

        if (quarantineNeeded)
        {
            File.Move(item.Path, quarantine);
        }

        if (!File.Exists(item.Path))
        {
            File.Move(staging, item.Path);
        }

        _evidence.InvalidateContentDigest(item.Path);
        return permit;
    }

    /// <summary>
    /// Moves the quarantined prepared-old bytes back to the item path, rechecking its key first.
    /// </summary>
    /// <param name="leases">The writer's leases; must cover the item path.</param>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the restoration.</returns>
    /// <exception cref="PermalinkException">Conflict when the item path's key changed under the lease.</exception>
    internal async Task RestoreQuarantineAsync(
        PermalinkWriterLeaseSet leases,
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        await leases.RecheckAsync(item.Path, cancellationToken).ConfigureAwait(false);
        File.Move(GetQuarantinePath(operation), item.Path);
        _evidence.InvalidateContentDigest(item.Path);
    }

    /// <summary>
    /// Leases the item path for a physical step run outside Commit (recovery, resolution).
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease set.</returns>
    internal Task<PermalinkWriterLeaseSet> LeaseItemPathAsync(BaseItem item, CancellationToken cancellationToken)
        => _leases.AcquireWriterSetAsync([item.Path], cancellationToken);

    /// <summary>
    /// Copies caller-owned bytes into durable operation staging and publishes exact Ready evidence.
    /// </summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="source">The source stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<string> StageAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        string? source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw Conflict("staging-missing", "The exact desired staging file is missing or unreadable.");
        }

        var staging = GetStagingPath(operation);
        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(staging)!);
        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(GetQuarantinePath(operation))!);
        var supplied = await _evidence.ComputeMediaReplacementAsync(
            item,
            source,
            cancellationToken).ConfigureAwait(false);
        PermalinkStagingCopy.RemoveStaleTemporaries(staging);
        if (!File.Exists(staging))
        {
            var anchor = _fileSystem.ReadAnchorToken(item.Path)
                ?? throw Conflict("anchor-missing", $"Protected item '{item.Id}' lost its stable anchor.");

            // The anchor is applied to the complete temporary file before the atomic
            // rename, so a staging file that exists is always complete and anchored.
            await PermalinkStagingCopy.CopyAtomicAsync(
                _fileSystem,
                source,
                staging,
                temporary => _fileSystem.AssignAnchorToken(temporary, anchor),
                cancellationToken).ConfigureAwait(false);
        }

        var desired = await _evidence.ComputeMediaReplacementAsync(
            item,
            staging,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(supplied.ContentRoot, desired.ContentRoot, StringComparison.Ordinal))
        {
            throw Conflict(
                "staging-evidence-mismatch",
                "Operation staging already contains different desired bytes.");
        }

        var existingReady = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false);
        if (existingReady is not null)
        {
            ValidateReadyFormat(existingReady);
            if (!string.Equals(
                    existingReady.ContentRoot,
                    desired.ContentRoot,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "staging-evidence-mismatch",
                    "Restaged bytes do not match the operation's immutable Ready evidence.");
            }

            if (existingReady.PreparedSuccessors is not null)
            {
                ValidatePreparedSuccessors(operation, existingReady);
            }
            else
            {
                RequireLegacySingleClaim(operation);
            }

            return staging;
        }

        var successors = await _successors.PrepareAsync(
            operation,
            item,
            desired,
            cancellationToken).ConfigureAwait(false);

        await _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            new PermalinkOperationPhase(
                operation.OperationId,
                PermalinkPhase.Ready.Name,
                desired.ContentRoot,
                operation.CreatedAt,
                desired.Leaves)
            {
                PreparedSuccessors = successors
            },
            cancellationToken).ConfigureAwait(false);
        return staging;
    }

    /// <summary>Returns the deterministic operation-owned desired staging path.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public string GetStagingPath(PermalinkOperationDocument operation)
    {
        return Path.Combine(_journal.GetContentOperationPath(operation), "staging", "desired");
    }

    /// <summary>Returns the deterministic operation-owned prepared-old quarantine path.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public string GetQuarantinePath(PermalinkOperationDocument operation)
    {
        return Path.Combine(_journal.GetContentOperationPath(operation), "quarantine", "old");
    }

    /// <summary>Returns the exact immutable bundle required by a protected operation.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public static PermalinkMutationBundle RequireBundle(PermalinkOperationDocument operation)
    {
        var bundle = operation.Bundle
            ?? new PermalinkMutationBundle(
                [
                    new PermalinkMutationClaim(
                        operation.CapsuleId,
                        operation.ItemId,
                        Guid.Empty,
                        operation.OldContentRoot,
                        "content")
                ],
                [new PermalinkMutationPart("main", operation.SourcePath, operation.DestinationPath)],
                Path.GetDirectoryName(operation.SourcePath)!,
                null);

        // Media operations with no aggregate closure have one predecessor,
        // and the immutable prepared-old root is its authoritative value.
        // Builds before 2026-09-07 accidentally persisted the capsule's
        // genesis here instead. After one successful rewrite that claim is
        // already consumed, leaving an otherwise exact Ready operation
        // permanently pending and fencing playback. Repair only this
        // unambiguous single-claim shape; a multi-capsule bundle does not
        // preserve every aggregate's prepared head and must still fail closed.
        if (operation.Kind == "media"
            && bundle.Claims.Count == 1
            && bundle.Claims[0] is { } claim
            && claim.Kind == "content"
            && claim.CapsuleId.Equals(operation.CapsuleId)
            && claim.ItemId.Equals(operation.ItemId)
            && !string.Equals(
                claim.Predecessor,
                operation.OldContentRoot,
                StringComparison.Ordinal))
        {
            return bundle with
            {
                Claims = [claim with { Predecessor = operation.OldContentRoot }]
            };
        }

        return bundle;
    }

    public async Task<PermalinkMutationResult> FinalizeAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken,
        PermalinkWriterLeaseSet? heldLeases = null,
        PermalinkWriterAdmissionPermit? permit = null)
    {
        var desired = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("ready-missing", "Operation-owned desired evidence is missing.");
        ValidateReadyFormat(ready);
        if (!string.Equals(ready.ContentRoot, desired.ContentRoot, StringComparison.Ordinal))
        {
            throw Conflict(
                "desired-evidence-mismatch",
                "Published media does not match the exact operation-owned Ready evidence.");
        }

        var successors = ready.PreparedSuccessors is null
            ? await _successors.PrepareLegacySingleAsync(
                operation,
                item,
                desired,
                cancellationToken).ConfigureAwait(false)
            : ValidatePreparedSuccessors(operation, ready);

        await _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.Published,
            new PermalinkOperationPhase(
                operation.OperationId,
                PermalinkPhase.Published.Name,
                desired.ContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        await _claims.FinalizePreparedAsync(
            RequireBundle(operation),
            operation.OperationId,
            successors,
            "ready",
            cancellationToken).ConfigureAwait(false);
        await AppendContentAsync(operation, item, successors, cancellationToken, heldLeases, permit)
            .ConfigureAwait(false);
        await _bindings.UpdateContentRootAsync(
            operation.ItemId,
            desired.ContentRoot,
            cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.Committed,
            new PermalinkOperationPhase(
                operation.OperationId,
                PermalinkPhase.Committed.Name,
                desired.ContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
    }

    private async Task VerifyPreparedOldAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        bool claimed,
        CancellationToken cancellationToken)
    {
        var current = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal))
        {
            if (claimed)
            {
                await _journal.WritePhaseAsync(
                    operation.OperationId,
                    PermalinkPhase.ClaimedPending,
                    new PermalinkOperationPhase(
                        operation.OperationId,
                        PermalinkPhase.ClaimedPending.Name,
                        current.ContentRoot,
                        operation.CreatedAt,
                        current.Leaves),
                    cancellationToken).ConfigureAwait(false);
            }

            throw Conflict(
                "prepared-old-mismatch",
                "Live content no longer matches prepared-old evidence.");
        }
    }

    private async Task AppendContentAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        IReadOnlyList<PermalinkPreparedContentSuccessor> successors,
        CancellationToken cancellationToken,
        PermalinkWriterLeaseSet? heldLeases,
        PermalinkWriterAdmissionPermit? permit)
    {
        var reservation = await _authority.FindByCapsuleAsync(
            operation.CapsuleId,
            cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict(
                "capsule-missing",
                $"Protected capsule '{operation.CapsuleId}' is unknown.");
        var liveAnchor = _fileSystem.ReadAnchorToken(item.Path);
        if (liveAnchor is null)
        {
            if (heldLeases is not null)
            {
                await heldLeases.RecheckWithPermitAsync(item.Path, permit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var leases = await _leases.AcquireWriterSetAsync([item.Path], cancellationToken)
                    .ConfigureAwait(false);
                await leases.RecheckAsync(item.Path, cancellationToken).ConfigureAwait(false);
            }

            _fileSystem.AssignAnchorToken(item.Path, reservation.AnchorToken);
        }
        else if (!string.Equals(liveAnchor, reservation.AnchorToken, StringComparison.Ordinal))
        {
            throw Conflict(
                "anchor-mismatch",
                $"Published media has anchor '{liveAnchor}', not prepared anchor '{reservation.AnchorToken}'.");
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
        foreach (var prepared in successors.OrderBy(value => value.CapsuleId))
        {
            var preparedReservation = prepared.CapsuleId.Equals(reservation.CapsuleId)
                ? reservation
                : await _authority.FindByCapsuleAsync(prepared.CapsuleId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw Conflict("capsule-missing", $"Protected capsule '{prepared.CapsuleId}' is unknown.");
            var preparedPath = prepared.CapsuleId.Equals(reservation.CapsuleId)
                ? capsulePath
                : preparedReservation.CapsulePath;
            var preparedSnapshot = prepared.CapsuleId.Equals(reservation.CapsuleId)
                ? snapshot
                : await _capsules.ReadValidatedAsync(
                    preparedPath,
                    prepared.CapsuleId,
                    preparedReservation.ItemKind,
                    cancellationToken).ConfigureAwait(false);
            if (preparedSnapshot.ContentHead.EventId.Equals(prepared.Event.EventId))
            {
                var expectedBytes = CanonicalJson.Serialize(prepared.Event);
                await _capsules.VerifyContentEventAsync(
                    preparedPath,
                    prepared.Event,
                    expectedBytes,
                    cancellationToken).ConfigureAwait(false);
                await _bindings.UpdateContentRootAsync(
                    prepared.ItemId,
                    prepared.Event.ContentRoot,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!prepared.Event.PreviousContentEventId.HasValue
                || !preparedSnapshot.ContentHead.EventId.Equals(prepared.Event.PreviousContentEventId.Value))
            {
                throw Conflict(
                    "successor-head-mismatch",
                    $"Prepared predecessor for capsule '{prepared.CapsuleId}' is no longer current.");
            }

            var bytes = CanonicalJson.Serialize(prepared.Event);
            await _transitions.AuthorizeContentTransitionAsync(
                prepared.CapsuleId,
                preparedSnapshot.ContentHead.EventId,
                prepared.Event,
                bytes,
                cancellationToken).ConfigureAwait(false);
            await _capsules.AppendContentAsync(preparedPath, prepared.Event, bytes, cancellationToken)
                .ConfigureAwait(false);
            await _bindings.UpdateContentRootAsync(
                prepared.ItemId,
                prepared.Event.ContentRoot,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<PermalinkPreparedContentSuccessor> ValidatePreparedSuccessors(
        PermalinkOperationDocument operation,
        PermalinkOperationPhase ready)
    {
        var bundle = RequireBundle(operation);
        var successors = ready.PreparedSuccessors
            ?? throw Conflict("prepared-successors-missing", "Ready phase has no exact successor decisions.");
        if (successors.Count != bundle.Claims.Count
            || successors.Select(value => value.CapsuleId).Distinct().Count() != successors.Count)
        {
            throw Conflict("prepared-successors-invalid", "Ready successors do not cover the immutable bundle once.");
        }

        foreach (var claim in bundle.Claims)
        {
            var successor = successors.SingleOrDefault(value => value.CapsuleId.Equals(claim.CapsuleId));
            if (successor is null
                || !successor.ItemId.Equals(claim.ItemId)
                || !string.Equals(successor.Predecessor, claim.Predecessor, StringComparison.Ordinal)
                || !successor.Event.OperationId.Equals(operation.OperationId)
                || !string.Equals(successor.Event.ContentRoot, PermalinkEvidence.BuildManifest(successor.Event.Leaves).ContentRoot, StringComparison.Ordinal))
            {
                throw Conflict("prepared-successors-invalid", "Ready successor evidence does not match its claim.");
            }
        }

        return successors;
    }

    internal static void ValidateReadyFormat(PermalinkOperationPhase ready)
    {
        if (ready.Version >= 3 && ready.PreparedSuccessors is null)
        {
            throw Conflict(
                "prepared-successors-missing",
                "Ready phase declares frozen successor evidence but has no successor decisions.");
        }
    }

    private static void RequireLegacySingleClaim(PermalinkOperationDocument operation)
    {
        var bundle = RequireBundle(operation);
        if (bundle.Claims.Count != 1 || bundle.Claims[0].Kind != "content")
        {
            throw Conflict(
                "prepared-successors-missing",
                "A relation mutation without frozen successors cannot be recovered safely.");
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
