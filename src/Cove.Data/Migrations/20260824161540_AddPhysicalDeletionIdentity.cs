using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhysicalDeletionIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ExpectedCreationTimeUtcTicks",
                table: "pending_physical_file_deletions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExpectedExists",
                table: "pending_physical_file_deletions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedLastWriteTimeUtcTicks",
                table: "pending_physical_file_deletions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedLength",
                table: "pending_physical_file_deletions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IdentityCaptured",
                table: "pending_physical_file_deletions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedCreationTimeUtcTicks",
                table: "pending_physical_file_deletions");

            migrationBuilder.DropColumn(
                name: "ExpectedExists",
                table: "pending_physical_file_deletions");

            migrationBuilder.DropColumn(
                name: "ExpectedLastWriteTimeUtcTicks",
                table: "pending_physical_file_deletions");

            migrationBuilder.DropColumn(
                name: "ExpectedLength",
                table: "pending_physical_file_deletions");

            migrationBuilder.DropColumn(
                name: "IdentityCaptured",
                table: "pending_physical_file_deletions");
        }
    }
}
