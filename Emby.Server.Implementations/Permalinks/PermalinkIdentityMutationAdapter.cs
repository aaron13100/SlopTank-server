using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Adapts real Jellyfin mutation entry points to the durable permalink mutation coordinator.
/// </summary>
internal sealed class PermalinkIdentityMutationAdapter : IPermalinkIdentityMutationAdapter
{
    private static readonly AsyncLocal<AmbientToken?> _ambient = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();
    private readonly IPermalinkMutationCoordinator _coordinator;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkPendingIdentityMutation _pending;
    private readonly ILibraryManager _libraryManager;

    public PermalinkIdentityMutationAdapter(
        IPermalinkMutationCoordinator coordinator,
        PermalinkBindingIndex bindings,
        PermalinkPendingIdentityMutation pending,
        ILibraryManager libraryManager)
    {
        _coordinator = coordinator;
        _bindings = bindings;
        _pending = pending;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request,
        Func<IPermalinkMutationAmbientToken, CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutation);

        if (_ambient.Value is { } ambient)
        {
            await mutation(ambient, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mutationItems = ExpandMutationItems(items, request);
        if (mutationItems.Count == 0)
        {
            await mutation(new AmbientToken([]), cancellationToken).ConfigureAwait(false);
            return;
        }

        var effectiveKind = ResolveEffectiveKind(mutationItems, request, out var promotedItem);
        if (promotedItem is not null
            && mutationItems.All(value => !value.Id.Equals(promotedItem.Id)))
        {
            mutationItems = [.. mutationItems, promotedItem];
        }

        var locks = mutationItems
            .OrderBy(value => value.Id)
            .Select(value => _itemLocks.GetOrAdd(value.Id, _ => new SemaphoreSlim(1, 1)))
            .ToArray();
        var acquiredLockCount = 0;
        try
        {
            foreach (var itemLock in locks)
            {
                await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquiredLockCount++;
            }

            var preparedItems = await PrepareProtectedItemsAsync(
                mutationItems,
                request,
                effectiveKind,
                promotedItem,
                cancellationToken).ConfigureAwait(false);
            await PublishPromotionBindingsAsync(
                mutationItems[0],
                effectiveKind,
                promotedItem,
                cancellationToken).ConfigureAwait(false);
            await InvokeMutationAsync(
                preparedItems,
                effectiveKind,
                mutation,
                cancellationToken).ConfigureAwait(false);
            await PublishMutationAsync(
                preparedItems,
                effectiveKind,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            for (var index = acquiredLockCount - 1; index >= 0; index--)
            {
                locks[index].Release();
            }
        }
    }

    private async Task<IReadOnlyList<PreparedItem>> PrepareProtectedItemsAsync(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request,
        string effectiveKind,
        BaseItem? promotedItem,
        CancellationToken cancellationToken)
    {
        var result = new List<PreparedItem>();
        foreach (var item in items)
        {
            var isProtected = await _bindings.HasItemBindingAsync(item.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!isProtected && !(effectiveKind == "promotion" && item == promotedItem))
            {
                continue;
            }

            RejectProtectedKindChange(item, items[0], request);
            var pendingOperation = await _pending.FindPendingAsync(item.Id, cancellationToken)
                .ConfigureAwait(false);
            if (pendingOperation is not null)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "identity-mutation-pending",
                    $"Item '{item.Id}' is fenced by unfinished durable operation '{pendingOperation.OperationId}'.",
                    operationId: pendingOperation.OperationId,
                    itemId: item.Id);
            }

            var operationId = Guid.NewGuid();
            await _coordinator.PrepareAsync(
                item,
                new PermalinkMutationPrepareRequest(
                    operationId,
                    item.Id,
                    effectiveKind,
                    null,
                    item == items[0] ? request.DesiredProviderIds : null),
                cancellationToken).ConfigureAwait(false);
            result.Add(new PreparedItem(item, operationId));
        }

        return result;
    }

