using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class CascadeUserOwnedDataOnDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM face_suggestion_decisions row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM interactions row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM playback_intervals row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM playback_sessions row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM ratings row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM segment_display_rules row WHERE row."UserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM segment_display_profiles row WHERE row."UserId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM user_bookmarks row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM user_entity_affinities row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                DELETE FROM user_sessions row WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u."Id" = row."UserId");
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters");

            migrationBuilder.AddForeignKey(
                name: "FK_face_suggestion_decisions_users_UserId",
                table: "face_suggestion_decisions",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_interactions_users_UserId",
                table: "interactions",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_playback_intervals_users_UserId",
                table: "playback_intervals",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_playback_sessions_users_UserId",
                table: "playback_sessions",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ratings_users_UserId",
                table: "ratings",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_segment_display_profiles_users_UserId",
                table: "segment_display_profiles",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_segment_display_rules_users_UserId",
                table: "segment_display_rules",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_bookmarks_users_UserId",
                table: "user_bookmarks",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_entity_affinities_users_UserId",
                table: "user_entity_affinities",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_sessions_users_UserId",
                table: "user_sessions",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_face_suggestion_decisions_users_UserId",
                table: "face_suggestion_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_interactions_users_UserId",
                table: "interactions");

            migrationBuilder.DropForeignKey(
                name: "FK_playback_intervals_users_UserId",
                table: "playback_intervals");

            migrationBuilder.DropForeignKey(
                name: "FK_playback_sessions_users_UserId",
                table: "playback_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_ratings_users_UserId",
                table: "ratings");

            migrationBuilder.DropForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters");

            migrationBuilder.DropForeignKey(
                name: "FK_segment_display_profiles_users_UserId",
                table: "segment_display_profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_segment_display_rules_users_UserId",
                table: "segment_display_rules");

            migrationBuilder.DropForeignKey(
                name: "FK_user_bookmarks_users_UserId",
                table: "user_bookmarks");

            migrationBuilder.DropForeignKey(
                name: "FK_user_entity_affinities_users_UserId",
                table: "user_entity_affinities");

            migrationBuilder.DropForeignKey(
                name: "FK_user_sessions_users_UserId",
                table: "user_sessions");

            migrationBuilder.AddForeignKey(
                name: "FK_saved_filters_users_UserId",
                table: "saved_filters",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
