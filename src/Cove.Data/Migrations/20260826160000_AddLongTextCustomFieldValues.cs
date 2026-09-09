using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260826160000_AddLongTextCustomFieldValues")]
public sealed class AddLongTextCustomFieldValues : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LongTextValue",
            table: "custom_field_values",
            type: "text",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE custom_field_values AS value
            SET "LongTextValue" = value."TextValue",
                "TextValue" = NULL
            FROM custom_field_definitions AS definition
            WHERE definition."Id" = value."DefinitionId"
              AND lower(definition."Type") = lower('longText');

            WITH aggregated AS (
                SELECT value."DefinitionId",
                       value."EntityType",
                       value."EntityId",
                       string_agg(value."LongTextValue", E'\n' ORDER BY value."Position") AS combined_value
                FROM custom_field_values AS value
                JOIN custom_field_definitions AS definition
                  ON definition."Id" = value."DefinitionId"
                WHERE lower(definition."Type") = lower('longText')
                GROUP BY value."DefinitionId", value."EntityType", value."EntityId"
                HAVING count(*) > 1
            )
            UPDATE custom_field_values AS value
            SET "LongTextValue" = aggregated.combined_value
            FROM aggregated
            WHERE value."DefinitionId" = aggregated."DefinitionId"
              AND value."EntityType" = aggregated."EntityType"
              AND value."EntityId" = aggregated."EntityId"
              AND value."Position" = 0;

            DELETE FROM custom_field_values AS value
            USING custom_field_definitions AS definition
            WHERE definition."Id" = value."DefinitionId"
              AND lower(definition."Type") = lower('longText')
              AND value."Position" <> 0;

            UPDATE custom_field_definitions
            SET "Type" = 'longText',
                "Filterable" = FALSE,
                "Sortable" = FALSE,
                "IsMultiValue" = FALSE
            WHERE lower("Type") = lower('longText');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM custom_field_values
                    WHERE char_length("LongTextValue") > 4000) THEN
                    RAISE EXCEPTION 'Cannot remove long-text storage while values longer than 4000 characters exist.';
                END IF;
            END
            $block$;

            UPDATE custom_field_values AS value
            SET "TextValue" = value."LongTextValue"
            FROM custom_field_definitions AS definition
            WHERE definition."Id" = value."DefinitionId"
              AND lower(definition."Type") = lower('longText');
            """);

        migrationBuilder.DropColumn(
            name: "LongTextValue",
            table: "custom_field_values");
    }
}
