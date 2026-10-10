// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Requests server-generated confirmation authority for one exact durable operation tuple.</summary>
/// <param name="OperationId">The durable operation identifier.</param>
/// <param name="TargetDigest">The canonical SHA-256 digest of the exact target tuple.</param>
/// <param name="InventoryGeneration">The canonical SHA-256 digest of the sealed inventory generation.</param>
/// <param name="RequestedAction">The exact elevated action.</param>
public sealed record PermalinkConfirmationChallengeRequest(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("targetDigest")] string TargetDigest,
    [property: JsonPropertyName("inventoryGeneration")] string InventoryGeneration,
    [property: JsonPropertyName("requestedAction")] string RequestedAction);
