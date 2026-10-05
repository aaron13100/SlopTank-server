// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;

namespace MediaBrowser.Controller.Watchlist;

/// <summary>
/// Manages a user's watchlist ("My List"): a per-user, idempotent record of
/// library items to watch. Membership is independent of Favorites, Playlists,
/// Collections, and playback progress; completing playback never removes an
/// entry.
/// </summary>
public interface IWatchlistManager
{
    /// <summary>
    /// Adds an item to the user's watchlist. Adding an entry that already
    /// exists leaves the list unchanged (idempotent) and keeps the original
    /// addition timestamp.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddToWatchlistAsync(Guid userId, Guid itemId);

    /// <summary>
    /// Removes an item from the user's watchlist. Removing an entry that does
    /// not exist leaves the list unchanged (idempotent).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RemoveFromWatchlistAsync(Guid userId, Guid itemId);

    /// <summary>
    /// Checks whether an item is currently in the user's watchlist.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item is on the user's list; otherwise <c>false</c>.</returns>
    Task<bool> IsInWatchlistAsync(Guid userId, Guid itemId);

    /// <summary>
    /// Gets the user's watchlist as resolvable library items, newest-added
    /// first unless another order is requested. Rows whose item has left the
    /// library (or that the user cannot see) are filtered out but preserved,
    /// so an item restored with the same id reappears automatically.
    /// </summary>
    /// <param name="user">The user whose list to read.</param>
    /// <param name="startIndex">The zero-based index of the first entry to return, or <c>null</c> for the start.</param>
    /// <param name="limit">The maximum number of entries to return, or <c>null</c> for no limit.</param>
    /// <param name="sortOrder">The ordering of <see cref="UserWatchlist.AddedAtUtc"/>.</param>
    /// <returns>A paged result of resolvable items.</returns>
    Task<QueryResult<BaseItem>> GetWatchlistAsync(User user, int? startIndex, int? limit, SortOrder sortOrder);
}
