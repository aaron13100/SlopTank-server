// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
namespace MediaBrowser.Controller.Library;

/// <summary>
/// The canonical episode content-completion boundary shared by watched state (server)
/// and the Up Next prompt (client), so both agree on when an episode is done (c189).
/// </summary>
/// <param name="ContentEndTicks">Position in ticks at which the episode's content is complete.</param>
/// <param name="Source">How the boundary was derived.</param>
public sealed record EpisodeContentEnd(long ContentEndTicks, EpisodeContentEndSource Source);
