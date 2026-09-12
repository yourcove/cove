using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreserveOverlappingDuplicateIgnores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DecisionCount",
                table: "duplicate_ignored_pairs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Rebuild missing rows from still-retained ignored groups and count overlapping decisions.
            // Existing orphaned rows may represent expired searches, so never reduce their count.
            migrationBuilder.Sql("""
                INSERT INTO duplicate_ignored_pairs ("LowVideoId", "HighVideoId", "CreatedAt", "DecisionCount")
                SELECT low_item."VideoId", high_item."VideoId", CURRENT_TIMESTAMP, COUNT(*)::integer
                FROM duplicate_search_groups AS duplicate_group
                JOIN duplicate_search_items AS low_item ON low_item."GroupId" = duplicate_group."Id"
                JOIN duplicate_search_items AS high_item ON high_item."GroupId" = duplicate_group."Id" AND low_item."VideoId" < high_item."VideoId"
                WHERE duplicate_group."Status" = 'Ignored'
                GROUP BY low_item."VideoId", high_item."VideoId"
                ON CONFLICT ("LowVideoId", "HighVideoId") DO UPDATE
                SET "DecisionCount" = GREATEST(duplicate_ignored_pairs."DecisionCount", EXCLUDED."DecisionCount");
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_duplicate_ignored_pairs_decision_count",
                table: "duplicate_ignored_pairs",
                sql: "\"DecisionCount\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_duplicate_ignored_pairs_decision_count",
                table: "duplicate_ignored_pairs");

            migrationBuilder.DropColumn(
                name: "DecisionCount",
                table: "duplicate_ignored_pairs");
        }
    }
}
