// SlopTank modification notice: added or changed by SlopTank on 2026-09-14, 2026-09-18.
namespace MediaBrowser.Model.Entities
{
    /// <summary>
    /// Enum UserDataSaveReason.
    /// </summary>
    public enum UserDataSaveReason
    {
        /// <summary>
        /// The playback start.
        /// </summary>
        PlaybackStart = 1,

        /// <summary>
        /// The playback progress.
        /// </summary>
        PlaybackProgress = 2,

        /// <summary>
        /// The playback finished.
        /// </summary>
        PlaybackFinished = 3,

        /// <summary>
        /// The toggle played.
        /// </summary>
        TogglePlayed = 4,

        /// <summary>
        /// The update user rating.
        /// </summary>
        UpdateUserRating = 5,

        /// <summary>
        /// The import.
        /// </summary>
        Import = 6,

        /// <summary>
        /// API call updated item user data.
        /// </summary>
        UpdateUserData = 7,

        /// <summary>
        /// The user removed the item from their Continue Watching rail by
        /// clearing only the resume position; watched state, play count and
        /// ratings are deliberately untouched.
        /// </summary>
        ResumeCleared = 8,
    }
}
