// SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
#pragma warning disable CS1591

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations;

public partial class AddPermalinkRemovalState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LastRemovalCause",
            table: "MediaPermalinkAliases",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "LastRemovalRecordedAtUtc",
            table: "MediaPermalinkAliases",
            type: "TEXT",
            nullable: true);

        // Existing title joins also retained empty source claims. Preserve
        // every row and URL, while letting the current item-backed claim own
        // the administrative removal record. Current ambiguities stay intact.
        migrationBuilder.Sql(
            """
            UPDATE "MediaPermalinkAliases" AS previous
            SET "DeletedAtUtc" = CURRENT_TIMESTAMP, "UpdatedAtUtc" = CURRENT_TIMESTAMP
            WHERE previous."IsPublic" = 1 AND previous."DeletedAtUtc" IS NULL
              AND NOT EXISTS (SELECT 1 FROM "BaseItems" AS oldItem
                              WHERE oldItem."PermalinkContentId" = previous."ContentId")
              AND EXISTS (
                  SELECT 1 FROM "MediaPermalinkAliases" AS currentAlias
                  JOIN "BaseItems" AS currentItem ON currentItem."PermalinkContentId" = currentAlias."ContentId"
                  WHERE currentAlias."Alias" = previous."Alias" AND currentAlias."IsPublic" = 1
                    AND currentAlias."DeletedAtUtc" IS NULL AND currentAlias."ContentId" <> previous."ContentId"
                    AND (currentAlias."Kind" = previous."Kind"
                         OR (previous."Kind" = 'video' AND currentAlias."Kind" IN ('movie', 'episode', 'musicvideo'))))
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LastRemovalCause", table: "MediaPermalinkAliases");
        migrationBuilder.DropColumn(name: "LastRemovalRecordedAtUtc", table: "MediaPermalinkAliases");
    }
}
