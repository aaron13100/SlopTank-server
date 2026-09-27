// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: serialized through private PermalinkRemovalInputTests.cs

/// <summary>Exact caller confirmation accepted after the physical move.</summary>
/// <param name="ItemId">The exact item leaving the library.</param>
/// <param name="DestinationPath">The quarantine destination.</param>
/// <param name="SurvivorItemId">The optional proven survivor item.</param>
public sealed record PermalinkRemovalCommitRequest(
    Guid ItemId,
    string DestinationPath,
    Guid? SurvivorItemId);
