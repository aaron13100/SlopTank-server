// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System.Collections.Generic;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Holds the validated folded capsule state.
/// </summary>
/// <param name="Header">The immutable capsule header.</param>
/// <param name="ContentHead">The folded content head.</param>
/// <param name="Events">The validated immutable event set.</param>
internal sealed record PermalinkCapsuleSnapshot(
    PermalinkCapsuleDocument Header,
    PermalinkEventDocument ContentHead,
    IReadOnlyList<PermalinkEventDocument> Events);
