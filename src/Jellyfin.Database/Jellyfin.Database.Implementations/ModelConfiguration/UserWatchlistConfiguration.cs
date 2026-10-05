// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures per-user watchlist ("My List") entries.</summary>
public sealed class UserWatchlistConfiguration : IEntityTypeConfiguration<UserWatchlist>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserWatchlist> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();

        // Idempotency: a user can hold an item only once. The unique composite
        // index also serves every per-user lookup through its leftmost column.
        builder.HasIndex(value => new { value.UserId, value.ItemId })
            .IsUnique()
            .HasDatabaseName("UX_UserWatchlists_UserId_ItemId");

        // ItemId lookups (reverse membership queries) do not share a prefix
        // with the composite index, so they get their own index.
        builder.HasIndex(value => value.ItemId)
            .HasDatabaseName("IX_UserWatchlists_ItemId");

        // Deliberately no navigation and no cascade delete: membership rows
        // survive user-independent item removal and are filtered at read time.
    }
}
