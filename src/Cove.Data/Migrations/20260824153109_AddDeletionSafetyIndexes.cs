using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletionSafetyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_group_items_Kind_HostId",
                table: "group_items",
                columns: new[] { "Kind", "HostId" });

            migrationBuilder.CreateIndex(
                name: "IX_segments_Kind_RefId",
                table: "segments",
                columns: new[] { "Kind", "RefId" });

            migrationBuilder.CreateIndex(
                name: "IX_tag_applications_ContextType_ContextId",
                table: "tag_applications",
                columns: new[] { "ContextType", "ContextId" });

            migrationBuilder.Sql("CREATE INDEX \"IX_files_Path_upper\" ON \"files\" (upper(\"Path\"));");
            migrationBuilder.Sql("CREATE INDEX \"IX_segments_Kind_lower_RefId\" ON \"segments\" (lower(\"Kind\"), \"RefId\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_detections_RefKind_lower_RefId\" ON \"detections\" (lower(\"RefKind\"), \"RefId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_detections_RefKind_lower_RefId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_segments_Kind_lower_RefId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_files_Path_upper\";");

            migrationBuilder.DropIndex(
                name: "IX_group_items_Kind_HostId",
                table: "group_items");

            migrationBuilder.DropIndex(
                name: "IX_segments_Kind_RefId",
                table: "segments");

            migrationBuilder.DropIndex(
                name: "IX_tag_applications_ContextType_ContextId",
                table: "tag_applications");
        }
    }
}
