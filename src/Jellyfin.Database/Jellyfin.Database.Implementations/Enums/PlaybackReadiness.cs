// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
namespace Jellyfin.Database.Implementations.Enums;

/// <summary>
/// Whether a primary playable video has been verified by the compatibility preparation authority.
/// Persisted as an integer column, so the numeric values are a stable contract: never renumber or
/// reuse one.
/// </summary>
public enum PlaybackReadiness
{
    /// <summary>
    /// The row predates the readiness gate, or was explicitly verified ready by the legacy path.
    /// Existing rows retain this value during the additive migration.
    /// </summary>
    Ready = 0,

    /// <summary>
    /// The item is awaiting compatibility verification or preparation.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Compatibility verification or preparation failed and may be retried.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The item has not yet been classified by the readiness contract.
    /// </summary>
    Unknown = 3
}
