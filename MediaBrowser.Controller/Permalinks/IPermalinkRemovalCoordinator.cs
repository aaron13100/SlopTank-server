// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: exercised by private PermalinkRemovalControllerTests.cs through HTTP

/// <summary>Coordinates the durable external-file removal protocol.</summary>
public interface IPermalinkRemovalCoordinator
{
    /// <summary>Freezes one exact removal before any file is moved.</summary>
    /// <param name="item">The source library item.</param>
    /// <param name="request">The exact removal intent and evidence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The durable prepared state.</returns>
    Task<PermalinkRemovalResult> PrepareAsync(
        BaseItem item,
        PermalinkRemovalPrepareRequest request,
        CancellationToken cancellationToken);

    /// <summary>Publishes a prepared removal after its exact file move.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="request">The exact post-move confirmation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The durable committed state.</returns>
    Task<PermalinkRemovalResult> CommitAsync(
        Guid operationId,
        PermalinkRemovalCommitRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an interrupted removal from durable evidence.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recovered durable state.</returns>
    Task<PermalinkRemovalResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
