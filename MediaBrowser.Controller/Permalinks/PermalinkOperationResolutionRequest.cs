namespace MediaBrowser.Controller.Permalinks;

/// <summary>Selects one guarded administrator continuation for suspended evidence.</summary>
/// <param name="Action">The guarded resolution action.</param>
/// <param name="StagedPath">The caller-supplied exact restage path.</param>
public sealed record PermalinkOperationResolutionRequest(
    string Action,
    string? StagedPath = null);
