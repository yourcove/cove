using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateSearchJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duplicate_searches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    JobId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MatchType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Distance = table.Column<int>(type: "integer", nullable: false),
                    DurationDifference = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    GroupCount = table.Column<int>(type: "integer", nullable: false),
                    VideoCount = table.Column<int>(type: "integer", nullable: false),
                    DeletionJobId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_searches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_search_groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_search_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_duplicate_search_groups_duplicate_searches_SearchId",
                        column: x => x.SearchId,
                        principalTable: "duplicate_searches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_search_items",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    VideoId = table.Column<int>(type: "integer", nullable: false),
                    Keep = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_search_items", x => new { x.GroupId, x.VideoId });
                    table.ForeignKey(
                        name: "FK_duplicate_search_items_duplicate_search_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "duplicate_search_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_duplicate_search_items_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_search_groups_SearchId_Position",
                table: "duplicate_search_groups",
                columns: new[] { "SearchId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_search_items_VideoId",
                table: "duplicate_search_items",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_searches_ExpiresAt",
                table: "duplicate_searches",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_searches_OwnerKey_CreatedAt",
                table: "duplicate_searches",
                columns: new[] { "OwnerKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_search_items");

            migrationBuilder.DropTable(
                name: "duplicate_search_groups");

            migrationBuilder.DropTable(
                name: "duplicate_searches");
        }
    }
}
