using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialDatePrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "videos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "text_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BirthdatePrecision",
                table: "performers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CareerEndPrecision",
                table: "performers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CareerStartPrecision",
                table: "performers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DeathDatePrecision",
                table: "performers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "images",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "galleries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DatePrecision",
                table: "audios",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "text_documents");

            migrationBuilder.DropColumn(
                name: "BirthdatePrecision",
                table: "performers");

            migrationBuilder.DropColumn(
                name: "CareerEndPrecision",
                table: "performers");

            migrationBuilder.DropColumn(
                name: "CareerStartPrecision",
                table: "performers");

            migrationBuilder.DropColumn(
                name: "DeathDatePrecision",
                table: "performers");

            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "images");

            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "galleries");

            migrationBuilder.DropColumn(
                name: "DatePrecision",
                table: "audios");
        }
    }
}
