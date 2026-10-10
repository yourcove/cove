using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

// Until now, taking a scraped tag off an audio or text in its edit form, a bulk edit or a later replacing scrape
// removed the tag link but kept the scrape's record of the tag, which then kept the tag on the item as a locked,
// derived tag that the edit form cannot remove. Delete such records: ones without a link that a scrape apply
// wrote (its attempt on this item as run id). A record added through the tag application API or by an
// extension is kept unless it copies that shape exactly. Tags keep no audio or text counts, so nothing is
// recounted.
[DbContext(typeof(CoveContext))]
[Migration("20261010120000_RemoveLeftoverScrapedAudioAndTextTagApplications")]
public sealed class RemoveLeftoverScrapedAudioAndTextTagApplications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM tag_applications AS application
            WHERE application."HostType" IN (9, 10)
              AND application."ContextType" IS NULL
              AND application."ContextId" IS NULL
              AND application."SourceKey" LIKE 'scraper:%'
              AND EXISTS (
                  SELECT 1
                  FROM scrape_attempts AS attempt
                  WHERE attempt."Id"::text = application."SourceRunId"
                    AND attempt."EntityId" = application."HostId"
                    AND lower(attempt."EntityType") = CASE application."HostType" WHEN 9 THEN 'audio' ELSE 'text' END)
              AND NOT (application."HostType" = 9 AND EXISTS (
                  SELECT 1
                  FROM audio_tags AS link
                  WHERE link."AudioId" = application."HostId" AND link."TagId" = application."TagId"))
              AND NOT (application."HostType" = 10 AND EXISTS (
                  SELECT 1
                  FROM text_tags AS link
                  WHERE link."TextDocumentId" = application."HostId" AND link."TagId" = application."TagId"));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The removed records only kept tags the person had already taken off, so they are not restored.
    }
}
