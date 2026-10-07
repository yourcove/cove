using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagNamespaceKeyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tags_NamespaceKey",
                table: "tags",
                column: "NamespaceKey")
                .Annotation("Npgsql:IndexMethod", "hash");

            migrationBuilder.CreateIndex(
                name: "IX_tag_aliases_NamespaceKey",
                table: "tag_aliases",
                column: "NamespaceKey")
                .Annotation("Npgsql:IndexMethod", "hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tags_NamespaceKey",
                table: "tags");

            migrationBuilder.DropIndex(
                name: "IX_tag_aliases_NamespaceKey",
                table: "tag_aliases");
        }
    }
}
