using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Health;

/// <summary>
/// Narrow read-only boundaries used by the deep health evaluator.
/// </summary>
public interface IDeepHealthProbe
{
    /// <summary>
    /// Executes one trivial query through the configured data-access layer.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>A task representing the database query.</returns>
    ValueTask QueryDatabaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the configured media library roots without scanning their contents.
    /// </summary>
    /// <returns>The configured roots.</returns>
    IReadOnlyList<string> GetLibraryRoots();

    /// <summary>
    /// Gets the configured FFmpeg executable path.
    /// </summary>
    /// <returns>The executable path.</returns>
    string GetFfmpegPath();

    /// <summary>
    /// Gets whether tracked transcode state has remained at zero progress
    /// since at least <paramref name="thresholdUtc"/>.
    /// </summary>
    /// <param name="thresholdUtc">Oldest allowed zero-progress observation.</param>
    /// <returns><see langword="true"/> when a tracked transcode is stalled.</returns>
    bool HasStalledTranscode(DateTime thresholdUtc);
}
