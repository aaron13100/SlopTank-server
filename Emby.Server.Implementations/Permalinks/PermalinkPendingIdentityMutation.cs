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
/// Owns validation, rollback, and completion of already-prepared identity mutations.
/// </summary>
internal sealed class PermalinkPendingIdentityMutation
{
    private readonly IPermalinkMutationCoordinator _coordinator;
    private readonly PermalinkOperationJournal _journal;

    public PermalinkPendingIdentityMutation(
        IPermalinkMutationCoordinator coordinator,
        PermalinkOperationJournal journal)
    {
        _coordinator = coordinator;
        _journal = journal;
    }

    public Task<bool> HasPendingAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return _journal.HasPendingAsync(itemId, cancellationToken);
    }

    public async Task<PermalinkMutationResult> CancelAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var operation = await _journal.FindLatestPendingAsync(item.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict(
                "pending-assignment-missing",
                $"Item '{item.Id}' has no pending permalink identity mutation.");
        if (!string.Equals(operation.Kind, "logical", StringComparison.Ordinal))
        {
            throw Conflict(
                "pending-assignment-kind",
                $"Item '{item.Id}' has pending '{operation.Kind}' work, not a logical reassignment.");
        }

        await RestorePreparedOldAsync(item, operation, cancellationToken).ConfigureAwait(false);
        return await _coordinator.CancelAsync(operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PermalinkMutationResult> CommitAsync(
        BaseItem item,
        Guid operationId,
        IReadOnlyDictionary<string, string> desiredProviderIds,
        CancellationToken cancellationToken)
    {
        var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null
            || !operation.ItemId.Equals(item.Id)
            || !string.Equals(operation.Kind, "logical", StringComparison.Ordinal))
        {
            throw Conflict(
                "pending-assignment-missing",
                $"Operation '{operationId}' is not pending for item '{item.Id}'.");
        }

        if (!ProviderIdsEqual(item.ProviderIds, desiredProviderIds))
        {
            throw Conflict(
                "desired-assignment-mismatch",
                "Current provider assignment does not match the requested confirmation.");
        }

        return await _coordinator.CommitAsync(
            operationId,
            new PermalinkMutationCommitRequest(null, desiredProviderIds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAndCancelAsync(
        BaseItem item,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        await RestorePreparedOldAsync(item, operation, cancellationToken).ConfigureAwait(false);
        await _coordinator.CancelAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestorePreparedOldAsync(
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
