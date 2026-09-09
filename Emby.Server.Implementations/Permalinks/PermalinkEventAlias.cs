// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Alias entry persisted in an event.
/// </summary>
/// <param name="Id">The permalink identifier.</param>
/// <param name="IssuanceNonce">The matching issuance nonce.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkEventAlias(string Id, Guid IssuanceNonce, string CreatedAt);
