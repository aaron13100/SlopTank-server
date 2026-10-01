// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>One <c>PermalinkRootIdentities</c> row.</summary>
/// <param name="RootIdentityId">The identity.</param>
/// <param name="Generation">The generation.</param>
/// <param name="CanonicalRoot">The canonical root path at registration.</param>
/// <param name="Attestation">The marker digest and root device:inode, <c>sha256:...|device:inode</c>.</param>
internal sealed record PermalinkRootIdentityRow(string RootIdentityId, long Generation, string CanonicalRoot, string Attestation);
