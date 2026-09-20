// SlopTank modification notice: added or changed by SlopTank on 2026-09-20.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>Summarizes one explicit permalink scratch maintenance invocation.</summary>
/// <param name="Examined">The number of selected operations examined.</param>
/// <param name="Reclaimed">The number whose staging was absent or removed.</param>
/// <param name="Retained">The number retained because the scratch was unsafe or non-terminal.</param>
/// <param name="Failed">The number whose evidence could not be read.</param>
/// <param name="MaximumOperationsPerPass">The configured pass ceiling.</param>
public sealed record PermalinkOperationScratchMaintenanceResult(
    int Examined,
    int Reclaimed,
    int Retained,
    int Failed,
    int MaximumOperationsPerPass);
