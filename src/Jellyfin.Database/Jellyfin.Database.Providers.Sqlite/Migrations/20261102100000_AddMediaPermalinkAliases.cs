// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-04.
#pragma warning disable CS1591

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations;

public partial class AddMediaPermalinkAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "PermalinkContentId",
            table: "BaseItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_BaseItems_PermalinkContentId",
            table: "BaseItems",
            column: "PermalinkContentId");

        migrationBuilder.CreateTable(
            name: "MediaPermalinkAliases",
            columns: table => new
            {
                Alias = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                ContentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: true),
                RecordVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaPermalinkAliases", x => new { x.Alias, x.ContentId });
                table.CheckConstraint(
                    "CK_MediaPermalinkAliases_Kind",
                    "\"Kind\" IN ('movie', 'series', 'season', 'episode', 'boxset', 'video', 'musicvideo')");
                table.CheckConstraint(
                    "CK_MediaPermalinkAliases_Ordinal",
                    "(\"IsPublic\" = 1 AND \"Ordinal\" >= 0) OR (\"IsPublic\" = 0 AND \"Ordinal\" IS NULL)");
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaPermalinkAliases_Alias",
            table: "MediaPermalinkAliases",
            column: "Alias");

        migrationBuilder.CreateIndex(
            name: "IX_MediaPermalinkAliases_ContentId_Ordinal",
            table: "MediaPermalinkAliases",
            columns: new[] { "ContentId", "Ordinal" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MediaPermalinkAliases");
        migrationBuilder.DropIndex(name: "IX_BaseItems_PermalinkContentId", table: "BaseItems");
        migrationBuilder.DropColumn(name: "PermalinkContentId", table: "BaseItems");
    }
}
