using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Coordinates durable protected mutations for items with permalink capsules.
/// </summary>
public interface IPermalinkMutationCoordinator
{
    /// <summary>Freezes the verified old state before a caller mutates live state.</summary>
    /// <param name="item">The protected library item.</param>
    /// <param name="request">The immutable mutation intent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded prepared operation state.</returns>
    Task<PermalinkMutationResult> PrepareAsync(
        BaseItem item,
        PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken);

    /// <summary>Commits the exact prepared mutation or fails without overwriting unknown bytes.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="request">The exact caller-owned staging input.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded committed or suspended operation state.</returns>
    Task<PermalinkMutationResult> CommitAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken);

    /// <summary>Recovers one operation from its deterministic durable phases.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded recovered operation state.</returns>
    Task<PermalinkMutationResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>Continues a suspended operation only when exact evidence authorizes the action.</summary>
    /// <param name="operationId">The suspended operation identifier.</param>
    /// <param name="request">The guarded administrator action.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded continued operation state.</returns>
    Task<PermalinkMutationResult> ResolveAsync(
        Guid operationId,
        PermalinkOperationResolutionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Cancels a prepared logical mutation only when live identity still equals prepared-old.</summary>
    /// <param name="operationId">The prepared logical operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded cancelled operation state.</returns>
    Task<PermalinkMutationResult> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>Terminates failed unpublished work without releasing published work from recovery.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded aborted, published, or existing terminal state.</returns>
    Task<PermalinkMutationResult> AbortAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
