// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;

namespace Jellyfin.Api.Models.MediaSegmentDtos;

/// <summary>
/// The canonical content-completion boundary of an episode, shared by watched
/// state (server) and the Up Next prompt (client) (c189).
/// </summary>
public sealed class EpisodeContentEndDto
{
    /// <summary>
    /// Gets or sets the id of the episode.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the position in ticks at which the episode's content is complete.
    /// Null when no boundary could be computed; clients fall back to their own default.
    /// </summary>
    public long? ContentEndTicks { get; set; }

    /// <summary>
    /// Gets or sets how the boundary was derived: "TerminalOutro" or "RuntimePercentage".
    /// Null when no boundary could be computed.
    /// </summary>
    public string? Source { get; set; }
}
