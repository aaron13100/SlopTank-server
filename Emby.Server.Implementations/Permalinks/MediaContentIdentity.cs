// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using System.Collections.Generic;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Describes content using the library's existing identification metadata.</summary>
/// <param name="Kind">The logical movie, series, season, episode or collection kind.</param>
/// <param name="PublicAliases">Existing provider identifiers in preferred URL order.</param>
/// <param name="CoordinateKey">A private series-scoped season or episode lookup key.</param>
internal sealed record MediaContentIdentity(
    string Kind,
    IReadOnlyList<string> PublicAliases,
    string? CoordinateKey);
