using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Exact caller-owned staging input accepted by Commit.</summary>
/// <param name="StagedPath">The caller-owned staged media path.</param>
/// <param name="DesiredProviderIds">The exact desired logical assignment.</param>
public sealed record PermalinkMutationCommitRequest(
    string? StagedPath,
    IReadOnlyDictionary<string, string>? DesiredProviderIds = null);
