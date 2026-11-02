// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures durable compatibility leases without a separate authority store.</summary>
public sealed class MediaPermalinkLeaseConfiguration : IEntityTypeConfiguration<MediaPermalinkLease>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaPermalinkLease> builder)
    {
        builder.HasKey(lease => lease.Handle);
        builder.Property(lease => lease.Handle).HasMaxLength(64);
        builder.Property(lease => lease.SecretDigest).HasMaxLength(64).IsRequired();
        builder.Property(lease => lease.Alias).HasMaxLength(512).IsRequired();
        builder.Property(lease => lease.Kind).HasMaxLength(16).IsRequired();
        builder.Property(lease => lease.PlaybackSessionId).HasMaxLength(128);
        builder.Property(lease => lease.Revision).IsConcurrencyToken();
        builder.Property(lease => lease.RecordVersion).HasDefaultValue(1);
        builder.HasIndex(lease => lease.ExpiresAtUtc);
        builder.ToTable("MediaPermalinkLeases", table => table.HasCheckConstraint(
            "CK_MediaPermalinkLeases_Kind", "\"Kind\" IN ('details', 'playback')"));
    }
}
