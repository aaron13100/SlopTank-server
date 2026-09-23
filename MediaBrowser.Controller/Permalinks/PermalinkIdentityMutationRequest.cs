// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-09-23.
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Immutable identity mutation intent supplied before live state changes.</summary>
/// <param name="Kind">The mutation kind.</param>
/// <param name="DesiredProviderIds">The exact desired provider assignment.</param>
/// <param name="DesiredItemKind">The exact desired item kind.</param>
/// <param name="CascadeToDescendants">Whether the mutation body rewrites recursive descendants and must durably fence them too. A manual metadata edit that leaves LockData unchanged never touches a descendant, so its response must not pay the per-descendant ensure and journal round-trips (measured 2026-09-23: 27 s for a 15-episode season under ambient load, 100 s+ on 2026-09-21 while the editor spun forever).</param>
public sealed record PermalinkIdentityMutationRequest(
    string Kind,
    IReadOnlyDictionary<string, string>? DesiredProviderIds = null,
    string? DesiredItemKind = null,
    bool CascadeToDescendants = false);
