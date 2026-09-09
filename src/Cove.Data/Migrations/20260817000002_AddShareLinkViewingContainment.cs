using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260817000002_AddShareLinkViewingContainment")]
public sealed class AddShareLinkViewingContainment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ContainedEntityIds",
            table: "share_links",
            type: "jsonb",
            nullable: false,
            defaultValue: "[]");
        migrationBuilder.Sql(AuthorizationSqlDefinitions.CreateFunctionsSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(AuthorizationSqlDefinitions.CreateFunctionsWithoutShareContainmentSql);
        migrationBuilder.DropColumn(name: "ContainedEntityIds", table: "share_links");
    }
}
