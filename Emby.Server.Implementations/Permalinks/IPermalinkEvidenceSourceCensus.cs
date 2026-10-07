// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Reads a deterministic, pinned generation of one physical evidence source.</summary>
internal interface IPermalinkEvidenceSourceCensus
{
    /// <summary>Reads one source generation and its ordered observations.</summary>
    /// <param name="request">The exact source boundary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The source generation and observations.</returns>
    Task<PermalinkEvidenceSourceCensus.Result> ReadAsync(
        PermalinkEvidenceSourceCensus.Request request,
        CancellationToken cancellationToken);
}
