using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <summary>
    /// Removes the "interactive" (funscript-presence) metadata from the core app. Interactive
    /// file support will return later as an extension. Drops the per-file Interactive/InteractiveSpeed
    /// flags from the shared files table and the denormalized Has(Non)InteractiveFiles/InteractiveSpeed
    /// columns (and their indexes) from videos. Dropping the columns also drops their dependent indexes.
    /// </summary>
    [DbContext(typeof(CoveContext))]
    [Migration("20260617000000_RemoveInteractiveFields")]
    public partial class RemoveInteractiveFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos DROP COLUMN IF EXISTS \"HasInteractiveFiles\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos DROP COLUMN IF EXISTS \"HasNonInteractiveFiles\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos DROP COLUMN IF EXISTS \"InteractiveSpeed\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.files DROP COLUMN IF EXISTS \"Interactive\";");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.files DROP COLUMN IF EXISTS \"InteractiveSpeed\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos ADD COLUMN \"HasInteractiveFiles\" boolean NOT NULL DEFAULT FALSE;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos ADD COLUMN \"HasNonInteractiveFiles\" boolean NOT NULL DEFAULT FALSE;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.videos ADD COLUMN \"InteractiveSpeed\" integer;");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_videos_HasInteractiveFiles\" ON public.videos (\"HasInteractiveFiles\");");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_videos_HasNonInteractiveFiles\" ON public.videos (\"HasNonInteractiveFiles\");");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.files ADD COLUMN \"Interactive\" boolean;");
            migrationBuilder.Sql("ALTER TABLE IF EXISTS public.files ADD COLUMN \"InteractiveSpeed\" integer;");
        }
    }
}
