using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoPlayHistoryOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "video_play_history",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "video_play_history"
                SET "UserId" = (
                    SELECT "Id"
                    FROM "users"
                    WHERE "IsSystem" = TRUE AND "IsActive" = TRUE AND "IsLocked" = FALSE
                    ORDER BY "Id"
                    LIMIT 1
                )
                WHERE "UserId" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_video_play_history_UserId_VideoId_PlayedAt",
                table: "video_play_history",
                columns: new[] { "UserId", "VideoId", "PlayedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_video_play_history_users_UserId",
                table: "video_play_history",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_video_play_history_users_UserId",
                table: "video_play_history");

            migrationBuilder.DropIndex(
                name: "IX_video_play_history_UserId_VideoId_PlayedAt",
                table: "video_play_history");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "video_play_history");
        }
    }
}
