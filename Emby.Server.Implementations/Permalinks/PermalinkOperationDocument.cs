using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Freezes the immutable input for one protected mutation operation.
/// </summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="ItemId">The source item identifier.</param>
/// <param name="CapsuleId">The source capsule identifier.</param>
/// <param name="Kind">The typed mutation kind.</param>
/// <param name="SourcePath">The prepared source path.</param>
/// <param name="DestinationPath">The optional prepared destination path.</param>
/// <param name="OldContentRoot">The prepared predecessor content root.</param>
/// <param name="OldProviderIds">The prepared provider assignment.</param>
/// <param name="DesiredProviderIds">The desired provider assignment.</param>
/// <param name="OldIdentity">The optional complete prepared identity snapshot.</param>
/// <param name="Bundle">The complete mutation bundle.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkOperationDocument(
    Guid OperationId,
    Guid ItemId,
    Guid CapsuleId,
    string Kind,
    string SourcePath,
    string? DestinationPath,
    string OldContentRoot,
    IReadOnlyDictionary<string, string> OldProviderIds,
    IReadOnlyDictionary<string, string> DesiredProviderIds,
    PermalinkIdentitySnapshot? OldIdentity,
    PermalinkMutationBundle? Bundle,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-operation";

    public int Version { get; init; } = 1;
}
