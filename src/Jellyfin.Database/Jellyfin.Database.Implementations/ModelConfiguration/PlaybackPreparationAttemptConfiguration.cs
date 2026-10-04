// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures durable playback preparation attempts.</summary>
public sealed class PlaybackPreparationAttemptConfiguration : IEntityTypeConfiguration<PlaybackPreparationAttemptEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlaybackPreparationAttemptEntity> builder)
    {
        builder.HasKey(value => value.RequestId);
        builder.Property(value => value.NonceHash).HasMaxLength(64).IsRequired();
        builder.Property(value => value.RequestedObjectIdentity).HasMaxLength(1024).IsRequired();
        builder.Property(value => value.RequestedPath).HasMaxLength(4096).IsRequired();
        builder.Property(value => value.Outcome).HasMaxLength(32);
        builder.Property(value => value.ReasonCode).HasMaxLength(64);
        builder.HasIndex(value => new { value.ItemId, value.Phase, value.LeaseExpiresAtUtc });
        builder.HasIndex(value => value.ItemId)
            .IsUnique()
            .HasDatabaseName("IX_PlaybackPreparationAttempts_ActiveItem")
            .HasFilter("\"Phase\" IN (0, 1)");
    }
}
