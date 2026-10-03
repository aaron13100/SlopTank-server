// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Requests a durable prepublication intent for one retirement hardlink or copy.</summary>
/// <param name="OperationId">The caller-generated idempotency and recovery identifier.</param>
/// <param name="SourceRootIdentityId">The configured content-root identity.</param>
/// <param name="SourceNormalizedRelativePath">The normalized source path below the content root.</param>
/// <param name="SourcePinnedObjectIdentity">The no-follow source device and inode.</param>
/// <param name="DestinationRootIdentityId">The configured evidence-root identity.</param>
/// <param name="DestinationNormalizedRelativePath">The normalized, absent destination below the evidence root.</param>
/// <param name="DestinationParentPinnedObjectIdentity">The no-follow device and inode of the existing destination parent.</param>
/// <param name="SnapshotGeneration">The latest published snapshot generation the writer read.</param>
public sealed record PermalinkRetirementAdmissionRequest(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("sourceRootIdentityId")] string SourceRootIdentityId,
    [property: JsonPropertyName("sourceNormalizedRelativePath")] string SourceNormalizedRelativePath,
    [property: JsonPropertyName("sourcePinnedObjectIdentity")] string SourcePinnedObjectIdentity,
    [property: JsonPropertyName("destinationRootIdentityId")] string DestinationRootIdentityId,
    [property: JsonPropertyName("destinationNormalizedRelativePath")] string DestinationNormalizedRelativePath,
    [property: JsonPropertyName("destinationParentPinnedObjectIdentity")] string DestinationParentPinnedObjectIdentity,
    [property: JsonPropertyName("snapshotGeneration")] long SnapshotGeneration);
