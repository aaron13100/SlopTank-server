using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Represents one user-bound and purpose-bound resolution lease.
/// </summary>
/// <param name="Handle">The opaque candidate handle.</param>
/// <param name="Lease">The single-use lease token.</param>
/// <param name="PermalinkId">The requested permalink alias.</param>
/// <param name="ItemId">The bound Jellyfin item.</param>
/// <param name="CapsuleId">The logical capsule.</param>
/// <param name="ContentRoot">The leased content head.</param>
/// <param name="AnchorToken">The leased stable anchor.</param>
/// <param name="CurrentPath">The leased media path.</param>
/// <param name="Purpose">The allowed redemption purpose.</param>
/// <param name="UserId">The bound user.</param>
/// <param name="ExpiresAt">The canonical expiration timestamp.</param>
internal sealed record PermalinkLeaseDocument(
    string Handle,
    string Lease,
    string PermalinkId,
    Guid ItemId,
    Guid CapsuleId,
    string ContentRoot,
    string AnchorToken,
    string CurrentPath,
    string Purpose,
    Guid UserId,
    string ExpiresAt);
