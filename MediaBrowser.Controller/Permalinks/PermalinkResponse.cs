// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Versioned response returned by the permalink endpoint.</summary>
/// <param name="Version">The response format version.</param>
/// <param name="Ids">The ordered active aliases.</param>
/// <param name="CanonicalId">The first canonical alias.</param>
public sealed record PermalinkResponse(
    int Version,
    IReadOnlyList<string> Ids,
    string CanonicalId);
