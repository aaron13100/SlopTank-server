// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>The complete physical state of one side of a removal move.</summary>
internal enum PermalinkRemovalGroupState
{
    Empty,
    Complete,
    Ambiguous
}
