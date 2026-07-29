using System.Collections.Generic;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Holds a canonical evidence root and its complete leaves.
/// </summary>
/// <param name="ContentRoot">The canonical full-content root.</param>
/// <param name="Leaves">The complete evidence leaf set.</param>
public sealed record PermalinkEvidenceResult(
    string ContentRoot,
    IReadOnlyList<PermalinkLeaf> Leaves);
