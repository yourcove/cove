using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "extension_data",
                columns: table => new
                {
                    ExtensionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extension_data", x => new { x.ExtensionId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ParentFolderId = table.Column<int>(type: "integer", nullable: true),
                    ZipFileId = table.Column<int>(type: "integer", nullable: true),
                    ModTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folders_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "performers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Disambiguation = table.Column<string>(type: "text", nullable: true),
                    Gender = table.Column<int>(type: "integer", nullable: true),
                    Birthdate = table.Column<DateOnly>(type: "date", nullable: true),
                    DeathDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Ethnicity = table.Column<string>(type: "text", nullable: true),
                    Country = table.Column<string>(type: "text", nullable: true),
                    EyeColor = table.Column<string>(type: "text", nullable: true),
                    HairColor = table.Column<string>(type: "text", nullable: true),
                    HeightCm = table.Column<int>(type: "integer", nullable: true),
                    Weight = table.Column<int>(type: "integer", nullable: true),
                    Measurements = table.Column<string>(type: "text", nullable: true),
                    FakeTits = table.Column<string>(type: "text", nullable: true),
                    PenisLength = table.Column<double>(type: "double precision", nullable: true),
                    Circumcised = table.Column<int>(type: "integer", nullable: true),
                    CareerStart = table.Column<DateOnly>(type: "date", nullable: true),
                    CareerEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    Tattoos = table.Column<string>(type: "text", nullable: true),
                    Piercings = table.Column<string>(type: "text", nullable: true),
                    Favorite = table.Column<bool>(type: "boolean", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IgnoreAutoTag = table.Column<bool>(type: "boolean", nullable: false),
                    ImageBlobId = table.Column<string>(type: "text", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "saved_filters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FindFilter = table.Column<string>(type: "jsonb", nullable: true),
                    ObjectFilter = table.Column<string>(type: "jsonb", nullable: true),
                    UIOptions = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_filters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "studios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Favorite = table.Column<bool>(type: "boolean", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IgnoreAutoTag = table.Column<bool>(type: "boolean", nullable: false),
                    Organized = table.Column<bool>(type: "boolean", nullable: false),
                    ImageBlobId = table.Column<string>(type: "text", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_studios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_studios_studios_ParentId",
                        column: x => x.ParentId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SortName = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Favorite = table.Column<bool>(type: "boolean", nullable: false),
                    IgnoreAutoTag = table.Column<bool>(type: "boolean", nullable: false),
                    ImageBlobId = table.Column<string>(type: "text", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerformerAlias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PerformerId = table.Column<int>(type: "integer", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformerAlias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformerAlias_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PerformerRemoteId",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PerformerId = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformerRemoteId", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformerRemoteId_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PerformerUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PerformerId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformerUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformerUrl_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "galleries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    Photographer = table.Column<string>(type: "text", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Organized = table.Column<bool>(type: "boolean", nullable: false),
                    StudioId = table.Column<int>(type: "integer", nullable: true),
                    FolderId = table.Column<int>(type: "integer", nullable: true),
                    ImageBlobId = table.Column<string>(type: "text", nullable: true),
                    CoverImageId = table.Column<int>(type: "integer", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_galleries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_galleries_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_galleries_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Aliases = table.Column<string>(type: "text", nullable: true),
                    Duration = table.Column<int>(type: "integer", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    StudioId = table.Column<int>(type: "integer", nullable: true),
                    Director = table.Column<string>(type: "text", nullable: true),
                    Synopsis = table.Column<string>(type: "text", nullable: true),
                    FrontImageBlobId = table.Column<string>(type: "text", nullable: true),
                    BackImageBlobId = table.Column<string>(type: "text", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_groups_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "images",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    Photographer = table.Column<string>(type: "text", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Organized = table.Column<bool>(type: "boolean", nullable: false),
                    OCounter = table.Column<int>(type: "integer", nullable: false),
                    StudioId = table.Column<int>(type: "integer", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_images_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scenes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    Director = table.Column<string>(type: "text", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Organized = table.Column<bool>(type: "boolean", nullable: false),
                    StudioId = table.Column<int>(type: "integer", nullable: true),
                    ResumeTime = table.Column<double>(type: "double precision", nullable: false),
                    PlayDuration = table.Column<double>(type: "double precision", nullable: false),
                    PlayCount = table.Column<int>(type: "integer", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OCounter = table.Column<int>(type: "integer", nullable: false),
                    CustomFields = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scenes_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StudioAlias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudioId = table.Column<int>(type: "integer", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudioAlias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudioAlias_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudioRemoteId",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudioId = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudioRemoteId", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudioRemoteId_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudioUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudioId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudioUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudioUrl_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "performer_tags",
                columns: table => new
                {
                    PerformerId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performer_tags", x => new { x.PerformerId, x.TagId });
                    table.ForeignKey(
                        name: "FK_performer_tags_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_performer_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "studio_tags",
                columns: table => new
                {
                    StudioId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_studio_tags", x => new { x.StudioId, x.TagId });
                    table.ForeignKey(
                        name: "FK_studio_tags_studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_studio_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_parents",
                columns: table => new
                {
                    ParentId = table.Column<int>(type: "integer", nullable: false),
                    ChildId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_parents", x => new { x.ParentId, x.ChildId });
                    table.ForeignKey(
                        name: "FK_tag_parents_tags_ChildId",
                        column: x => x.ChildId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tag_parents_tags_ParentId",
                        column: x => x.ParentId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagAlias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagAlias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagAlias_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagRemoteId",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagRemoteId", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagRemoteId_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_roles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gallery_chapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    ImageIndex = table.Column<int>(type: "integer", nullable: false),
                    GalleryId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gallery_chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_gallery_chapters_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gallery_performers",
                columns: table => new
                {
                    GalleryId = table.Column<int>(type: "integer", nullable: false),
                    PerformerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gallery_performers", x => new { x.GalleryId, x.PerformerId });
                    table.ForeignKey(
                        name: "FK_gallery_performers_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gallery_performers_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gallery_tags",
                columns: table => new
                {
                    GalleryId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gallery_tags", x => new { x.GalleryId, x.TagId });
                    table.ForeignKey(
                        name: "FK_gallery_tags_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gallery_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GalleryUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GalleryId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GalleryUrl_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_relations",
                columns: table => new
                {
                    ContainingGroupId = table.Column<int>(type: "integer", nullable: false),
                    SubGroupId = table.Column<int>(type: "integer", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_relations", x => new { x.ContainingGroupId, x.SubGroupId });
                    table.ForeignKey(
                        name: "FK_group_relations_groups_ContainingGroupId",
                        column: x => x.ContainingGroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_relations_groups_SubGroupId",
                        column: x => x.SubGroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_tags",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_tags", x => new { x.GroupId, x.TagId });
                    table.ForeignKey(
                        name: "FK_group_tags_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupUrl_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "image_galleries",
                columns: table => new
                {
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    GalleryId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_galleries", x => new { x.ImageId, x.GalleryId });
                    table.ForeignKey(
                        name: "FK_image_galleries_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_galleries_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "image_performers",
                columns: table => new
                {
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    PerformerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_performers", x => new { x.ImageId, x.PerformerId });
                    table.ForeignKey(
                        name: "FK_image_performers_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_performers_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "image_tags",
                columns: table => new
                {
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_tags", x => new { x.ImageId, x.TagId });
                    table.ForeignKey(
                        name: "FK_image_tags_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageUrl_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Basename = table.Column<string>(type: "text", nullable: false),
                    ParentFolderId = table.Column<int>(type: "integer", nullable: false),
                    ZipFileId = table.Column<int>(type: "integer", nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ModTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FileType = table.Column<string>(type: "character varying(21)", maxLength: 21, nullable: false),
                    GalleryId = table.Column<int>(type: "integer", nullable: true),
                    Format = table.Column<string>(type: "text", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    ImageId = table.Column<int>(type: "integer", nullable: true),
                    VideoFile_Format = table.Column<string>(type: "text", nullable: true),
                    VideoFile_Width = table.Column<int>(type: "integer", nullable: true),
                    VideoFile_Height = table.Column<int>(type: "integer", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    VideoCodec = table.Column<string>(type: "text", nullable: true),
                    AudioCodec = table.Column<string>(type: "text", nullable: true),
                    FrameRate = table.Column<double>(type: "double precision", nullable: true),
                    BitRate = table.Column<long>(type: "bigint", nullable: true),
                    Interactive = table.Column<bool>(type: "boolean", nullable: true),
                    InteractiveSpeed = table.Column<int>(type: "integer", nullable: true),
                    SceneId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_files_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_files_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_files_images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_files_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scene_galleries",
                columns: table => new
                {
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    GalleryId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_galleries", x => new { x.SceneId, x.GalleryId });
                    table.ForeignKey(
                        name: "FK_scene_galleries_galleries_GalleryId",
                        column: x => x.GalleryId,
                        principalTable: "galleries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_galleries_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_groups",
                columns: table => new
                {
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    SceneIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_groups", x => new { x.SceneId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_scene_groups_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_groups_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_markers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Seconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: true),
                    PrimaryTagId = table.Column<int>(type: "integer", nullable: false),
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_markers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scene_markers_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_markers_tags_PrimaryTagId",
                        column: x => x.PrimaryTagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_performers",
                columns: table => new
                {
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    PerformerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_performers", x => new { x.SceneId, x.PerformerId });
                    table.ForeignKey(
                        name: "FK_scene_performers_performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_performers_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_tags",
                columns: table => new
                {
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_tags", x => new { x.SceneId, x.TagId });
                    table.ForeignKey(
                        name: "FK_scene_tags_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneOHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneOHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneOHistory_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScenePlayHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenePlayHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenePlayHistory_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneRemoteId",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneRemoteId", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneRemoteId_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneUrl",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SceneId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneUrl", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneUrl_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileFingerprints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileFingerprints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileFingerprints_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VideoCaptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    LanguageCode = table.Column<string>(type: "text", nullable: false),
                    CaptionType = table.Column<string>(type: "text", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoCaptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoCaptions_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_marker_tags",
                columns: table => new
                {
                    SceneMarkerId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_marker_tags", x => new { x.SceneMarkerId, x.TagId });
                    table.ForeignKey(
                        name: "FK_scene_marker_tags_scene_markers_SceneMarkerId",
                        column: x => x.SceneMarkerId,
                        principalTable: "scene_markers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scene_marker_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_extension_data_ExtensionId",
                table: "extension_data",
                column: "ExtensionId");

            migrationBuilder.CreateIndex(
                name: "IX_FileFingerprints_FileId",
                table: "FileFingerprints",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileFingerprints_Type_Value",
                table: "FileFingerprints",
                columns: new[] { "Type", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_files_GalleryId",
                table: "files",
                column: "GalleryId");

            migrationBuilder.CreateIndex(
                name: "IX_files_ImageId",
                table: "files",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_files_ParentFolderId_Basename",
                table: "files",
                columns: new[] { "ParentFolderId", "Basename" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_files_SceneId",
                table: "files",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_folders_ParentFolderId",
                table: "folders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_folders_Path",
                table: "folders",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_galleries_FolderId",
                table: "galleries",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_galleries_StudioId",
                table: "galleries",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_galleries_Title",
                table: "galleries",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_gallery_chapters_GalleryId",
                table: "gallery_chapters",
                column: "GalleryId");

            migrationBuilder.CreateIndex(
                name: "IX_gallery_performers_PerformerId",
                table: "gallery_performers",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_gallery_tags_TagId",
                table: "gallery_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_GalleryUrl_GalleryId",
                table: "GalleryUrl",
                column: "GalleryId");

            migrationBuilder.CreateIndex(
                name: "IX_group_relations_SubGroupId",
                table: "group_relations",
                column: "SubGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_group_tags_TagId",
                table: "group_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_groups_Name",
                table: "groups",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_groups_StudioId",
                table: "groups",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupUrl_GroupId",
                table: "GroupUrl",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_image_galleries_GalleryId",
                table: "image_galleries",
                column: "GalleryId");

            migrationBuilder.CreateIndex(
                name: "IX_image_performers_PerformerId",
                table: "image_performers",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_image_tags_TagId",
                table: "image_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_images_CreatedAt",
                table: "images",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_images_Organized",
                table: "images",
                column: "Organized");

            migrationBuilder.CreateIndex(
                name: "IX_images_Rating",
                table: "images",
                column: "Rating");

            migrationBuilder.CreateIndex(
                name: "IX_images_StudioId",
                table: "images",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_images_Title",
                table: "images",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_images_UpdatedAt",
                table: "images",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUrl_ImageId",
                table: "ImageUrl",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_performer_tags_TagId",
                table: "performer_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_PerformerAlias_PerformerId",
                table: "PerformerAlias",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_PerformerRemoteId_PerformerId",
                table: "PerformerRemoteId",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_performers_Favorite",
                table: "performers",
                column: "Favorite");

            migrationBuilder.CreateIndex(
                name: "IX_performers_Name",
                table: "performers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_performers_Rating",
                table: "performers",
                column: "Rating");

            migrationBuilder.CreateIndex(
                name: "IX_PerformerUrl_PerformerId",
                table: "PerformerUrl",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_galleries_GalleryId",
                table: "scene_galleries",
                column: "GalleryId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_groups_GroupId",
                table: "scene_groups",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_marker_tags_TagId",
                table: "scene_marker_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_markers_PrimaryTagId",
                table: "scene_markers",
                column: "PrimaryTagId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_markers_SceneId",
                table: "scene_markers",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_performers_PerformerId",
                table: "scene_performers",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_scene_tags_TagId",
                table: "scene_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneOHistory_SceneId",
                table: "SceneOHistory",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_ScenePlayHistory_SceneId",
                table: "ScenePlayHistory",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneRemoteId_SceneId",
                table: "SceneRemoteId",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_CreatedAt",
                table: "scenes",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_Date",
                table: "scenes",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_Organized",
                table: "scenes",
                column: "Organized");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_Rating",
                table: "scenes",
                column: "Rating");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_StudioId",
                table: "scenes",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_Title",
                table: "scenes",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_scenes_UpdatedAt",
                table: "scenes",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SceneUrl_SceneId",
                table: "SceneUrl",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_studio_tags_TagId",
                table: "studio_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_StudioAlias_StudioId",
                table: "StudioAlias",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_StudioRemoteId_StudioId",
                table: "StudioRemoteId",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_studios_Name",
                table: "studios",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_studios_ParentId",
                table: "studios",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_StudioUrl_StudioId",
                table: "StudioUrl",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_tag_parents_ChildId",
                table: "tag_parents",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_TagAlias_TagId",
                table: "TagAlias",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagRemoteId_TagId",
                table: "TagRemoteId",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_tags_Favorite",
                table: "tags",
                column: "Favorite");

            migrationBuilder.CreateIndex(
                name: "IX_tags_Name",
                table: "tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_UserId_Role",
                table: "user_roles",
                columns: new[] { "UserId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ApiKey",
                table: "users",
                column: "ApiKey",
                unique: true,
                filter: "\"ApiKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoCaptions_FileId",
                table: "VideoCaptions",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extension_data");

            migrationBuilder.DropTable(
                name: "FileFingerprints");

            migrationBuilder.DropTable(
                name: "gallery_chapters");

            migrationBuilder.DropTable(
                name: "gallery_performers");

            migrationBuilder.DropTable(
                name: "gallery_tags");

            migrationBuilder.DropTable(
                name: "GalleryUrl");

            migrationBuilder.DropTable(
                name: "group_relations");

            migrationBuilder.DropTable(
                name: "group_tags");

            migrationBuilder.DropTable(
                name: "GroupUrl");

            migrationBuilder.DropTable(
                name: "image_galleries");

            migrationBuilder.DropTable(
                name: "image_performers");

            migrationBuilder.DropTable(
                name: "image_tags");

            migrationBuilder.DropTable(
                name: "ImageUrl");

            migrationBuilder.DropTable(
                name: "performer_tags");

            migrationBuilder.DropTable(
                name: "PerformerAlias");

            migrationBuilder.DropTable(
                name: "PerformerRemoteId");

            migrationBuilder.DropTable(
                name: "PerformerUrl");

            migrationBuilder.DropTable(
                name: "saved_filters");

            migrationBuilder.DropTable(
                name: "scene_galleries");

            migrationBuilder.DropTable(
                name: "scene_groups");

            migrationBuilder.DropTable(
                name: "scene_marker_tags");

            migrationBuilder.DropTable(
                name: "scene_performers");

            migrationBuilder.DropTable(
                name: "scene_tags");

            migrationBuilder.DropTable(
                name: "SceneOHistory");

            migrationBuilder.DropTable(
                name: "ScenePlayHistory");

            migrationBuilder.DropTable(
                name: "SceneRemoteId");

            migrationBuilder.DropTable(
                name: "SceneUrl");

            migrationBuilder.DropTable(
                name: "studio_tags");

            migrationBuilder.DropTable(
                name: "StudioAlias");

            migrationBuilder.DropTable(
                name: "StudioRemoteId");

            migrationBuilder.DropTable(
                name: "StudioUrl");

            migrationBuilder.DropTable(
                name: "tag_parents");

            migrationBuilder.DropTable(
                name: "TagAlias");

            migrationBuilder.DropTable(
                name: "TagRemoteId");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "VideoCaptions");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "scene_markers");

            migrationBuilder.DropTable(
                name: "performers");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "galleries");

            migrationBuilder.DropTable(
                name: "images");

            migrationBuilder.DropTable(
                name: "scenes");

            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "studios");
        }
    }
}
