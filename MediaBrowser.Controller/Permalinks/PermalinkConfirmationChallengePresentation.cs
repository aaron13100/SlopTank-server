// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Presents the complete server-issued confirmation tuple for atomic consumption.</summary>
/// <param name="ChallengeId">The challenge identifier.</param>
/// <param name="OperationId">The durable operation identifier.</param>
/// <param name="Challenge">The server-issued one-time plaintext secret.</param>
/// <param name="TargetDigest">The exact target tuple digest.</param>
/// <param name="InventoryGeneration">The sealed inventory generation digest.</param>
/// <param name="RequestedAction">The exact elevated action.</param>
/// <param name="ExpiresAt">The exact server-selected expiry.</param>
public sealed record PermalinkConfirmationChallengePresentation(
    [property: JsonPropertyName("challengeId")] Guid ChallengeId,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("targetDigest")] string TargetDigest,
    [property: JsonPropertyName("inventoryGeneration")] string InventoryGeneration,
    [property: JsonPropertyName("requestedAction")] string RequestedAction,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
