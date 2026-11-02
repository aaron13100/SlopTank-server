// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// Configures durable logical media aliases.
/// </summary>
public sealed class MediaPermalinkAliasConfiguration : IEntityTypeConfiguration<MediaPermalinkAlias>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaPermalinkAlias> builder)
    {
        builder.HasKey(alias => new { alias.Alias, alias.ContentId });
        builder.Property(alias => alias.Alias).HasMaxLength(512).IsRequired();
        builder.Property(alias => alias.Kind).HasMaxLength(32).IsRequired();
        builder.Property(alias => alias.RecordVersion).HasDefaultValue(1);
        builder.HasIndex(alias => alias.Alias);
        builder.HasIndex(alias => new { alias.ContentId, alias.Ordinal }).IsUnique();
        builder.ToTable("MediaPermalinkAliases", table =>
        {
            table.HasCheckConstraint(
                "CK_MediaPermalinkAliases_Kind",
                "\"Kind\" IN ('movie', 'series', 'season', 'episode', 'boxset', 'video', 'musicvideo')");
            table.HasCheckConstraint(
                "CK_MediaPermalinkAliases_Ordinal",
                "(\"IsPublic\" = 1 AND \"Ordinal\" >= 0) OR (\"IsPublic\" = 0 AND \"Ordinal\" IS NULL)");
        });
    }
}
