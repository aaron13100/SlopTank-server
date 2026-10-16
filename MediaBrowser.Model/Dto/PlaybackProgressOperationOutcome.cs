// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// The outcome of an offline playback progress operation.
    /// </summary>
    public enum PlaybackProgressOperationOutcome
    {
        /// <summary>
        /// The operation's baseline matched and its playback change was committed.
        /// </summary>
        Applied = 1,

        /// <summary>
        /// The server playback state changed after the operation's baseline; nothing was written.
        /// </summary>
        Conflict = 2,

        /// <summary>
        /// The operation id or sequence was already used with a different payload; nothing was written.
        /// </summary>
        PayloadMismatch = 3,

        /// <summary>
        /// The operation's sequence is at or below the device's compacted receipt range; nothing was written.
        /// </summary>
        Expired = 4,

        /// <summary>
        /// The user already has the maximum number of devices holding receipts; nothing was written.
        /// </summary>
        CapacityExceeded = 5
    }
}
