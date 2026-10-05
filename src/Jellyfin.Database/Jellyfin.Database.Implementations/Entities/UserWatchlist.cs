// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// One entry of a user's watchlist ("My List"). Membership is per user and
/// per item, is idempotent, and survives item removal: rows are never
/// cascade-deleted when the underlying item leaves the library, and become
/// visible again automatically if the item is restored with the same id.
/// </summary>
public class UserWatchlist
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    /// <value>The row id.</value>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the id of the user that owns this entry.
    /// </summary>
    /// <value>The user id.</value>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the id of the watched-list item.
    /// </summary>
    /// <value>The item id.</value>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant the item was added to the list. This is
    /// also the row creation timestamp.
    /// </summary>
    /// <value>The added timestamp (UTC).</value>
    public DateTime AddedAtUtc { get; set; }
}
