namespace MediaBrowser.Model.System;

/// <summary>
/// One bounded subsystem observation. It deliberately carries no path,
/// exception, user, item, or session identifiers.
/// </summary>
public sealed class DeepHealthCheck
{
    /// <summary>
    /// Gets or sets the stable check name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state: ok, degraded, unavailable, or unknown.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable machine-readable reason code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of configured resources observed by this check.
    /// </summary>
    public int? ObservedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of observed resources that failed the check.
    /// </summary>
    public int? AffectedCount { get; set; }
}
