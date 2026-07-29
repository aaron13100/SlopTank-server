using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Immutable never-reusable issuance tombstone.
/// </summary>
/// <param name="Id">The issued permalink identifier.</param>
/// <param name="IssuanceNonce">The issuance nonce.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkIssuance(string Id, Guid IssuanceNonce, string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-issuance";

    public int Version { get; init; } = 1;
}
