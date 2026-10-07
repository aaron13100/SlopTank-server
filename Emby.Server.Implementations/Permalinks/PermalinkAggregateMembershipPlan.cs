// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Freezes both database outcomes for one protected aggregate.</summary>
/// <param name="ItemId">The aggregate item identifier.</param>
/// <param name="CapsuleId">The protected capsule identifier.</param>
/// <param name="PreviousMemberIds">The complete membership before mutation.</param>
/// <param name="DesiredMemberIds">The complete intended membership after mutation.</param>
internal sealed record PermalinkAggregateMembershipPlan(
    Guid ItemId,
    Guid CapsuleId,
    IReadOnlyList<Guid> PreviousMemberIds,
    IReadOnlyList<Guid> DesiredMemberIds);
