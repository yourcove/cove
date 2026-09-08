using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoreDuplicateManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FingerprintAlgorithm",
                table: "duplicate_searches",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "any");

            migrationBuilder.AddColumn<string>(
                name: "FolderMode",
                table: "duplicate_searches",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "all");

            migrationBuilder.AddColumn<string>(
                name: "FolderPathsJson",
                table: "duplicate_searches",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "KeeperRulesJson",
                table: "duplicate_searches",
                type: "jsonb",
                nullable: false,
                defaultValue: "[\"resolution\",\"codec\",\"bitrate\",\"duration\",\"metadata\",\"oldest\"]");

            migrationBuilder.AddColumn<double>(
                name: "MinimumDuration",
                table: "duplicate_searches",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "PreferredCodecsJson",
                table: "duplicate_searches",
                type: "jsonb",
                nullable: false,
                defaultValue: "[\"av1\",\"hevc\",\"h264\",\"vp9\",\"mpeg4\"]");

            migrationBuilder.AddColumn<string>(
                name: "RankingMode",
                table: "duplicate_searches",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "balanced");

            migrationBuilder.AddColumn<string>(
                name: "RecommendationReason",
                table: "duplicate_search_groups",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecommendedVideoId",
                table: "duplicate_search_groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskNotesJson",
                table: "duplicate_search_groups",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "RiskScore",
                table: "duplicate_search_groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "image_duplicate_searches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    JobId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MinimumBytes = table.Column<long>(type: "bigint", nullable: false),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    GroupCount = table.Column<int>(type: "integer", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    FreeableBytes = table.Column<long>(type: "bigint", nullable: false),
                    CleanupJobId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_duplicate_searches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "image_duplicate_keeper_reservations",
                columns: table => new
                {
                    SearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_duplicate_keeper_reservations", x => new { x.SearchId, x.FileId });
                    table.ForeignKey(
                        name: "FK_image_duplicate_keeper_reservations_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_image_duplicate_keeper_reservations_image_duplicate_searche~",
                        column: x => x.SearchId,
                        principalTable: "image_duplicate_searches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_duplicate_keeper_reservations_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "image_duplicate_search_groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KeeperFileId = table.Column<int>(type: "integer", nullable: false),
                    FreeableBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_duplicate_search_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_image_duplicate_search_groups_image_duplicate_searches_Sear~",
                        column: x => x.SearchId,
                        principalTable: "image_duplicate_searches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "image_duplicate_search_items",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    Protected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_duplicate_search_items", x => new { x.GroupId, x.FileId });
                    table.ForeignKey(
                        name: "FK_image_duplicate_search_items_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_duplicate_search_items_image_duplicate_search_groups_~",
                        column: x => x.GroupId,
                        principalTable: "image_duplicate_search_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_duplicate_search_items_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_keeper_reservations_FileId",
                table: "image_duplicate_keeper_reservations",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_keeper_reservations_ImageId",
                table: "image_duplicate_keeper_reservations",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_search_groups_SearchId_Position",
                table: "image_duplicate_search_groups",
                columns: new[] { "SearchId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_search_items_FileId",
                table: "image_duplicate_search_items",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_search_items_ImageId",
                table: "image_duplicate_search_items",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_searches_ExpiresAt",
                table: "image_duplicate_searches",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_image_duplicate_searches_OwnerKey_CreatedAt",
                table: "image_duplicate_searches",
                columns: new[] { "OwnerKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "image_duplicate_keeper_reservations");

            migrationBuilder.DropTable(
                name: "image_duplicate_search_items");

            migrationBuilder.DropTable(
                name: "image_duplicate_search_groups");

            migrationBuilder.DropTable(
                name: "image_duplicate_searches");

            migrationBuilder.DropColumn(
                name: "FingerprintAlgorithm",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "FolderMode",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "FolderPathsJson",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "KeeperRulesJson",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "MinimumDuration",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "PreferredCodecsJson",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "RankingMode",
                table: "duplicate_searches");

            migrationBuilder.DropColumn(
                name: "RecommendationReason",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "RecommendedVideoId",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "RiskNotesJson",
                table: "duplicate_search_groups");

            migrationBuilder.DropColumn(
                name: "RiskScore",
                table: "duplicate_search_groups");
        }
    }
}
