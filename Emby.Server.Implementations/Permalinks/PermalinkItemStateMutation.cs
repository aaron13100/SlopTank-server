using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Completes and recovers journal-only item-state mutations.
/// </summary>
internal sealed class PermalinkItemStateMutation
{
    private readonly PermalinkOperationJournal _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkItemStateMutation"/> class.
    /// </summary>
    /// <param name="journal">The durable mutation journal.</param>
    public PermalinkItemStateMutation(PermalinkOperationJournal journal)
    {
        _journal = journal;
    }

    /// <summary>
    /// Gets a value indicating whether the kind changes an item's persisted identity.
    /// </summary>
    /// <param name="kind">The mutation kind.</param>
    /// <returns><see langword="true"/> when recovery must validate or restore item identity.</returns>
    public static bool IsIdentityKind(string kind)
    {
        return kind is "logical"
            or "identify"
            or "manual-metadata"
            or "automatic-refresh"
            or "nfo-refresh"
            or "scan-replacement";
    }

    /// <summary>
    /// Commits a completed item-identity mutation.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="item">The item whose identity changed.</param>
    /// <param name="request">The commit evidence supplied by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The committed mutation result.</returns>
    public async Task<PermalinkMutationResult> CommitIdentityAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var desired = request.DesiredProviderIds ?? operation.DesiredProviderIds;
        if (!ProviderIdsEqual(item.ProviderIds, desired))
        {
            throw Conflict(
                "desired-assignment-mismatch",
                "Live provider assignment does not match desired state.");
        }

        await CompleteAsync(operation, operation.OldContentRoot, cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
    }

    /// <summary>
    /// Commits a mutation whose durable journal is its only recovery state.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The committed mutation result.</returns>
    public async Task<PermalinkMutationResult> CommitStateOnlyAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        await CompleteAsync(operation, operation.OldContentRoot, cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
    }

    /// <summary>
    /// Recovers an interrupted item-identity mutation from its durable phase boundary.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="item">The item whose identity may need restoration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recovered terminal mutation result.</returns>
    public async Task<PermalinkMutationResult> RecoverIdentityAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false);
        if (ready is not null)
        {
            await CompleteAsync(operation, ready.ContentRoot, cancellationToken)
                .ConfigureAwait(false);
            return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
        }

        if (_journal.HasPhase(operation.OperationId, PermalinkPhase.Published))
        {
            return await CloseAssignmentUnknownAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        await RestorePreparedOldAsync(item, operation, cancellationToken).ConfigureAwait(false);
        return await CancelAsync(operation, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers a journal-only mutation from its durable phase boundary.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recovered terminal mutation result.</returns>
    public async Task<PermalinkMutationResult> RecoverStateOnlyAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false);
        if (ready is not null)
        {
            await CompleteAsync(operation, ready.ContentRoot, cancellationToken)
                .ConfigureAwait(false);
            return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
        }

        if (_journal.HasPhase(operation.OperationId, PermalinkPhase.Published))
        {
            return await CloseAssignmentUnknownAsync(operation, item: null, cancellationToken)
                .ConfigureAwait(false);
        }

        await WritePhaseIfMissingAsync(
            operation,
            PermalinkPhase.Aborted,
            operation.OldContentRoot,
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Aborted.Name);
    }

    /// <summary>
    /// Cancels a prepared identity mutation after verifying the restored assignment.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="item">The item whose restored identity is validated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cancelled mutation result.</returns>
    public async Task<PermalinkMutationResult> CancelAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var oldIdentity = operation.OldIdentity;
        if (oldIdentity is not null
            ? !oldIdentity.Matches(item)
            : !ProviderIdsEqual(item.ProviderIds, operation.OldProviderIds))
        {
            throw Conflict(
                "prepared-old-mismatch",
                "Live identity does not equal the complete prepared-old assignment.");
        }

        await WritePhaseIfMissingAsync(
            operation,
            PermalinkPhase.Cancelled,
            operation.OldContentRoot,
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Cancelled.Name);
    }

    /// <summary>
    /// Restores the complete item identity captured at prepare time.
    /// </summary>
    /// <param name="item">The item to restore.</param>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RestorePreparedOldAsync(
        BaseItem item,
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        if (operation.OldIdentity is { } snapshot)
        {
            snapshot.Restore(item);
        }
        else
        {
            item.ProviderIds = new Dictionary<string, string>(
                operation.OldProviderIds,
                StringComparer.OrdinalIgnoreCase);
        }

        await item.UpdateToRepositoryAsync(
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteAsync(
        PermalinkOperationDocument operation,
        string? contentRoot,
        CancellationToken cancellationToken)
    {
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Ready, contentRoot, cancellationToken)
            .ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Published, contentRoot, cancellationToken)
            .ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Committed, contentRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Closes an operation whose durable evidence is partial: it published something, but its ready
    /// boundary is absent, so neither the exact prepared-old nor the exact desired assignment can be
    /// reconstructed. The record snapshots what was observed and asserts nothing about it: it claims
    /// no content, detaches nothing, removes no binding and permits no remint. Throwing here instead
    /// left the operation non-terminal, which fenced the item out of playback and out of every later
    /// scan indefinitely.
    /// </summary>
    /// <param name="operation">The immutable prepared operation.</param>
    /// <param name="item">The live item when one is in scope, otherwise null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal mutation result.</returns>
    private async Task<PermalinkMutationResult> CloseAssignmentUnknownAsync(
        PermalinkOperationDocument operation,
        BaseItem? item,
        CancellationToken cancellationToken)
    {
        // The snapshot is observed rather than derived from the immutable operation, so re-writing
        // it later could produce different bytes and fail the create-exclusive byte comparison as
        // operation-id-reused, re-fencing the item this phase just released. Write it once.
        if (!_journal.HasPhase(operation.OperationId, PermalinkPhase.AssignmentUnknown))
        {
            await _journal.WritePhaseAsync(
                operation.OperationId,
                PermalinkPhase.AssignmentUnknown,
                new PermalinkOperationPhase(
                    operation.OperationId,
                    PermalinkPhase.AssignmentUnknown.Name,
                    operation.OldContentRoot,
                    operation.CreatedAt)
                {
                    ObservedState = DescribeObservedState(operation, item)
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new PermalinkMutationResult(
            operation.OperationId,
            PermalinkPhase.AssignmentUnknown.Name);
    }

    private static string DescribeObservedState(
        PermalinkOperationDocument operation,
        BaseItem? item)
    {
        return "ready=missing;published=present"
            + $";live={DescribeAssignment(item?.ProviderIds)}"
            + $";old={DescribeAssignment(operation.OldProviderIds)}"
            + $";desired={DescribeAssignment(operation.DesiredProviderIds)}";
    }

    private static string DescribeAssignment(IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null)
        {
            return "unavailable";
        }

        return providerIds.Count == 0
            ? "none"
            : string.Join(
                ',',
                providerIds
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private async Task WritePhaseIfMissingAsync(
        PermalinkOperationDocument operation,
        PermalinkPhase phase,
        string? contentRoot,
        CancellationToken cancellationToken)
    {
        if (_journal.HasPhase(operation.OperationId, phase))
        {
            return;
        }

        await _journal.WritePhaseAsync(
            operation.OperationId,
            phase,
            new PermalinkOperationPhase(
                operation.OperationId,
                phase.Name,
                contentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool ProviderIdsEqual(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> desired)
    {
        return current.Count == desired.Count
            && current.All(pair => desired.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
