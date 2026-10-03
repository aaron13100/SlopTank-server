// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Admits a scanner batch under the canonical path leases of its new items.</summary>
public interface IPermalinkScanAdmissionService
{
    /// <summary>
    /// Takes every managed candidate path lease in canonical order and identifies candidates that
    /// are currently reserved. The returned admission stays held through library indexing.
    /// </summary>
    /// <param name="candidates">The new filesystem items the scan is about to index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch admission.</returns>
    Task<IPermalinkScanBatchAdmission> BeginNewItemBatchAsync(
        IReadOnlyList<BaseItem> candidates,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes pending-scan evidence left by a crash after repository creation, but only for items
    /// the current filesystem scan rediscovered as already persisted.
    /// </summary>
    /// <param name="rediscoveredItems">Persisted items rediscovered by the current scan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing reconciliation.</returns>
    Task ReconcileRediscoveredItemsAsync(
        IReadOnlyList<BaseItem> rediscoveredItems,
        CancellationToken cancellationToken);
}

/// <summary>A scanner batch's held leases and its per-item admission decisions.</summary>
public interface IPermalinkScanBatchAdmission : IAsyncDisposable
{
    /// <summary>Returns whether the candidate is fenced and must not be indexed in this scan.</summary>
    /// <param name="itemId">The candidate item id.</param>
    /// <returns>True when the item is blocked.</returns>
    bool IsBlocked(Guid itemId);

    /// <summary>Marks pending scan evidence resolved after the given items were indexed.</summary>
    /// <param name="indexedItems">The items successfully passed to the library repository.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing durable completion.</returns>
    Task CompleteAsync(IReadOnlyList<BaseItem> indexedItems, CancellationToken cancellationToken);
}
