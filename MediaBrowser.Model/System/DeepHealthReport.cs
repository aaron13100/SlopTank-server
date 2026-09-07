using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.System;

/// <summary>
/// A named, bounded health snapshot for independently observable subsystems.
/// </summary>
public sealed class DeepHealthReport
{
    /// <summary>
    /// Gets or sets the observation time in UTC.
    /// </summary>
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the aggregate state derived from <see cref="Checks"/>.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the individual check results.
    /// </summary>
    public IReadOnlyList<DeepHealthCheck> Checks { get; set; } = Array.Empty<DeepHealthCheck>();
}
