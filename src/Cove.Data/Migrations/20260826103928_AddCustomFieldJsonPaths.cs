using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomFieldJsonPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_field_json_paths",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Filterable = table.Column<bool>(type: "boolean", nullable: false),
                    Sortable = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_json_paths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_json_paths_custom_field_definitions_Definition~",
                        column: x => x.DefinitionId,
                        principalTable: "custom_field_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_json_paths_DefinitionId_DisplayOrder",
                table: "custom_field_json_paths",
                columns: new[] { "DefinitionId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_json_paths_DefinitionId_Path",
                table: "custom_field_json_paths",
                columns: new[] { "DefinitionId", "Path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_field_json_paths");
        }
    }
}
