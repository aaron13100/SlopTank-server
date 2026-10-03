// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Reads the complete freshly attested root-identity generation set.</summary>
internal interface IPermalinkRootIdentityCensus
{
    /// <summary>Validates every configured and previously registered root.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The canonical ordered generation set.</returns>
    Task<PermalinkRootIdentityCensusResult> ReadAsync(CancellationToken cancellationToken);
}
