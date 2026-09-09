// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Initial binding-specific path lineage event.
/// </summary>
/// <param name="EventId">The path event identifier.</param>
/// <param name="PreviousPathEventId">The predecessor path event.</param>
/// <param name="Path">The bound media path.</param>
/// <param name="RootPath">The admitted content root.</param>
internal sealed record PermalinkPathAssignment(
    Guid EventId,
    Guid? PreviousPathEventId,
    string Path,
    string RootPath);
