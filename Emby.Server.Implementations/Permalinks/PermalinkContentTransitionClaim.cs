// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// One durable, single-use claim on a capsule's content predecessor, read back from the authority.
/// </summary>
/// <param name="CapsuleId">The logical capsule identifier.</param>
/// <param name="Predecessor">
/// The claimed predecessor as stored: the predecessor event id in "D" format, or the literal
/// "genesis" the mint writes for the genesis event, so it is a string rather than a Guid.
/// </param>
/// <param name="OperationId">The operation that owns the claim.</param>
/// <param name="EventId">The authorized successor event id.</param>
/// <param name="EventDigest">The canonical digest of the authorized successor event bytes.</param>
/// <param name="CreatedAt">The canonical creation timestamp the successor event carries.</param>
internal sealed record PermalinkContentTransitionClaim(
    Guid CapsuleId,
    string Predecessor,
    Guid OperationId,
    Guid EventId,
    string EventDigest,
    string CreatedAt);
