// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Reports live authority freshness independently of public snapshots.</summary>
public interface IPermalinkAuthorityGenerationOracle
{
    /// <summary>Gets the current live counters and latest published generation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live generation state.</returns>
    Task<PermalinkAuthorityGenerationInfo> GetGenerationAsync(CancellationToken cancellationToken);
}
