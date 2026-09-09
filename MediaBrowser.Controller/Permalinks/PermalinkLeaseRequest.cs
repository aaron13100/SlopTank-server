// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-07-30, 2026-09-09.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Opaque lease body accepted by candidate redemption.</summary>
/// <param name="Lease">The purpose-bound lease.</param>
/// <param name="PlaybackSessionId">The playback session claiming a playback lease.</param>
/// <param name="QueueOrdinal">The frozen queue entry requested by the bound session.</param>
/// <param name="Complete">Whether the last queue entry has finished.</param>
public sealed record PermalinkLeaseRequest(
    string Lease,
    string? PlaybackSessionId = null,
    int QueueOrdinal = 0,
    bool Complete = false);
