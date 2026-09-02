using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
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
/// <remarks>
/// This is a durable format with tens of thousands of published records, so it only ever grows by
/// optional members. Readers tolerate unknown members and any version, and a record that carries no
/// content beyond the original shape must keep serializing to byte-identical JSON, because phase
/// publication is create-exclusive and compares exact bytes on replay.
/// </remarks>
internal sealed record PermalinkOperationPhase(
    Guid OperationId,
    string State,
    string? ContentRoot,
    string CreatedAt,
    IReadOnlyList<PermalinkLeaf>? Leaves = null)
{
    public string Type { get; init; } = "sloptank.permalink-operation-phase";

    /// <summary>
    /// Gets the durable record version. It is derived rather than stored, so it can never disagree
    /// with the content actually present: a record without <see cref="ObservedState"/> stays at
    /// version 1 and keeps the exact bytes every already-published record has, and only a record
    /// carrying the snapshot declares version 2.
    /// </summary>
    public int Version => ObservedState is null ? 1 : 2;

    /// <summary>
    /// Gets the canonical observed-evidence snapshot recorded by a phase that closes an operation
    /// without asserting what its partial evidence meant. Omitted entirely when absent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedState { get; init; }
}
