// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Frozen first-alias election result.
/// </summary>
/// <param name="PermalinkId">The elected permalink identifier.</param>
/// <param name="IssuanceNonce">The optional issuance nonce.</param>
/// <param name="EventId">The alias event identifier.</param>
/// <param name="EventJson">The canonical alias event.</param>
/// <param name="IsWinner">Whether this caller won the election.</param>
internal sealed record PermalinkAliasClaim(
    string PermalinkId,
    Guid? IssuanceNonce,
    Guid EventId,
    ReadOnlyMemory<byte> EventJson,
    bool IsWinner);
