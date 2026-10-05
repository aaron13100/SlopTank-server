// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Watchlist;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Manages the per-user watchlist ("My List") rows in jellyfin.db. Membership
/// rows deliberately have no foreign keys and no cascade behavior: an entry
/// outlives the item leaving the library and becomes resolvable again as soon
/// as an item with the same id is restored by an ordinary rescan.
/// </summary>
public class WatchlistManager : IWatchlistManager
{
    private const string UniqueIndexName = "UX_UserWatchlists_UserId_ItemId";

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WatchlistManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistManager"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistManager(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        ILibraryManager libraryManager,
        ILogger<WatchlistManager> logger)
    {
        _dbProvider = dbProvider;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddToWatchlistAsync(Guid userId, Guid itemId)
    {
        using var context = _dbProvider.CreateDbContext();

        // Fast path: the row is already present, keep the original AddedAtUtc.
        var exists = await context.UserWatchlists
            .AnyAsync(entry => entry.UserId.Equals(userId) && entry.ItemId.Equals(itemId))
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        var entry = new UserWatchlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = itemId,
            AddedAtUtc = DateTime.UtcNow
        };
        context.UserWatchlists.Add(entry);
        try
        {
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Two concurrent adds race past the existence check; the unique
            // index decides. First writer wins and keeps its AddedAtUtc, the
            // loser resolves to the same idempotent outcome. Any other
            // database failure keeps its full context and propagates.
            var stillAbsent = !await context.UserWatchlists
                .AnyAsync(row => row.UserId.Equals(userId) && row.ItemId.Equals(itemId))
                .ConfigureAwait(false);
            if (stillAbsent)
            {
                _logger.LogError(ex, "Watchlist add for user {UserId}, item {ItemId} failed and the row is still absent", userId, itemId);
                throw;
            }

            _logger.LogDebug(
                ex,
                "Concurrent watchlist add for user {UserId}, item {ItemId} resolved by unique index {UniqueIndexName}",
                userId,
                itemId,
                UniqueIndexName);
        }
    }

    /// <inheritdoc />
    public async Task RemoveFromWatchlistAsync(Guid userId, Guid itemId)
    {
        using var context = _dbProvider.CreateDbContext();
        await context.UserWatchlists
            .Where(entry => entry.UserId.Equals(userId) && entry.ItemId.Equals(itemId))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsInWatchlistAsync(Guid userId, Guid itemId)
    {
        using var context = _dbProvider.CreateDbContext();
        return await context.UserWatchlists
            .AnyAsync(entry => entry.UserId.Equals(userId) && entry.ItemId.Equals(itemId))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<QueryResult<BaseItem>> GetWatchlistAsync(User user, int? startIndex, int? limit, SortOrder sortOrder)
    {
        using var context = _dbProvider.CreateDbContext();

        // A personal server's watchlist is small (hundreds at most), so the
        // whole list is read and resolved once: filtering dangling or
        // user-invisible rows at the database page level would return
        // undersized pages and a TotalRecordCount that lies about the list
        // the user actually sees. Page boundaries are applied over the
        // resolved, visible entries instead.
        var ordered = sortOrder == SortOrder.Ascending
            ? context.UserWatchlists
                .Where(entry => entry.UserId.Equals(user.Id))
                .OrderBy(entry => entry.AddedAtUtc).ThenBy(entry => entry.Id)
            : context.UserWatchlists
                .Where(entry => entry.UserId.Equals(user.Id))
                .OrderByDescending(entry => entry.AddedAtUtc).ThenBy(entry => entry.Id);

        var rows = await ordered
            .Select(entry => entry.ItemId)
            .ToListAsync()
            .ConfigureAwait(false);

        var items = new List<BaseItem>(rows.Count);
        foreach (var itemId in rows)
        {
            // Null means the item is gone from the library or hidden from
            // this user; the row itself is preserved for a later restore.
            var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                _logger.LogDebug("Watchlist row for user {UserId}, item {ItemId} has no resolvable item; filtered from the read", user.Id, itemId);
            }
        }

        var total = items.Count;
        var from = Math.Clamp(startIndex ?? 0, 0, total);
        var count = limit.HasValue ? Math.Clamp(limit.Value, 0, total - from) : total - from;
        var page = items.Skip(from).Take(count).ToList();
        return new QueryResult<BaseItem>(from, total, page);
    }
}
