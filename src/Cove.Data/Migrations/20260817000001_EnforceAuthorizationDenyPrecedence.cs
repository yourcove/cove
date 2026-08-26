using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260817000001_EnforceAuthorizationDenyPrecedence")]
public sealed class EnforceAuthorizationDenyPrecedence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(AuthorizationSqlDefinitions.CreateFunctionsWithoutShareContainmentSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Retain the safer deny-precedence behavior when downgrading the schema.
    }
}
