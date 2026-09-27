// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>One exact stem-sharing companion frozen with a removal.</summary>
internal sealed record PermalinkRemovalCompanion(
    string FileName,
    string ContentRoot,
    long Length);
