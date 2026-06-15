using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <summary>
    /// Drops the per-entity "IgnoreAutoTag" opt-out flag from performers, studios, and tags.
    /// The auto-tag feature has been removed, so the flag no longer has any effect.
    /// </summary>
    [DbContext(typeof(CoveContext))]
    [Migration("20260615000000_DropIgnoreAutoTag")]
    public partial class DropIgnoreAutoTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.performers DROP COLUMN IF EXISTS \"IgnoreAutoTag\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.studios DROP COLUMN IF EXISTS \"IgnoreAutoTag\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.tags DROP COLUMN IF EXISTS \"IgnoreAutoTag\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.performers ADD COLUMN \"IgnoreAutoTag\" boolean NOT NULL DEFAULT FALSE;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.studios ADD COLUMN \"IgnoreAutoTag\" boolean NOT NULL DEFAULT FALSE;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.tags ADD COLUMN \"IgnoreAutoTag\" boolean NOT NULL DEFAULT FALSE;");
        }
    }
}
