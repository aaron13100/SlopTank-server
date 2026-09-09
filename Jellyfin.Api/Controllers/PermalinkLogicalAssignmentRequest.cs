// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System.Collections.Generic;

namespace Jellyfin.Api.Controllers;

/// <summary>Exact logical-assignment confirmation payload.</summary>
/// <param name="OperationId">The prepared operation identifier.</param>
/// <param name="ProviderIds">The exact current provider assignment.</param>
public sealed record PermalinkLogicalAssignmentRequest(
    string OperationId,
    IReadOnlyDictionary<string, string>? ProviderIds);
