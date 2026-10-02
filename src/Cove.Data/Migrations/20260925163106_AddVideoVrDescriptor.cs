using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoVrDescriptor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VrFieldOfView",
                table: "videos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VrProjection",
                table: "videos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VrStereoMode",
                table: "videos",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VrFieldOfView",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "VrProjection",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "VrStereoMode",
                table: "videos");
        }
    }
}
