// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-03.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Immutable caller intent accepted by the Prepare boundary.</summary>
/// <param name="OperationId">The caller-selected idempotency identifier.</param>
/// <param name="ItemId">The protected item identifier.</param>
/// <param name="Kind">The typed mutation kind.</param>
/// <param name="DestinationPath">The desired destination for path mutations.</param>
/// <param name="DesiredProviderIds">The desired logical assignment.</param>
/// <param name="ExpectedSourcePath">Optional filename association observed before preparing the replacement.</param>
public sealed record PermalinkMutationPrepareRequest(
    Guid OperationId,
    Guid ItemId,
    string Kind,
    string? DestinationPath,
    IReadOnlyDictionary<string, string>? DesiredProviderIds,
    string? ExpectedSourcePath = null);
