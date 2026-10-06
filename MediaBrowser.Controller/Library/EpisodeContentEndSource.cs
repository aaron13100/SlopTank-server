// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
namespace MediaBrowser.Controller.Library;

/// <summary>
/// Where the main content of an episode ends.
/// </summary>
public enum EpisodeContentEndSource
{
    /// <summary>
    /// Derived from the start of a terminal outro segment.
    /// </summary>
    TerminalOutro,

    /// <summary>
    /// Derived from the configured maximum resume percentage.
    /// </summary>
    RuntimePercentage
}
