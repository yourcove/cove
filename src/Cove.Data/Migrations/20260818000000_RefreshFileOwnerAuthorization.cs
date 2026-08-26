using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260818000000_RefreshFileOwnerAuthorization")]
public sealed class RefreshFileOwnerAuthorization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(AuthorizationSqlDefinitions.CreateFunctionsSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Retain the safer owner-aware File rule behavior when downgrading the schema.
    }
}
