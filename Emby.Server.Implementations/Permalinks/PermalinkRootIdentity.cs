// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>The root a path lease keys on.</summary>
/// <param name="RootIdentityId">The registered identity.</param>
/// <param name="Generation">The registered generation.</param>
/// <param name="CanonicalRoot">The canonical root path the identity was resolved through.</param>
internal sealed record PermalinkRootIdentity(string RootIdentityId, long Generation, string CanonicalRoot);
