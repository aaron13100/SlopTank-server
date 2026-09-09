// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Identifies one exact capsule predecessor claimed atomically with its bundle.</summary>
/// <param name="CapsuleId">The claimed capsule identifier.</param>
/// <param name="ItemId">The owning item identifier.</param>
/// <param name="BindingInstanceId">The physical binding instance.</param>
/// <param name="Predecessor">The exact predecessor event.</param>
/// <param name="Kind">The content, path, or aggregate claim kind.</param>
public sealed record PermalinkMutationClaim(
    Guid CapsuleId,
    Guid ItemId,
    Guid BindingInstanceId,
    string Predecessor,
    string Kind);
