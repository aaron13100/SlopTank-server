using System.Collections.Generic;

namespace Jellyfin.Api.Controllers;

/// <summary>Exact logical-assignment confirmation payload.</summary>
/// <param name="OperationId">The prepared operation identifier.</param>
/// <param name="ProviderIds">The exact current provider assignment.</param>
public sealed record PermalinkLogicalAssignmentRequest(
    string OperationId,
    IReadOnlyDictionary<string, string>? ProviderIds);
