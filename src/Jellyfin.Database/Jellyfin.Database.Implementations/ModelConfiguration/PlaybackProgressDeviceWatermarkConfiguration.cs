// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures per-device receipt retention watermarks.</summary>
public sealed class PlaybackProgressDeviceWatermarkConfiguration : IEntityTypeConfiguration<PlaybackProgressDeviceWatermark>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlaybackProgressDeviceWatermark> builder)
    {
        builder.HasKey(value => new { value.UserId, value.DeviceId });
    }
}
