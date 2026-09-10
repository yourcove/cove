using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoPrimaryFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrimaryFileId",
                table: "videos",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE videos AS video
                SET "PrimaryFileId" = (
                    SELECT MIN(file."Id")
                    FROM files AS file
                    WHERE file."VideoId" = video."Id" AND file."FileType" = 'Video'
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_videos_PrimaryFileId",
                table: "videos",
                column: "PrimaryFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_videos_files_PrimaryFileId",
                table: "videos",
                column: "PrimaryFileId",
                principalTable: "files",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_videos_files_PrimaryFileId",
                table: "videos");

            migrationBuilder.DropIndex(
                name: "IX_videos_PrimaryFileId",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "PrimaryFileId",
                table: "videos");

        }
    }
}
