using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedFilterUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "saved_filters",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_saved_filters_UserId_Mode",
                table: "saved_filters",
                columns: new[] { "UserId", "Mode" });

            migrationBuilder.AddForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters");

            migrationBuilder.DropIndex(
                name: "IX_saved_filters_UserId_Mode",
                table: "saved_filters");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "saved_filters");
        }
    }
}
