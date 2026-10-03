// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The canonical lease key of one leased path: <c>(root_identity_id, normalized_relative_path)</c>.
/// </summary>
/// <param name="RootIdentityId">The root identity.</param>
/// <param name="NormalizedRelativePath">The NFC-normalized relative path.</param>
internal readonly record struct PermalinkLeasedKey(string RootIdentityId, string NormalizedRelativePath);
