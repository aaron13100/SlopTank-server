// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// One offline playback progress operation: a conditional, idempotent update of an item's
    /// playback progress that applies only if the server revision still equals
    /// <see cref="BaseRevision"/>.
    /// </summary>
    public class PlaybackProgressOperationDto
    {
        /// <summary>
        /// Gets or sets the client-generated operation id. Every retry of the same payload reuses it.
        /// </summary>
        /// <value>The operation id.</value>
        public Guid OperationId { get; set; }

        /// <summary>
        /// Gets or sets the per-device operation sequence number (strictly increasing, starting at 1).
        /// </summary>
        /// <value>The sequence number.</value>
        public long Sequence { get; set; }

        /// <summary>
        /// Gets or sets the id of the server the client believes it is talking to.
        /// </summary>
        /// <value>The server id.</value>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the client's device id; it must equal the authenticated session's device id.
        /// </summary>
        /// <value>The device id.</value>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the revision the client last acknowledged before recording this operation offline.
        /// </summary>
        /// <value>The baseline revision.</value>
        public long BaseRevision { get; set; }

        /// <summary>
        /// Gets or sets the resulting playback position in ticks.
        /// </summary>
        /// <value>The position.</value>
        public long PositionTicks { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the item is played afterwards.
        /// </summary>
        /// <value>The played state.</value>
        public bool Played { get; set; }

        /// <summary>
        /// Gets or sets the number of completed playbacks recorded offline (0 to 100).
        /// </summary>
        /// <value>The play count increment.</value>
        public int PlayCountIncrement { get; set; }

        /// <summary>
        /// Gets or sets the local last played date (UTC), if any.
        /// </summary>
        /// <value>The last played date.</value>
        public DateTime? LastPlayedDate { get; set; }
    }
}