    private async Task InvokeMutationAsync(
        IReadOnlyList<PreparedItem> preparedItems,
        string effectiveKind,
        Func<IPermalinkMutationAmbientToken, CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        var token = new AmbientToken(preparedItems.Select(value => value.OperationId).ToArray());
        try
        {
            _ambient.Value = token;
            await mutation(token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (IsRestorableIdentityMutation(effectiveKind))
            {
                foreach (var preparedItem in preparedItems)
                {
                    await _pending.RestoreAndCancelAsync(
                        preparedItem.Item,
                        preparedItem.OperationId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var preparedItem in preparedItems)
                {
                    await _coordinator.AbortAsync(
                        preparedItem.OperationId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            throw;
        }
        finally
        {
            _ambient.Value = null;
        }
    }

    private async Task PublishMutationAsync(
        IReadOnlyList<PreparedItem> preparedItems,
        string effectiveKind,
        CancellationToken cancellationToken)
    {
        if (effectiveKind == "deletion")
        {
            foreach (var preparedItem in preparedItems)
            {
                await _bindings.RemoveItemBindingsAsync(
                    preparedItem.Item.Id,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var preparedItem in preparedItems)
        {
            await _coordinator.CommitAsync(
                preparedItem.OperationId,
                new PermalinkMutationCommitRequest(
                    null,
                    new Dictionary<string, string>(
                        preparedItem.Item.ProviderIds,
                        StringComparer.OrdinalIgnoreCase)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishPromotionBindingsAsync(
        BaseItem primaryItem,
        string effectiveKind,
        BaseItem? promotedItem,
        CancellationToken cancellationToken)
    {
        if (effectiveKind == "promotion" && promotedItem is not null)
        {
            await _bindings.PromoteItemBindingsAsync(
                primaryItem.Id,
                promotedItem.Id,
                promotedItem.Path,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RejectProtectedKindChange(
        BaseItem item,
        BaseItem primaryItem,
        PermalinkIdentityMutationRequest request)
    {
        if (request.DesiredItemKind is not null
            && request.Kind != "kind-reclassification"
            && item == primaryItem
            && !string.Equals(
                request.DesiredItemKind,
                item.GetBaseItemKind().ToString(),
                StringComparison.Ordinal))
        {
            throw Conflict(
                "item-kind-immutable",
                $"Protected item kind '{item.GetBaseItemKind()}' cannot change to '{request.DesiredItemKind}'.");
        }
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CancelPendingAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        return await _pending.CancelAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CommitPendingAsync(
        BaseItem item,
        Guid operationId,
        IReadOnlyDictionary<string, string> desiredProviderIds,
        CancellationToken cancellationToken)
    {
        return await _pending.CommitAsync(
            item,
            operationId,
            desiredProviderIds,
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<BaseItem> ExpandMutationItems(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request)
    {
        var expanded = items
            .Where(value => value is not null)
            .DistinctBy(value => value.Id)
            .ToList();
        if (request.Kind is "deletion" or "manual-metadata")
        {
            expanded.AddRange(items
                .OfType<Folder>()
                .SelectMany(value => value.GetRecursiveChildren()));
        }

        return expanded
            .DistinctBy(value => value.Id)
            .ToArray();
    }

    private string ResolveEffectiveKind(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request,
        out BaseItem? promotedItem)
    {
        promotedItem = null;
        if (request.Kind == "promotion")
        {
            promotedItem = items.Skip(1).FirstOrDefault();
            return request.Kind;
        }

        var primary = items.FirstOrDefault() as Video;
        if (request.Kind != "deletion"
            || primary is null
            || primary.PrimaryVersionId.HasValue
            || !primary.OwnerId.Equals(Guid.Empty))
        {
            return request.Kind;
        }

        promotedItem = _libraryManager.GetLocalAlternateVersionIds(primary)
            .Concat(_libraryManager.GetLinkedAlternateVersions(primary).Select(value => value.Id))
            .Distinct()
            .Select(value => _libraryManager.GetItemById(value))
            .FirstOrDefault(value => value is Video);
        return promotedItem is null ? request.Kind : "promotion";
    }

    private static bool IsRestorableIdentityMutation(string kind)
    {
        return kind is "logical" or "identify" or "manual-metadata" or "automatic-refresh" or "nfo-refresh";
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    private sealed record AmbientToken(
        IReadOnlyList<Guid> OperationIds) : IPermalinkMutationAmbientToken;

    private sealed record PreparedItem(BaseItem Item, Guid OperationId);
}
