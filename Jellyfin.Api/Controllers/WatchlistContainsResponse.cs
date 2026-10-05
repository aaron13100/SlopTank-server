// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;

namespace Jellyfin.Api.Controllers;

/// <summary>Watchlist membership answer for one item.</summary>
/// <param name="ItemId">The item that was checked.</param>
/// <param name="IsInWatchlist">Whether the item is on the requesting user's watchlist.</param>
public sealed record WatchlistContainsResponse(
    Guid ItemId,
    bool IsInWatchlist);
