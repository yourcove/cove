using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneCaptionsAndInteractiveSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Captions",
                table: "scenes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InteractiveSpeed",
                table: "scenes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Captions",
                table: "scenes");

            migrationBuilder.DropColumn(
                name: "InteractiveSpeed",
                table: "scenes");
        }
    }
}
