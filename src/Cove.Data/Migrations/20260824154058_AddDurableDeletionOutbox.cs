using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableDeletionOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duplicate_deletion_keeper_reservations",
                columns: table => new
                {
                    SearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_deletion_keeper_reservations", x => new { x.SearchId, x.VideoId });
                    table.ForeignKey(
                        name: "FK_duplicate_deletion_keeper_reservations_duplicate_searches_S~",
                        column: x => x.SearchId,
                        principalTable: "duplicate_searches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_duplicate_deletion_keeper_reservations_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pending_physical_file_deletions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_physical_file_deletions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_deletion_keeper_reservations_VideoId",
                table: "duplicate_deletion_keeper_reservations",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_physical_file_deletions_BatchId_Id",
                table: "pending_physical_file_deletions",
                columns: new[] { "BatchId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_physical_file_deletions_CreatedAt",
                table: "pending_physical_file_deletions",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_deletion_keeper_reservations");

            migrationBuilder.DropTable(
                name: "pending_physical_file_deletions");

        }
    }
}
