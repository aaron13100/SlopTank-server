using System;
using System.Collections.Generic;
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
    Task<PermalinkMutationResult> PrepareAsync(
        BaseItem item,
        PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken);

    /// <summary>Commits the exact prepared mutation or fails without overwriting unknown bytes.</summary>
    Task<PermalinkMutationResult> CommitAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken);

    /// <summary>Recovers one operation from its deterministic durable phases.</summary>
    Task<PermalinkMutationResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable caller intent accepted by the Prepare boundary.
/// </summary>
public sealed record PermalinkMutationPrepareRequest(
    Guid OperationId,
    Guid ItemId,
    string Kind,
    string? DestinationPath,
    IReadOnlyDictionary<string, string>? DesiredProviderIds);

/// <summary>
/// Exact caller-owned staging input accepted by Commit.
/// </summary>
public sealed record PermalinkMutationCommitRequest(string? StagedPath);

/// <summary>
/// Public folded state of a durable mutation operation.
/// </summary>
public sealed record PermalinkMutationResult(Guid OperationId, string State);
