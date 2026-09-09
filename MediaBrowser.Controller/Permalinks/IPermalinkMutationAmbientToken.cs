// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Opaque operation context valid only within its owning adapter invocation.</summary>
public interface IPermalinkMutationAmbientToken
{
    /// <summary>Gets the immutable operation ids prepared for this bundle.</summary>
    IReadOnlyList<Guid> OperationIds { get; }
}
