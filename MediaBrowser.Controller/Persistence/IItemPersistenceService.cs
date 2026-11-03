// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Persistence;

/// <summary>
/// Provides item persistence operations (save, delete, update).
/// </summary>
public interface IItemPersistenceService
{
    /// <summary>Refines a generic video's classification without replacing its record or metadata.</summary>
    /// <param name="source">The existing record observed by the library.</param>
    /// <param name="targetType">Movie, Episode or MusicVideo.</param>
    /// <param name="currentPath">The current filename, including a completed container conversion.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The completion of the metadata update.</returns>
    Task RefineVideoKindAsync(Video source, Type targetType, string currentPath, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes items by their IDs.
    /// </summary>
    /// <param name="ids">The IDs to delete.</param>
    void DeleteItem(params IReadOnlyList<Guid> ids);

    /// <summary>
    /// Saves items to the database.
    /// </summary>
    /// <param name="items">The items to save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// Saves image info for an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reattaches user data entries to the correct item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Updates inherited values.
    /// </summary>
    void UpdateInheritedValues();
}
