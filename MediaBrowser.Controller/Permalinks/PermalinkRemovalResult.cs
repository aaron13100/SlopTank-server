// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: asserted through private PermalinkRemovalControllerTests.cs

/// <summary>Removal state with the complete immutable intent echoed to the caller.</summary>
/// <param name="OperationId">The durable operation identifier.</param>
/// <param name="ItemId">The exact item leaving the library.</param>
/// <param name="SurvivorItemId">The optional proven survivor item.</param>
/// <param name="DestinationPath">The quarantine destination.</param>
/// <param name="State">The folded operation state.</param>
public sealed record PermalinkRemovalResult(
    Guid OperationId,
    Guid ItemId,
    Guid? SurvivorItemId,
    string DestinationPath,
    string State);
