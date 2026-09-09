// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Immutable root-local anchor election record.
/// </summary>
/// <param name="AnchorToken">The stable physical anchor token.</param>
/// <param name="CapsuleId">The elected capsule identifier.</param>
/// <param name="BindingInstanceId">The elected physical binding.</param>
/// <param name="OperationId">The genesis operation.</param>
/// <param name="ItemKind">The immutable item kind.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="Path">The elected media path.</param>
/// <param name="RootPath">The admitted content root.</param>
/// <param name="PathEventId">The initial path event.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkAnchorDocument(
    string AnchorToken,
    Guid CapsuleId,
    Guid BindingInstanceId,
    Guid OperationId,
    string ItemKind,
    string ContentRoot,
    string Path,
    string RootPath,
    Guid PathEventId,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-anchor";

    public int Version { get; init; } = 1;
}
