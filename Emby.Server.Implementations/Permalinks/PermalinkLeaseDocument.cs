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
/// <param name="BindingInstanceId">The leased binding instance.</param>
/// <param name="CurrentPath">The leased media path.</param>
/// <param name="ObjectIdentity">The leased live object identity.</param>
/// <param name="AssignmentHead">The leased logical assignment head.</param>
/// <param name="AcceptedProviderDigest">The accepted provider-set digest.</param>
/// <param name="ActiveAliasSetDigest">The active alias-set digest.</param>
/// <param name="Purpose">The allowed redemption purpose.</param>
/// <param name="UserId">The bound user.</param>
/// <param name="ServerId">The bound server authority.</param>
/// <param name="ExpiresAt">The canonical expiration timestamp.</param>
internal sealed record PermalinkLeaseDocument(
    string Handle,
    string Lease,
    string PermalinkId,
    Guid ItemId,
    Guid CapsuleId,
    string ContentRoot,
    string AnchorToken,
    Guid BindingInstanceId,
    string CurrentPath,
    string ObjectIdentity,
    string AssignmentHead,
    string AcceptedProviderDigest,
    string ActiveAliasSetDigest,
    string Purpose,
    Guid UserId,
    Guid ServerId,
    string ExpiresAt);
