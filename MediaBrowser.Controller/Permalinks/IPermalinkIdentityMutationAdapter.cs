// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-09-19, 2026-10-08.
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
    /// Returns whether an already-resolved scan child is provably identical at every
    /// permalink-bearing boundary and its persisted content digest still matches the
    /// filesystem change token.
    /// </summary>
    /// <param name="currentItem">The item currently persisted in the library.</param>
    /// <param name="resolvedItem">The item resolved by the current scan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True only when durable evidence proves identity work is redundant.</returns>
    Task<bool> CanSkipScanReplacementAsync(
        BaseItem currentItem,
        BaseItem resolvedItem,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether an existing occupant at a resolved child's path is the durable
    /// adopter of that exact path generation, so a scan must keep the occupant (its id,
    /// UserData, images, and readiness) instead of removing it and birthing a fresh row.
    /// This is the scan-side half of a sanctioned path adoption: the commit already
    /// repointed the occupant to a new physical generation whose path-derived id can
    /// never equal the occupant's, and without this check every later scan churns the
    /// row (delete plus recreate), losing identity and re-identification metadata.
    /// </summary>
    /// <param name="occupant">The persisted item currently occupying the child's path.</param>
    /// <param name="resolvedChild">The item the current scan resolved from the filesystem.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// True only when the authority binding index records the occupant as bound at the
    /// child's exact current path and the object at that path carries that binding's
    /// anchor token. The authority record is the authorizer; anchor equality alone
    /// authorizes nothing, so an unbound or divergent occupant is never adopted.
    /// </returns>
    Task<bool> ShouldAdoptOccupantAsync(
        BaseItem occupant,
        BaseItem resolvedChild,
        CancellationToken cancellationToken);

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
