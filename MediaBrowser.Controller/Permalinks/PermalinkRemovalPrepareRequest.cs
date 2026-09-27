// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: serialized through private PermalinkRemovalInputTests.cs

/// <summary>Immutable caller intent accepted by the elevated removal prepare boundary.</summary>
/// <param name="OperationId">The deterministic idempotency identifier.</param>
/// <param name="ItemId">The exact item leaving the library.</param>
/// <param name="SurvivorItemId">The optional proven survivor item.</param>
/// <param name="DestinationPath">The quarantine destination.</param>
/// <param name="EquivalenceEvidence">The exact structured comparison evidence.</param>
public sealed record PermalinkRemovalPrepareRequest(
    Guid OperationId,
    Guid ItemId,
    Guid? SurvivorItemId,
    string DestinationPath,
    PermalinkRemovalEquivalenceEvidence EquivalenceEvidence);
