// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <summary>
    /// Adds BaseItems.IngestState (Complete = 0, Pending = 1, ProbeFailed = 2). Additive only: every
    /// existing row takes the default 0 (Complete), so no existing item changes visibility.
    /// </summary>
    /// <inheritdoc />
    public partial class AddIngestStateToBaseItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IngestState",
                table: "BaseItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IngestState",
                table: "BaseItems");
        }
    }
}
