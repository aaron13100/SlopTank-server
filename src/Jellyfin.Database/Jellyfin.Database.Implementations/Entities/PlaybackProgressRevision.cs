// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// The server-owned, monotonic playback progress revision of one item for one user.
/// Every write that changes playback progress (position, played, play count, last played)
/// advances it inside the same database transaction as the write. A missing row means
/// revision 0. The row deliberately has no foreign key so it survives an item leaving the
/// library and stays monotonic if the item is restored with the same id.
/// </summary>
public class PlaybackProgressRevision
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    /// <value>The user id.</value>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    /// <value>The item id.</value>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the revision. Starts at 1 for the first recorded write.
    /// </summary>
    /// <value>The revision.</value>
    public long Revision { get; set; }
}
