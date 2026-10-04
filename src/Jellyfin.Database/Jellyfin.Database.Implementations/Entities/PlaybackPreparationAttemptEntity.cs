// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
#pragma warning disable CS1591
using System;

namespace Jellyfin.Database.Implementations.Entities;

public enum PlaybackPreparationAttemptPhase
{
    Claimed = 0,
    Preparing = 1,
    Completed = 2,
    Failed = 3,
    Expired = 4
}

public class PlaybackPreparationAttemptEntity
{
    public required Guid RequestId { get; set; }

    public required Guid ItemId { get; set; }

    public required string NonceHash { get; set; }

    public required string RequestedObjectIdentity { get; set; }

    public required string RequestedPath { get; set; }

    public PlaybackPreparationAttemptPhase Phase { get; set; }

    public required DateTime CreatedAtUtc { get; set; }

    public required DateTime LeaseExpiresAtUtc { get; set; }

    public string? Outcome { get; set; }

    public string? ReasonCode { get; set; }

    public string? PlanJson { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
