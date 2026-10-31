// SlopTank modification notice: added or changed by SlopTank on 2026-09-20, 2026-10-14, 2026-10-31.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Counts one bounded terminal-operation scratch reclaim pass.
/// </summary>
/// <param name="Examined">The number of terminal candidates inspected.</param>
/// <param name="Reclaimed">The number of candidates whose staging was absent or removed.</param>
/// <param name="Retained">The number of candidates retained because their scratch was unsafe.</param>
/// <param name="Failed">The number of candidates whose operation evidence could not be read.</param>
/// <param name="MaximumOperationsPerPass">The hard pass ceiling.</param>
internal sealed record PermalinkOperationScratchReclaimResult(
    int Examined,
    int Reclaimed,
    int Retained,
    int Failed,
    int MaximumOperationsPerPass);
