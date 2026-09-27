// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

// allow-no-test-found: serialized through private PermalinkRemovalInputTests.cs

/// <summary>One conservative decoded-frame comparison supplied by the removal tool.</summary>
/// <param name="TimestampSeconds">The requested frame timestamp.</param>
/// <param name="SearchOffsetsSeconds">The exact nearby offsets searched.</param>
/// <param name="DecodedFrameDifference">The best decoded-frame difference score.</param>
public sealed record PermalinkRemovalFrameSample(
    int TimestampSeconds,
    IReadOnlyList<int> SearchOffsetsSeconds,
    double DecodedFrameDifference);
