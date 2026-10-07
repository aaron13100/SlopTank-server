// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>One immutable, operation-owned successor for a claimed capsule predecessor.</summary>
/// <param name="CapsuleId">The capsule advanced by the event.</param>
/// <param name="ItemId">The item whose derived binding advances with the capsule.</param>
/// <param name="Predecessor">The exact content root claimed by the operation.</param>
/// <param name="Event">The exact canonical event to authorize and append.</param>
internal sealed record PermalinkPreparedContentSuccessor(
    Guid CapsuleId,
    Guid ItemId,
    string Predecessor,
    PermalinkEventDocument Event);
