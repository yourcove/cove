using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CoveContext))]
    [Migration("20260708000000_DropLegacyVideoMarkers")]
    public partial class DropLegacyVideoMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rips out the dead legacy VideoMarker/VideoMarkerTag apparatus and relabels the
            // remaining "marker" naming that was actually live Segment data wearing the old name.
            // Nothing reads or writes the video_marker(_tags) tables anymore (Stash markers import
            // straight into segments), so the tables are dropped outright. Written idempotently so it
            // is safe on databases that never had these tables and a no-op once applied.

            // --- drop the dead legacy VideoMarker tables ----------------------------------------------
            // Order matters: video_marker_tags FKs video_markers.
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS video_marker_tags;
                DROP TABLE IF EXISTS video_markers;
                """);

            // --- rename tags."VideoMarkerCount" -> tags."SegmentCount" ---------------------------------
            // The column has always stored the per-tag *segment* count (refreshed from the segments
            // table); only the name was stale. Rename the column and its supporting index in place so
            // the denormalized data and its index are preserved. IF EXISTS makes the rename a no-op
            // on re-run (the old name no longer resolves once applied).
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS tags RENAME COLUMN "VideoMarkerCount" TO "SegmentCount";
                ALTER INDEX IF EXISTS "IX_tags_VideoMarkerCount" RENAME TO "IX_tags_SegmentCount";
                """);

            // --- migrate persisted entity_kind = 'marker' -> 'segment' --------------------------------
            // Segments authorize under the entity-kind string that used to be "marker". Every table that
            // persists an entity kind for that path must be re-pointed so existing share links and role
            // grants keep matching segments after EntityKinds.Marker became EntityKinds.Segment.
            migrationBuilder.Sql("""
                UPDATE share_links           SET "EntityKind" = 'segment' WHERE lower("EntityKind") = 'marker';
                UPDATE role_content_rules    SET "EntityKind" = 'segment' WHERE lower("EntityKind") = 'marker';
                UPDATE role_entity_overrides SET "EntityKind" = 'segment' WHERE lower("EntityKind") = 'marker';
                """);

            // --- drop the dead FilterMode.VideoMarkers enum member (integer 4) -------------------------
            // saved_filters."Mode" persists FilterMode as its integer value. Removing the mid-enum
            // VideoMarkers member shifts every later member down by one, so delete any (dead) saved
            // filters that used it and decrement the modes above it to keep the rest pointing at the
            // same entity type. Ordering: delete the 4s first, then shift 5+ down into 4+.
            migrationBuilder.Sql("""
                DELETE FROM saved_filters WHERE "Mode" = 4;
                UPDATE saved_filters SET "Mode" = "Mode" - 1 WHERE "Mode" > 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the reversible relabeling. The dropped video_marker(_tags) tables are NOT recreated
            // — they were dead and carried no data the app can reconstruct (consistent with how other
            // destructive migrations in this project treat their Down()).
            // Re-open integer slot 4 for VideoMarkers by shifting the modes back up. The deleted
            // marker saved-filter rows are not recoverable (they were dead), matching the lossy
            // treatment of the dropped tables above.
            migrationBuilder.Sql("""
                UPDATE saved_filters SET "Mode" = "Mode" + 1 WHERE "Mode" >= 4;
                """);

            migrationBuilder.Sql("""
                UPDATE role_entity_overrides SET "EntityKind" = 'marker' WHERE lower("EntityKind") = 'segment';
                UPDATE role_content_rules    SET "EntityKind" = 'marker' WHERE lower("EntityKind") = 'segment';
                UPDATE share_links           SET "EntityKind" = 'marker' WHERE lower("EntityKind") = 'segment';
                """);

            migrationBuilder.Sql("""
                ALTER INDEX IF EXISTS "IX_tags_SegmentCount" RENAME TO "IX_tags_VideoMarkerCount";
                ALTER TABLE IF EXISTS tags RENAME COLUMN "SegmentCount" TO "VideoMarkerCount";
                """);
        }
    }
}
