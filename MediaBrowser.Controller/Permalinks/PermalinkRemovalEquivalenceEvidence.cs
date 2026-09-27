// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: serialized through private PermalinkRemovalInputTests.cs

/// <summary>Caller evidence bound to the exact files participating in a removal.</summary>
/// <param name="SourceItemId">The exact item leaving the library.</param>
/// <param name="SourcePath">The source path before the move.</param>
/// <param name="SurvivorItemId">The optional proven survivor item.</param>
/// <param name="SurvivorPath">The exact decoded-frame comparison counterpart.</param>
/// <param name="DestinationPath">The quarantine destination.</param>
/// <param name="Samples">The conservative decoded-frame samples.</param>
/// <param name="Threshold">The score threshold used by the caller.</param>
public sealed record PermalinkRemovalEquivalenceEvidence(
    Guid SourceItemId,
    string SourcePath,
    Guid? SurvivorItemId,
    string SurvivorPath,
    string DestinationPath,
    IReadOnlyList<PermalinkRemovalFrameSample> Samples,
    double Threshold);
