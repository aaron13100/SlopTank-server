// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>The authority-bound facts needed to redeem one external writer admission.</summary>
/// <param name="PinnedObjectIdentity">The no-follow object identity at issue time.</param>
/// <param name="Action">The admitted physical action.</param>
/// <param name="AuthorityEpoch">The protocol epoch at issue time.</param>
/// <param name="Status">The durable lifecycle state.</param>
/// <param name="HeartbeatDeadlineUtc">The last time at which the started row remains live.</param>
internal sealed record PermalinkWriterAdmissionEvidence(
    string PinnedObjectIdentity,
    string Action,
    long AuthorityEpoch,
    string Status,
    DateTimeOffset? HeartbeatDeadlineUtc);
