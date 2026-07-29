using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Immutable append-only capsule event.
/// </summary>
/// <param name="EventId">The event identifier.</param>
/// <param name="OperationId">The operation that published the event.</param>
/// <param name="ParentEventIds">The immutable parent event identifiers.</param>
/// <param name="Kind">The event kind.</param>
/// <param name="Ids">The aliases carried by the event.</param>
/// <param name="PreviousContentEventId">The predecessor content event.</param>
/// <param name="PreviousAssignmentEventId">The predecessor assignment event.</param>
/// <param name="AcceptedProviderClaim">The accepted provider claim.</param>
/// <param name="ActiveExternalAliases">The active external aliases.</param>
/// <param name="ContentRoot">The full-content evidence root.</param>
/// <param name="Leaves">The complete evidence leaves.</param>
/// <param name="BindingInstanceId">The physical binding identifier.</param>
/// <param name="PathAssignment">The binding path assignment.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkEventDocument(
    Guid EventId,
    Guid OperationId,
    IReadOnlyList<Guid> ParentEventIds,
    string Kind,
    IReadOnlyList<PermalinkEventAlias> Ids,
    Guid? PreviousContentEventId,
    Guid? PreviousAssignmentEventId,
    IReadOnlyList<PermalinkProviderClaim> AcceptedProviderClaim,
    IReadOnlyList<string> ActiveExternalAliases,
    string ContentRoot,
    IReadOnlyList<PermalinkLeaf> Leaves,
    Guid BindingInstanceId,
    PermalinkPathAssignment PathAssignment,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-event";

    public int Version { get; init; } = 1;
}
