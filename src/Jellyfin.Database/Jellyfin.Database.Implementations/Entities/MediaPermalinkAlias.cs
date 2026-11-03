// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03.
using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Maps a permanent permalink alias to a logical media content identity.
/// </summary>
public sealed class MediaPermalinkAlias
{
    /// <summary>Gets or sets the public alias or private coordinate key.</summary>
    public required string Alias { get; set; }

    /// <summary>Gets or sets the logical content identity.</summary>
    public required Guid ContentId { get; set; }

    /// <summary>Gets or sets the logical content kind.</summary>
    public required string Kind { get; set; }

    /// <summary>Gets or sets a value indicating whether this alias is public.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Gets or sets the alias order, or null for private keys.</summary>
    public int? Ordinal { get; set; }

    /// <summary>Gets or sets the persisted record format version.</summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>Gets or sets when this alias was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets when this alias was last updated.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Gets or sets when this alias was soft-deleted.</summary>
    public DateTime? DeletedAtUtc { get; set; }
}
