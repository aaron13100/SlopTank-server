// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// The durable, immutable outcome of one offline playback progress operation. It is written
/// in the same transaction as the playback change it records (or as the refusal, for a
/// conflict), so a retried operation returns this outcome instead of writing again.
/// </summary>
public class PlaybackProgressReceipt
{
    /// <summary>
    /// Gets or sets the authenticated user id.
    /// </summary>
    /// <value>The user id.</value>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the authenticated device id.
    /// </summary>
    /// <value>The device id.</value>
    public required string DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the client-generated operation id.
    /// </summary>
    /// <value>The operation id.</value>
    public Guid OperationId { get; set; }

    /// <summary>
    /// Gets or sets the per-device operation sequence number.
    /// </summary>
    /// <value>The sequence number.</value>
    public long Sequence { get; set; }

    /// <summary>
    /// Gets or sets the item id the operation targeted.
    /// </summary>
    /// <value>The item id.</value>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 hash (lowercase hex) of the canonical operation payload.
    /// </summary>
    /// <value>The payload hash.</value>
    public required string PayloadHash { get; set; }

    /// <summary>
    /// Gets or sets the recorded outcome: 1 for applied, 2 for conflict.
    /// </summary>
    /// <value>The outcome.</value>
    public int Outcome { get; set; }

    /// <summary>
    /// Gets or sets the playback revision after the operation (the current revision for a conflict).
    /// </summary>
    /// <value>The resulting revision.</value>
    public long ResultRevision { get; set; }

    /// <summary>
    /// Gets or sets the resulting playback position in ticks.
    /// </summary>
    /// <value>The position.</value>
    public long ResultPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item was played afterwards.
    /// </summary>
    /// <value>The played state.</value>
    public bool ResultPlayed { get; set; }

    /// <summary>
    /// Gets or sets the resulting play count.
    /// </summary>
    /// <value>The play count.</value>
    public int ResultPlayCount { get; set; }

    /// <summary>
    /// Gets or sets the resulting last played date.
    /// </summary>
    /// <value>The last played date.</value>
    public DateTime? ResultLastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant the receipt was written.
    /// </summary>
    /// <value>The creation instant.</value>
    public DateTime CreatedAtUtc { get; set; }
}
