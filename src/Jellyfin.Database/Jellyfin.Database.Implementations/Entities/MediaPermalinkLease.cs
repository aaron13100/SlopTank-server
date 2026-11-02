// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Preserves the existing user-bound, single-use resolution API in the application
/// database. A lease authorizes metadata lookup, never access to a physical file.
/// </summary>
public sealed class MediaPermalinkLease
{
    /// <summary>Gets or sets the opaque candidate handle.</summary>
    public required string Handle { get; set; }

    /// <summary>Gets or sets the digest of the random lease secret, not of media.</summary>
    public required string SecretDigest { get; set; }

    /// <summary>Gets or sets the requested public alias.</summary>
    public required string Alias { get; set; }

    /// <summary>Gets or sets the logical title identity.</summary>
    public Guid ContentId { get; set; }

    /// <summary>Gets or sets the authenticated user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the details or playback purpose.</summary>
    public required string Kind { get; set; }

    /// <summary>Gets or sets the redemption deadline.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Gets or sets when the lease was first consumed.</summary>
    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>Gets or sets the playback session owning a consumed lease.</summary>
    public string? PlaybackSessionId { get; set; }

    /// <summary>Gets or sets the frozen logical queue, encoded as versioned JSON.</summary>
    public string? PlaybackPlan { get; set; }

    /// <summary>Gets or sets when playback completed.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Gets or sets the optimistic concurrency revision.</summary>
    public int Revision { get; set; }

    /// <summary>Gets or sets the persisted format version.</summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the last update time.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Gets or sets the deletion time.</summary>
    public DateTime? DeletedAtUtc { get; set; }
}
