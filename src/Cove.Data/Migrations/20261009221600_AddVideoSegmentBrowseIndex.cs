using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20261009221600_AddVideoSegmentBrowseIndex")]
public sealed class AddVideoSegmentBrowseIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Early adopters may already have the same index under the local name. Adopt it instead
        // of building a second index across a multi-million-row segments table.
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF to_regclass('public.ix_local_segments_video_updated_id') IS NOT NULL
                    AND to_regclass('public.ix_segments_video_updated_id') IS NULL THEN
                    ALTER INDEX public.ix_local_segments_video_updated_id RENAME TO ix_segments_video_updated_id;
                END IF;
            END $$;
            """);

        // The unfiltered Raw segments page requests newest video segments first. Applying this
        // transactionally lets a failed migration roll back without leaving an invalid index.
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_segments_video_updated_id
            ON public.segments ("UpdatedAt" DESC, "Id" DESC)
            WHERE "HostType" = 1;
            """);

        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS public.ix_local_segments_video_updated_id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS public.ix_segments_video_updated_id;
            """);
    }
}
