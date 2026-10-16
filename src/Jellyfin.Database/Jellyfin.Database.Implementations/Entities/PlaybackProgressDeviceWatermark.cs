// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// The receipt retention watermark of one device of one user. Receipts with a sequence at or
/// below <see cref="CompactedThroughSequence"/> have been deleted; an operation in that range
/// without a receipt is refused as expired so that compaction can never reopen a replay.
/// </summary>
public class PlaybackProgressDeviceWatermark
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    /// <value>The user id.</value>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the device id.
    /// </summary>
    /// <value>The device id.</value>
    public required string DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the highest sequence whose receipt has been compacted away (0 if none).
    /// </summary>
    /// <value>The compaction watermark.</value>
    public long CompactedThroughSequence { get; set; }
}
