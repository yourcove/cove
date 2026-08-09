using Cove.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260808123000_SavedFilterStringModes")]
public sealed class SavedFilterStringModes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_saved_filters_UserId_Mode",
            table: "saved_filters");

        migrationBuilder.Sql("""
            ALTER TABLE saved_filters
            ALTER COLUMN "Mode" TYPE character varying(200)
            USING CASE "Mode"
                WHEN 0 THEN 'videos'
                WHEN 1 THEN 'performers'
                WHEN 2 THEN 'studios'
                WHEN 3 THEN 'galleries'
                WHEN 4 THEN 'groups'
                WHEN 5 THEN 'tags'
                WHEN 6 THEN 'images'
                WHEN 7 THEN 'audios'
                WHEN 8 THEN 'faces'
                WHEN 9 THEN 'texts'
                WHEN 10 THEN 'segments'
                WHEN 11 THEN 'rawsegments'
                WHEN 12 THEN 'groupitems'
                ELSE 'legacy:' || "Mode"::text
            END;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_saved_filters_UserId_Mode",
            table: "saved_filters",
            columns: ["UserId", "Mode"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_saved_filters_UserId_Mode",
            table: "saved_filters");

        migrationBuilder.Sql("""
            DELETE FROM saved_filters
            WHERE "Mode" NOT IN (
                'videos', 'performers', 'studios', 'galleries', 'groups', 'tags', 'images',
                'audios', 'faces', 'texts', 'segments', 'rawsegments', 'groupitems'
            );
            ALTER TABLE saved_filters
            ALTER COLUMN "Mode" TYPE integer
            USING CASE "Mode"
                WHEN 'videos' THEN 0
                WHEN 'performers' THEN 1
                WHEN 'studios' THEN 2
                WHEN 'galleries' THEN 3
                WHEN 'groups' THEN 4
                WHEN 'tags' THEN 5
                WHEN 'images' THEN 6
                WHEN 'audios' THEN 7
                WHEN 'faces' THEN 8
                WHEN 'texts' THEN 9
                WHEN 'segments' THEN 10
                WHEN 'rawsegments' THEN 11
                WHEN 'groupitems' THEN 12
            END;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_saved_filters_UserId_Mode",
            table: "saved_filters",
            columns: ["UserId", "Mode"]);
    }
}
