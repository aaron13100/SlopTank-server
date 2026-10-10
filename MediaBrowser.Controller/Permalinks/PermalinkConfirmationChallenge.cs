// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>The one-time server-issued confirmation challenge and every field it binds.</summary>
/// <param name="ChallengeId">The challenge identifier.</param>
/// <param name="OperationId">The durable operation identifier.</param>
/// <param name="Challenge">The server-generated one-time plaintext secret, which is never persisted.</param>
/// <param name="TargetDigest">The exact target tuple digest.</param>
/// <param name="InventoryGeneration">The sealed inventory generation digest.</param>
/// <param name="RequestedAction">The exact elevated action.</param>
/// <param name="ExpiresAt">The server-selected expiry.</param>
public sealed record PermalinkConfirmationChallenge(
    [property: JsonPropertyName("challengeId")] Guid ChallengeId,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("targetDigest")] string TargetDigest,
    [property: JsonPropertyName("inventoryGeneration")] string InventoryGeneration,
    [property: JsonPropertyName("requestedAction")] string RequestedAction,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
