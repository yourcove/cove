using System;
using System.Text.Json;
using Cove.Data.Services;
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
            // The functions the cut CHECK constraints call.
            migrationBuilder.Sql(VideoShotSqlDefinitions.CreateFunctionsSql);

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
                    CutTimes = table.Column<double[]>(type: "double precision[]", nullable: false),
                    CutFrames = table.Column<int[]>(type: "integer[]", nullable: true),
                    ShotTypes = table.Column<string[]>(type: "character varying(100)[]", nullable: false),
                    Transitions = table.Column<string[]>(type: "character varying(100)[]", nullable: false),
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
                    table.CheckConstraint("CK_video_shot_sets_array_shapes", "cardinality(\"ShotTypes\") = \"ShotCount\" AND cardinality(\"CutTimes\") = \"ShotCount\" - 1\nAND cardinality(\"Transitions\") = \"ShotCount\" - 1\nAND (\"CutFrames\" IS NULL OR cardinality(\"CutFrames\") = \"ShotCount\" - 1)\nAND coalesce(array_ndims(\"ShotTypes\"), 1) = 1 AND coalesce(array_ndims(\"CutTimes\"), 1) = 1\nAND coalesce(array_ndims(\"Transitions\"), 1) = 1 AND coalesce(array_ndims(\"CutFrames\"), 1) = 1");
                    table.CheckConstraint("CK_video_shot_sets_cut_frames", "\"CutFrames\" IS NULL OR (\"FrameCount\" IS NOT NULL AND public.cove_video_shot_cut_frames_valid(\"CutFrames\", \"FrameCount\"))");
                    table.CheckConstraint("CK_video_shot_sets_cut_times", "public.cove_video_shot_cut_times_valid(\"CutTimes\", \"DurationSec\")");
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_shot_sets");

            migrationBuilder.Sql(VideoShotSqlDefinitions.DropFunctionsSql);
        }
    }
}
