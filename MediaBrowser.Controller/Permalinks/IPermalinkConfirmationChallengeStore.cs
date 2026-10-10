// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Issues and atomically consumes exact, durable administrator confirmation challenges.</summary>
public interface IPermalinkConfirmationChallengeStore
{
    /// <summary>Issues one server-generated challenge for an exact operation tuple.</summary>
    /// <param name="request">The exact operation tuple.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The one-time plaintext challenge and its immutable binding.</returns>
    Task<PermalinkConfirmationChallenge> IssueAsync(
        PermalinkConfirmationChallengeRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates and consumes a challenge in one serializable writer transaction.</summary>
    /// <param name="presentation">The complete server-issued challenge tuple.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The consumed challenge and operation identifiers.</returns>
    Task<PermalinkConfirmationChallengeConsumption> ConsumeAsync(
        PermalinkConfirmationChallengePresentation presentation,
        CancellationToken cancellationToken);
}
