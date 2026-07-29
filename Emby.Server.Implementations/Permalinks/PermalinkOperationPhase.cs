using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Represents one deterministic immutable operation phase.
/// </summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="State">The phase state.</param>
/// <param name="ContentRoot">The optional verified content root.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
/// <param name="Leaves">The optional complete evidence leaves.</param>
internal sealed record PermalinkOperationPhase(
    Guid OperationId,
    string State,
    string? ContentRoot,
    string CreatedAt,
    IReadOnlyList<PermalinkLeaf>? Leaves = null)
{
    public string Type { get; init; } = "sloptank.permalink-operation-phase";

    public int Version { get; init; } = 1;
}
