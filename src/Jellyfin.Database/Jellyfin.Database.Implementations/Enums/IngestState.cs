// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
namespace Jellyfin.Database.Implementations.Enums;

/// <summary>
/// How far a library item has progressed through ingest. Persisted as an integer column on BaseItems,
/// so the numeric values are a stable contract: never renumber or reuse one.
/// </summary>
public enum IngestState
{
    /// <summary>
    /// The item is fully ingested (or does not need a media probe). Every pre-existing row is Complete.
    /// </summary>
    Complete = 0,

    /// <summary>
    /// The item row exists but its media has not been probed yet. Hidden from user-facing listings.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The media probe failed. Hidden from user-facing listings and re-probed on every scan.
    /// </summary>
    ProbeFailed = 2
}
