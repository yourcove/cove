using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

// From 1.5.0, taking a tag off by hand in a tagger review whose scrape or metadata-server import also
// returned that tag removed the video's tag link but saved the import's own record of the tag, which then
// kept the tag on the video as a locked, derived tag that the edit form cannot remove. Delete such records:
// ones without a link that a scrape apply wrote (its attempt on this video as run id) or a metadata-server
// import wrote (the endpoint as both source and run id). This also clears the same leftovers from a later
// replace or overwrite by another source. A record added through the tag application API or by an
// extension is kept unless it copies one of those shapes exactly. The affected tags' video counts, which
// include records, are recounted the way CoveContext does.
[DbContext(typeof(CoveContext))]
[Migration("20261010090000_RemoveLeftoverImportedVideoTagApplications")]
public sealed class RemoveLeftoverImportedVideoTagApplications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE leftover_tag_applications AS
            SELECT application."Id", application."TagId"
            FROM tag_applications AS application
            WHERE application."HostType" = 1
              AND application."ContextType" IS NULL
              AND application."ContextId" IS NULL
              AND ((application."SourceKey" LIKE 'scraper:%'
                    AND EXISTS (
                        SELECT 1
                        FROM scrape_attempts AS attempt
                        WHERE attempt."Id"::text = application."SourceRunId"
                          AND attempt."EntityId" = application."HostId"
                          AND lower(attempt."EntityType") = 'video'))
                   OR (application."SourceRunId" <> '' AND application."SourceKey" = 'metadata:' || application."SourceRunId"))
              AND NOT EXISTS (
                  SELECT 1
                  FROM video_tags AS link
                  WHERE link."VideoId" = application."HostId" AND link."TagId" = application."TagId");

            DELETE FROM tag_applications
            WHERE "Id" IN (SELECT "Id" FROM leftover_tag_applications);

            UPDATE tags AS tag
            SET "VideoCount" = (
                SELECT COUNT(*)
                FROM (
                    SELECT link."VideoId"
                    FROM video_tags AS link
                    WHERE link."TagId" = tag."Id"
                    UNION
                    SELECT application."HostId"
                    FROM tag_applications AS application
                    WHERE application."HostType" = 1
                      AND application."TagId" = tag."Id"
                      AND application."ContextType" IS NULL
                      AND application."ContextId" IS NULL
                      AND ((tag."MinOccurrenceSec" IS NULL AND tag."MinOccurrencePercent" IS NULL)
                           OR (tag."MinOccurrenceSec" IS NOT NULL
                               AND application."TotalDurationSec" IS NOT NULL
                               AND application."TotalDurationSec" >= tag."MinOccurrenceSec")
                           OR (tag."MinOccurrencePercent" IS NOT NULL
                               AND application."TotalDurationSec" IS NOT NULL
                               AND application."HostDurationSec" IS NOT NULL
                               AND application."HostDurationSec" > 0
                               AND application."TotalDurationSec" * 100 / application."HostDurationSec" >= tag."MinOccurrencePercent"))
                ) AS host)
            WHERE tag."Id" IN (SELECT "TagId" FROM leftover_tag_applications);

            DROP TABLE leftover_tag_applications;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The removed records only kept tags the person had already taken off, so they are not restored.
    }
}
