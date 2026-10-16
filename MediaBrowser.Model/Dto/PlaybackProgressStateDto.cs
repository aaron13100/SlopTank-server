// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// The playback progress of one item for one user together with its server-owned revision.
    /// </summary>
    public class PlaybackProgressStateDto
    {
        /// <summary>
        /// Gets or sets the item id.
        /// </summary>
        /// <value>The item id.</value>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the id of the server that owns this state.
        /// </summary>
        /// <value>The server id.</value>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the playback progress revision. 0 means no playback write was recorded yet.
        /// </summary>
        /// <value>The revision.</value>
        public long Revision { get; set; }

        /// <summary>
        /// Gets or sets the playback position in ticks.
        /// </summary>
        /// <value>The position.</value>
        public long PositionTicks { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the item is played.
        /// </summary>
        /// <value>The played state.</value>
        public bool Played { get; set; }

        /// <summary>
        /// Gets or sets the play count.
        /// </summary>
        /// <value>The play count.</value>
        public int PlayCount { get; set; }

        /// <summary>
        /// Gets or sets the last played date.
        /// </summary>
        /// <value>The last played date.</value>
        public DateTime? LastPlayedDate { get; set; }
    }
}
