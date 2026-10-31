// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-10-14, 2026-10-31.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Accepted external assignment entry.
/// </summary>
/// <param name="Namespace">The provider namespace.</param>
/// <param name="Value">The provider identifier.</param>
internal sealed record PermalinkProviderClaim(string Namespace, string Value);
