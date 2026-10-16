// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;

namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// The result of an offline playback progress operation.
    /// </summary>
    public class PlaybackProgressOperationResultDto
    {
        /// <summary>
        /// Gets or sets the outcome.
        /// </summary>
        /// <value>The outcome.</value>
        public PlaybackProgressOperationOutcome Outcome { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is the recorded outcome of an earlier,
        /// identical request (no write happened for this request).
        /// </summary>
        /// <value>Whether the outcome was replayed from a receipt.</value>
        public bool Replayed { get; set; }

        /// <summary>
        /// Gets or sets the operation id.
        /// </summary>
        /// <value>The operation id.</value>
        public Guid OperationId { get; set; }

        /// <summary>
        /// Gets or sets the resulting (or, for a refusal, current) playback state, if known.
        /// </summary>
        /// <value>The state.</value>
        public PlaybackProgressStateDto? State { get; set; }
    }
}
