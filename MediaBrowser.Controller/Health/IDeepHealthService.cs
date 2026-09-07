using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.System;

namespace MediaBrowser.Controller.Health;

/// <summary>
/// Produces the single shared deep-health truth used by API projections.
/// </summary>
public interface IDeepHealthService
{
    /// <summary>
    /// Runs the bounded checks for the current request.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>A named per-check report.</returns>
    Task<DeepHealthReport> CheckAsync(CancellationToken cancellationToken);
}
