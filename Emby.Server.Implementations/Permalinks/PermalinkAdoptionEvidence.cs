// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The immutable authority evidence one adoption's journal record is rebuilt from.
/// </summary>
/// <param name="OperationId">The derived adoption operation identifier.</param>
/// <param name="ItemId">The adopted library item.</param>
/// <param name="CapsuleId">The sole capsule adopted against.</param>
/// <param name="RecordedContentRoot">The predecessor content root the capsule head held.</param>
/// <param name="ObservedContentRoot">The adopted content root the successor event carries.</param>
/// <param name="SourcePath">The immutable path assignment the successor event carries.</param>
/// <param name="CreatedAt">The creation timestamp shared by the successor event, its authority claim and the record.</param>
internal sealed record PermalinkAdoptionEvidence(
    Guid OperationId,
    Guid ItemId,
    Guid CapsuleId,
    string RecordedContentRoot,
    string ObservedContentRoot,
    string SourcePath,
    string CreatedAt);
