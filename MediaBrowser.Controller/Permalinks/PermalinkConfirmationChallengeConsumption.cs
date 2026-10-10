// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Identifies the exact challenge and operation consumed by a successful transaction.</summary>
/// <param name="ChallengeId">The consumed challenge identifier.</param>
/// <param name="OperationId">The authorized durable operation identifier.</param>
public sealed record PermalinkConfirmationChallengeConsumption(
    Guid ChallengeId,
    Guid OperationId);
