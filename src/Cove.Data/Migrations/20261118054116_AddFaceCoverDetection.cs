using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceCoverDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverDetectionId",
                table: "faces",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_faces_CoverDetectionId",
                table: "faces",
                column: "CoverDetectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_faces_detections_CoverDetectionId",
                table: "faces",
                column: "CoverDetectionId",
                principalTable: "detections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_faces_detections_CoverDetectionId",
                table: "faces");

            migrationBuilder.DropIndex(
                name: "IX_faces_CoverDetectionId",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "CoverDetectionId",
                table: "faces");
        }
    }
}
