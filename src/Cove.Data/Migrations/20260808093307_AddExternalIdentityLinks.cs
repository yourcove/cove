using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdentityLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_identity_links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ExtensionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identity_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_identity_links_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_links_ExtensionId_ProviderId_Subject",
                table: "external_identity_links",
                columns: new[] { "ExtensionId", "ProviderId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_links_UserId_ProviderLabel",
                table: "external_identity_links",
                columns: new[] { "UserId", "ProviderLabel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identity_links");
        }
    }
}
