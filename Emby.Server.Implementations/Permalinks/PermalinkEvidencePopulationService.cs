// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Runs population only from the complete freshly attested production plan.</summary>
internal sealed class PermalinkEvidencePopulationService : IPermalinkEvidencePopulationService
{
    private readonly PermalinkEvidencePopulationPlan _plan;
    private readonly PermalinkEvidencePopulationCoordinator _coordinator;

    /// <summary>Initializes a new instance of the <see cref="PermalinkEvidencePopulationService"/> class.</summary>
    /// <param name="plan">The complete production source planner.</param>
    /// <param name="coordinator">The resumable population coordinator.</param>
    public PermalinkEvidencePopulationService(
        PermalinkEvidencePopulationPlan plan,
        PermalinkEvidencePopulationCoordinator coordinator)
    {
        _plan = plan;
        _coordinator = coordinator;
    }

    /// <inheritdoc />
    public async Task RunAsync(string populationId, CancellationToken cancellationToken)
    {
        var requests = await _plan.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _coordinator.RunAsync(populationId, requests, cancellationToken).ConfigureAwait(false);
    }
}
