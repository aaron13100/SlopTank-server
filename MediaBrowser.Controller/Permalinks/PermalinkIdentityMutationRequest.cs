using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Immutable identity mutation intent supplied before live state changes.</summary>
/// <param name="Kind">The mutation kind.</param>
/// <param name="DesiredProviderIds">The exact desired provider assignment.</param>
/// <param name="DesiredItemKind">The exact desired item kind.</param>
public sealed record PermalinkIdentityMutationRequest(
    string Kind,
    IReadOnlyDictionary<string, string>? DesiredProviderIds = null,
    string? DesiredItemKind = null);
