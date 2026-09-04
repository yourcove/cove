using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeVideoFingerprintLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_files_Id_VideoId_video",
                table: "files",
                column: "Id",
                filter: "\"VideoId\" IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "VideoId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_files_Id_VideoId_video",
                table: "files");
        }
    }
}
