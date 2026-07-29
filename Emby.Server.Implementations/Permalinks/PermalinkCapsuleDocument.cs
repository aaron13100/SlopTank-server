using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Immutable capsule header.
/// </summary>
/// <param name="CapsuleId">The logical capsule identifier.</param>
/// <param name="ItemKind">The immutable item kind.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkCapsuleDocument(Guid CapsuleId, string ItemKind, string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-capsule";

    public int Version { get; init; } = 1;
}
