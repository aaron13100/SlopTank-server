// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-10-03.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Exact caller-owned staging input accepted by Commit.</summary>
/// <param name="StagedPath">The caller-owned staged media path.</param>
/// <param name="DesiredProviderIds">The exact desired logical assignment.</param>
/// <param name="WriterAdmissionId">The external writer admission to redeem at the physical publication seam.</param>
public sealed record PermalinkMutationCommitRequest(
    string? StagedPath,
    IReadOnlyDictionary<string, string>? DesiredProviderIds = null,
    Guid? WriterAdmissionId = null);
