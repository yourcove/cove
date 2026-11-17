using System;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentSpanIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "segment_span_indexed_profiles",
                columns: table => new
                {
                    ProfileId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_span_indexed_profiles", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_segment_span_indexed_profiles_segment_display_profiles_Prof~",
                        column: x => x.ProfileId,
                        principalTable: "segment_display_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "segment_span_video_states",
                columns: table => new
                {
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    VideoId = table.Column<int>(type: "integer", nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    ResolvedVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_span_video_states", x => new { x.ProfileId, x.VideoId });
                    table.ForeignKey(
                        name: "FK_segment_span_video_states_segment_display_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "segment_display_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_segment_span_video_states_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "segment_span_video_versions",
                columns: table => new
                {
                    VideoId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_span_video_versions", x => x.VideoId);
                    table.ForeignKey(
                        name: "FK_segment_span_video_versions_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "segment_spans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    VideoId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SpanKey = table.Column<string>(type: "text", nullable: false),
                    StartSec = table.Column<double>(type: "double precision", nullable: false),
                    EndSec = table.Column<double>(type: "double precision", nullable: false),
                    DurationSec = table.Column<double>(type: "double precision", nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: true),
                    TagId = table.Column<int>(type: "integer", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: true),
                    ColorHint = table.Column<string>(type: "text", nullable: true),
                    Lane = table.Column<int>(type: "integer", nullable: true),
                    CollapsedToInstant = table.Column<bool>(type: "boolean", nullable: false),
                    SegmentIds = table.Column<int[]>(type: "integer[]", nullable: false),
                    SegmentCount = table.Column<int>(type: "integer", nullable: false),
                    RefIds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    EntityRefIds = table.Column<int[]>(type: "integer[]", nullable: false),
                    Titles = table.Column<string[]>(type: "text[]", nullable: false),
                    SourceRunIds = table.Column<string[]>(type: "text[]", nullable: false),
                    ColorHints = table.Column<string[]>(type: "text[]", nullable: false),
                    HasImage = table.Column<bool>(type: "boolean", nullable: false),
                    HasPayload = table.Column<bool>(type: "boolean", nullable: false),
                    MinConfidence = table.Column<float>(type: "real", nullable: true),
                    MaxConfidence = table.Column<float>(type: "real", nullable: true),
                    MinSegmentCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxSegmentCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MinSegmentUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxSegmentUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RandomKey = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_spans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_segment_spans_segment_display_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "segment_display_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_segment_spans_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_segment_span_video_states_VideoId",
                table: "segment_span_video_states",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_ProfileId_DurationSec_Id",
                table: "segment_spans",
                columns: new[] { "ProfileId", "DurationSec", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_ProfileId_StartSec_Id",
                table: "segment_spans",
                columns: new[] { "ProfileId", "StartSec", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_ProfileId_TagId_VideoId",
                table: "segment_spans",
                columns: new[] { "ProfileId", "TagId", "VideoId" });

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_ProfileId_VideoId_Ordinal",
                table: "segment_spans",
                columns: new[] { "ProfileId", "VideoId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_RefIds",
                table: "segment_spans",
                column: "RefIds")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_segment_spans_VideoId",
                table: "segment_spans",
                column: "VideoId");

            // The triggers that mark a video's spans stale, then a version row for every video that
            // already has segments so the span index is built for an existing library.
            migrationBuilder.Sql(SegmentSpanIndexSqlDefinitions.CreateSql);
            migrationBuilder.Sql(SegmentSpanIndexSqlDefinitions.BackfillVersionsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SegmentSpanIndexSqlDefinitions.DropSql);

            migrationBuilder.DropTable(
                name: "segment_span_indexed_profiles");

            migrationBuilder.DropTable(
                name: "segment_span_video_states");

            migrationBuilder.DropTable(
                name: "segment_span_video_versions");

            migrationBuilder.DropTable(
                name: "segment_spans");
        }
    }
}
