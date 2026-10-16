// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackProgressReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackProgressDeviceWatermarks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    CompactedThroughSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackProgressDeviceWatermarks", x => new { x.UserId, x.DeviceId });
                });

            migrationBuilder.CreateTable(
                name: "PlaybackProgressReceipts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultPositionTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultPlayed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResultPlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultLastPlayedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackProgressReceipts", x => new { x.UserId, x.DeviceId, x.OperationId });
                });

            migrationBuilder.CreateTable(
                name: "PlaybackProgressRevisions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackProgressRevisions", x => new { x.UserId, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "UX_PlaybackProgressReceipts_UserId_DeviceId_Sequence",
                table: "PlaybackProgressReceipts",
                columns: new[] { "UserId", "DeviceId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackProgressDeviceWatermarks");

            migrationBuilder.DropTable(
                name: "PlaybackProgressReceipts");

            migrationBuilder.DropTable(
                name: "PlaybackProgressRevisions");
        }
    }
}
