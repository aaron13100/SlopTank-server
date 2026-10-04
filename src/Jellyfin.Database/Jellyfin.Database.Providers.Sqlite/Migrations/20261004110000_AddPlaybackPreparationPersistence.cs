// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
#pragma warning disable CS1591
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations;

public partial class AddPlaybackPreparationPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PlaybackReadyObjectIdentity",
            table: "BaseItems",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlaybackReadyAlgorithmVersion",
            table: "BaseItems",
            type: "TEXT",
            nullable: true);
        migrationBuilder.CreateTable(
            name: "PlaybackPreparationAttempts",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                NonceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RequestedObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                RequestedPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                Phase = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                ReasonCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PlanJson = table.Column<string>(type: "TEXT", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_PlaybackPreparationAttempts", x => x.RequestId));
        migrationBuilder.CreateIndex(
            name: "IX_PlaybackPreparationAttempts_ItemId_Phase_LeaseExpiresAtUtc",
            table: "PlaybackPreparationAttempts",
            columns: new[] { "ItemId", "Phase", "LeaseExpiresAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_PlaybackPreparationAttempts_ActiveItem",
            table: "PlaybackPreparationAttempts",
            column: "ItemId",
            unique: true,
            filter: "\"Phase\" IN (0, 1)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PlaybackPreparationAttempts");
        migrationBuilder.DropColumn(name: "PlaybackReadyObjectIdentity", table: "BaseItems");
        migrationBuilder.DropColumn(name: "PlaybackReadyAlgorithmVersion", table: "BaseItems");
    }
}
