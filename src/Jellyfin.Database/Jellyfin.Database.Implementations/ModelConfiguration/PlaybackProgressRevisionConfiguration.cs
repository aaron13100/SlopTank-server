// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures the per-user, per-item playback progress revision.</summary>
public sealed class PlaybackProgressRevisionConfiguration : IEntityTypeConfiguration<PlaybackProgressRevision>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlaybackProgressRevision> builder)
    {
        // Deliberately no navigation and no cascade delete (see the entity documentation).
        builder.HasKey(value => new { value.UserId, value.ItemId });
    }
}
