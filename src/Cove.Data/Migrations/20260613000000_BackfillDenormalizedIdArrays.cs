using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <summary>
    /// Resynchronizes the denormalized Video/Image/Gallery TagIds/PerformerIds int[] arrays from the
    /// authoritative join tables. Detail-page lists filter on these arrays (e.g. a performer's videos via
    /// videos."PerformerIds"), while the tab counts are computed straight from the join tables. Any row whose
    /// array drifted out of sync with its join rows shows a count but an empty list (or vice versa). This
    /// rebuilds every array to exactly match its join table, repairing data that predates the denormalization
    /// or was written through a path that didn't refresh the arrays.
    ///
    /// Idempotent: each statement only touches rows whose array differs from the join-derived value, so it is
    /// safe to re-run and cheap when nothing has drifted.
    /// </summary>
    [DbContext(typeof(CoveContext))]
    [Migration("20260613000000_BackfillDenormalizedIdArrays")]
    public partial class BackfillDenormalizedIdArrays : Migration
    {
        private static string ResyncSql(string parentTable, string arrayColumn, string joinTable, string parentKey, string childKey) => $@"
            UPDATE public.{parentTable} p
            SET ""{arrayColumn}"" = COALESCE(agg.ids, ARRAY[]::integer[])
            FROM public.{parentTable} p2
            LEFT JOIN LATERAL (
                SELECT array_agg(DISTINCT j.""{childKey}"" ORDER BY j.""{childKey}"") AS ids
                FROM public.{joinTable} j
                WHERE j.""{parentKey}"" = p2.""Id""
            ) agg ON TRUE
            WHERE p.""Id"" = p2.""Id""
              AND p.""{arrayColumn}"" IS DISTINCT FROM COALESCE(agg.ids, ARRAY[]::integer[]);";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ResyncSql("videos", "PerformerIds", "video_performers", "VideoId", "PerformerId"));
            migrationBuilder.Sql(ResyncSql("videos", "TagIds", "video_tags", "VideoId", "TagId"));
            migrationBuilder.Sql(ResyncSql("images", "PerformerIds", "image_performers", "ImageId", "PerformerId"));
            migrationBuilder.Sql(ResyncSql("images", "TagIds", "image_tags", "ImageId", "TagId"));
            migrationBuilder.Sql(ResyncSql("galleries", "PerformerIds", "gallery_performers", "GalleryId", "PerformerId"));
            migrationBuilder.Sql(ResyncSql("galleries", "TagIds", "gallery_tags", "GalleryId", "TagId"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only repair: the resynced arrays are the correct values, so there is nothing to roll back.
        }
    }
}
