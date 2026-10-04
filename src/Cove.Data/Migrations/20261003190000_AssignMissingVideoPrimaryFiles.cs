using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

// Stash imports in 1.5.0 and 1.5.1 left every imported video without a primary file, so none of them
// could play. Give each such video its first video file, as AddVideoPrimaryFile did originally.
[DbContext(typeof(CoveContext))]
[Migration("20261003190000_AssignMissingVideoPrimaryFiles")]
public sealed class AssignMissingVideoPrimaryFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE videos AS video
            SET "PrimaryFileId" = first_file."Id"
            FROM (
                SELECT file."VideoId", MIN(file."Id") AS "Id"
                FROM files AS file
                WHERE file."VideoId" IS NOT NULL AND file."FileType" = 'Video'
                GROUP BY file."VideoId"
            ) AS first_file
            WHERE first_file."VideoId" = video."Id" AND video."PrimaryFileId" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The assigned primaries are indistinguishable from ones chosen later, so they stay.
    }
}
