// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>Fail-closed classification for one exact filesystem path.</summary>
internal enum PermalinkRemovalPathState
{
    Missing,
    Regular,
    Occupied
}
