using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoShotSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_shot_sets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceRunId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Mode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DecodeBackend = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Fps = table.Column<double>(type: "double precision", nullable: true),
                    FrameCount = table.Column<int>(type: "integer", nullable: true),
                    DurationSec = table.Column<double>(type: "double precision", nullable: false),
                    ShotCount = table.Column<int>(type: "integer", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_shot_sets", x => x.Id);
                    table.CheckConstraint("CK_video_shot_sets_duration", "\"DurationSec\" > 0 AND \"DurationSec\" < 'Infinity'");
                    table.CheckConstraint("CK_video_shot_sets_fps", "\"Fps\" IS NULL OR (\"Fps\" > 0 AND \"Fps\" < 'Infinity')");
                    table.CheckConstraint("CK_video_shot_sets_frame_count", "\"FrameCount\" IS NULL OR \"FrameCount\" > 0");
                    table.CheckConstraint("CK_video_shot_sets_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_video_shot_sets_shot_count", "\"ShotCount\" > 0");
                    table.ForeignKey(
                        name: "FK_video_shot_sets_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_shots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SetId = table.Column<int>(type: "integer", nullable: false),
                    StartSec = table.Column<double>(type: "double precision", nullable: false),
                    EndSec = table.Column<double>(type: "double precision", nullable: false),
                    StartFrame = table.Column<int>(type: "integer", nullable: true),
                    EndFrame = table.Column<int>(type: "integer", nullable: true),
                    ShotType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TransitionIn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_shots", x => x.Id);
                    table.CheckConstraint("CK_video_shots_frames", "(\"StartFrame\" IS NULL AND \"EndFrame\" IS NULL) OR (\"StartFrame\" IS NOT NULL AND \"EndFrame\" IS NOT NULL AND \"StartFrame\" >= 0 AND \"EndFrame\" > \"StartFrame\")");
                    table.CheckConstraint("CK_video_shots_range", "\"StartSec\" >= 0 AND \"EndSec\" > \"StartSec\" AND \"EndSec\" < 'Infinity'");
                    table.ForeignKey(
                        name: "FK_video_shots_video_shot_sets_SetId",
                        column: x => x.SetId,
                        principalTable: "video_shot_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_video_shot_sets_FileId",
                table: "video_shot_sets",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_video_shot_sets_SourceKey",
                table: "video_shot_sets",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "IX_video_shot_sets_SourceRunId",
                table: "video_shot_sets",
                column: "SourceRunId");

            migrationBuilder.CreateIndex(
                name: "IX_video_shots_SetId_StartSec",
                table: "video_shots",
                columns: new[] { "SetId", "StartSec" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_shots");

            migrationBuilder.DropTable(
                name: "video_shot_sets");
        }
    }
}
