using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "ExcludePaths",
                table: "duplicate_searches",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "IncludePaths",
                table: "duplicate_searches",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "KeeperRulesJson",
                table: "duplicate_searches",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinimumDuration",
                table: "duplicate_searches",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "DecisionRule",
                table: "duplicate_search_groups",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionSource",
                table: "duplicate_search_groups",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeleteFiles",
                table: "duplicate_search_groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DeleteGenerated",
                table: "duplicate_search_groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "duplicate_search_groups",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedAt",
                table: "duplicate_search_groups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemovedBytes",
                table: "duplicate_search_groups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RemovedVideoCount",
                table: "duplicate_search_groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionAction",
                table: "duplicate_search_groups",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "duplicate_search_groups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "duplicate_search_groups",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unresolved");

            migrationBuilder.CreateTable(
                name: "duplicate_ignored_pairs",
                columns: table => new
                {
                    LowVideoId = table.Column<int>(type: "integer", nullable: false),
                    HighVideoId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_ignored_pairs", x => new { x.LowVideoId, x.HighVideoId });
                    table.CheckConstraint("CK_duplicate_ignored_pairs_ordered", "\"LowVideoId\" < \"HighVideoId\"");
                    table.ForeignKey(
                        name: "FK_duplicate_ignored_pairs_videos_HighVideoId",
                        column: x => x.HighVideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_duplicate_ignored_pairs_videos_LowVideoId",
                        column: x => x.LowVideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_search_groups_SearchId_Status",
                table: "duplicate_search_groups",
                columns: new[] { "SearchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_ignored_pairs_HighVideoId",
                table: "duplicate_ignored_pairs",
                column: "HighVideoId");

            // The previous workflow claimed a whole search for one deletion job. Those in-memory jobs cannot
            // outlive the upgrade restart, and groups now carry their own review state.
            migrationBuilder.Sql("""
                UPDATE duplicate_searches SET "DeletionJobId" = NULL WHERE "DeletionJobId" IS NOT NULL;
                DELETE FROM duplicate_deletion_keeper_reservations;
                UPDATE duplicate_search_groups SET "DecisionSource" = 'auto';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_ignored_pairs");

            migrationBuilder.DropIndex(
                name: "IX_duplicate_search_groups_SearchId_Status",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "ExcludePaths",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "IncludePaths",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "KeeperRulesJson",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "MinimumDuration",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "DecisionRule",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "DecisionSource",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "DeleteFiles",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "DeleteGenerated",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "RemovedBytes",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "RemovedVideoCount",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "ResolutionAction",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "duplicate_search_groups");
        }
    }
}
