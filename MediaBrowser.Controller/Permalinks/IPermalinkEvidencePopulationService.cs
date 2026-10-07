// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Runs the guarded, resumable population of every configured recovery source.</summary>
public interface IPermalinkEvidencePopulationService
{
    /// <summary>Runs or resumes one stable population identifier.</summary>
    /// <param name="populationId">The operator-selected durable population identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the population.</returns>
    Task RunAsync(string populationId, CancellationToken cancellationToken);
}
