// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>The exact external writer capability offered for one leased path recheck.</summary>
/// <param name="AdmissionId">The durable admission row.</param>
/// <param name="Action">The physical action this call is performing.</param>
/// <param name="PinnedObjectIdentity">The current no-follow object identity.</param>
/// <param name="Key">The canonical path key the admission reserves.</param>
internal sealed record PermalinkWriterAdmissionPermit(
    Guid AdmissionId,
    string Action,
    string PinnedObjectIdentity,
    PermalinkLeasedKey Key);
