using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Immutable authority identity.
/// </summary>
/// <param name="AuthorityId">The authority identifier.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkAuthorityDocument(Guid AuthorityId, string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-authority";

    public int Version { get; init; } = 1;
}
