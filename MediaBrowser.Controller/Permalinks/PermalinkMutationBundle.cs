// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Freezes every durable predecessor and physical part owned by one protected mutation.
/// </summary>
public sealed record PermalinkMutationBundle(
    IReadOnlyList<PermalinkMutationClaim> Claims,
    IReadOnlyList<PermalinkMutationPart> Parts,
    string ContentRootPath,
    string? DestinationRootPath);
