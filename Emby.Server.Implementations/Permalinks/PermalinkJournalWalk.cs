// SlopTank modification notice: added or changed by SlopTank on 2026-09-04, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// What one full enumeration of the permalink operation journal cost.
///
/// The directory count travels with the timing on purpose. The journal grows
/// without bound (nothing prunes terminal operations), so a walk duration read
/// on its own is not comparable against the same walk a month later, and the
/// only question worth asking of these numbers is how the cost moves with the
/// size.
/// </summary>
/// <param name="Directories">Operation directories enumerated.</param>
/// <param name="Pending">Operations that were still pending among them.</param>
/// <param name="Elapsed">Wall-clock time the enumeration took.</param>
internal sealed record PermalinkJournalWalk(
    int Directories,
    int Pending,
    TimeSpan Elapsed);
