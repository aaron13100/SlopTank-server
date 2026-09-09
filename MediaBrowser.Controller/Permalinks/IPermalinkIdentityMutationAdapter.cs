// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Encloses identity-bearing Jellyfin entry points in one durable permalink mutation bundle.
/// </summary>
public interface IPermalinkIdentityMutationAdapter
{
    /// <summary>
    /// Executes one mutation after all protected items have durable prepared state.
    /// </summary>
    /// <param name="items">The complete protected item set.</param>
    /// <param name="request">The immutable mutation intent.</param>
    /// <param name="mutation">The live mutation invoked with its ambient operation token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the durable mutation.</returns>
    Task ExecuteAsync(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request,
        Func<IPermalinkMutationAmbientToken, CancellationToken, Task> mutation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the latest pending mutation for an item after verifying or restoring old identity.
    /// </summary>
    /// <param name="item">The protected item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded terminal or suspended operation state.</returns>
    Task<PermalinkMutationResult> CancelPendingAsync(
        BaseItem item,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits one exact pending logical assignment after current identity revalidation.
    /// </summary>
    /// <param name="item">The protected item.</param>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="desiredProviderIds">The exact desired provider assignment.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded committed operation state.</returns>
    Task<PermalinkMutationResult> CommitPendingAsync(
        BaseItem item,
        Guid operationId,
        IReadOnlyDictionary<string, string> desiredProviderIds,
        CancellationToken cancellationToken);
}
