// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures offline playback progress operation receipts.</summary>
public sealed class PlaybackProgressReceiptConfiguration : IEntityTypeConfiguration<PlaybackProgressReceipt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlaybackProgressReceipt> builder)
    {
        builder.HasKey(value => new { value.UserId, value.DeviceId, value.OperationId });

        // A sequence number identifies at most one operation per device.
        builder.HasIndex(value => new { value.UserId, value.DeviceId, value.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_PlaybackProgressReceipts_UserId_DeviceId_Sequence");
    }
}
