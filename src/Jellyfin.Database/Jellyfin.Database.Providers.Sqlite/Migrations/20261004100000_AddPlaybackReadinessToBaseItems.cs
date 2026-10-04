// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations;

/// <summary>
/// Adds the server-owned playback-readiness admission state. Existing rows remain visible until
/// the compatibility worker positively classifies them; newly scanned primary videos are assigned
/// Pending by LibraryManager before persistence.
/// </summary>
public partial class AddPlaybackReadinessToBaseItems : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PlaybackReadiness",
            table: "BaseItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PlaybackReadiness",
            table: "BaseItems");
    }
}
