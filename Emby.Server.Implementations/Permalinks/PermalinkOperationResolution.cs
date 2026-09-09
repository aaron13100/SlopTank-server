// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Records one authenticated guarded operation resolution attempt.
/// </summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="Action">The guarded action.</param>
/// <param name="StagedPath">The optional restage input path.</param>
/// <param name="ObservedState">The canonical observed evidence state.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkOperationResolution(
    Guid OperationId,
    string Action,
    string? StagedPath,
    string ObservedState,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-operation-resolution";

    public int Version { get; init; } = 1;
}
