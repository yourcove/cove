using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceTopSuggestionProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TopSuggestionComputedAt",
                table: "faces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TopSuggestionConfidence",
                table: "faces",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopSuggestionCoverImageUrl",
                table: "faces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopSuggestionExternalUrl",
                table: "faces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TopSuggestionLocalPerformerHasImage",
                table: "faces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TopSuggestionLocalPerformerId",
                table: "faces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TopSuggestionLocalPerformerIsLocalOnly",
                table: "faces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TopSuggestionPerformerId",
                table: "faces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopSuggestionPerformerName",
                table: "faces",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_faces_PerformerId_TopSuggestionComputedAt",
                table: "faces",
                columns: new[] { "PerformerId", "TopSuggestionComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_faces_PerformerId_TopSuggestionConfidence",
                table: "faces",
                columns: new[] { "PerformerId", "TopSuggestionConfidence" });

            migrationBuilder.CreateIndex(
                name: "IX_faces_TopSuggestionLocalPerformerId",
                table: "faces",
                column: "TopSuggestionLocalPerformerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_faces_PerformerId_TopSuggestionComputedAt",
                table: "faces");

            migrationBuilder.DropIndex(
                name: "IX_faces_PerformerId_TopSuggestionConfidence",
                table: "faces");

            migrationBuilder.DropIndex(
                name: "IX_faces_TopSuggestionLocalPerformerId",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionComputedAt",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionConfidence",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionCoverImageUrl",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionExternalUrl",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionLocalPerformerHasImage",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionLocalPerformerId",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionLocalPerformerIsLocalOnly",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionPerformerId",
                table: "faces");

            migrationBuilder.DropColumn(
                name: "TopSuggestionPerformerName",
                table: "faces");
        }
    }
}
