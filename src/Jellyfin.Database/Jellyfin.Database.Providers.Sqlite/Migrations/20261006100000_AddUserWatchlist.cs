// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
#pragma warning disable CS1591
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations;

public partial class AddUserWatchlist : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserWatchlists",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_UserWatchlists", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_UserWatchlists_ItemId",
            table: "UserWatchlists",
            column: "ItemId");
        migrationBuilder.CreateIndex(
            name: "UX_UserWatchlists_UserId_ItemId",
            table: "UserWatchlists",
            columns: new[] { "UserId", "ItemId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserWatchlists");
    }
}
