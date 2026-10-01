// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The durable <c>.sloptank-root-identity</c> marker stored at a content root (P6 decisions,
/// "Root identity registration"). It travels with the root, so two alias paths and a
/// remounted volume read the same identity.
/// </summary>
/// <param name="AuthorityId">The authority that registered the root.</param>
/// <param name="RootIdentityId">The root identity this marker names.</param>
/// <param name="Generation">The registered generation.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkRootMarker(Guid AuthorityId, Guid RootIdentityId, long Generation, string CreatedAt)
{
    /// <summary>The marker file name at a content root. Jellyfin and the mediaserver tools must leave it alone.</summary>
    public const string FileName = ".sloptank-root-identity";

    /// <summary>Gets the marker format version.</summary>
    public int Version { get; init; } = 1;
}
