// SlopTank modification notice: added or changed by SlopTank on 2026-10-02.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// A pending anchor-intent registry row as recovery reads it.
/// </summary>
/// <param name="RegistryId">The registry row id.</param>
/// <param name="RootIdentityId">The root identity the key lives under.</param>
/// <param name="NormalizedRelativePath">The normalized relative path of the key.</param>
/// <param name="AnchorToken">The token the row's lease wrote as xattr, or null for a row that predates the column.</param>
internal sealed record PermalinkAnchorIntentRow(
    Guid RegistryId,
    string RootIdentityId,
    string NormalizedRelativePath,
    string? AnchorToken);
