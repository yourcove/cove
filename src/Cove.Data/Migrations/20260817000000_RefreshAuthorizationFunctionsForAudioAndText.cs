using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260817000000_RefreshAuthorizationFunctionsForAudioAndText")]
public sealed class RefreshAuthorizationFunctionsForAudioAndText : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(AuthorizationSqlDefinitions.CreateFunctionsWithoutShareContainmentSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The refreshed functions are backward compatible with the preceding schema, so retaining
        // them is safer than restoring definitions that omitted valid audio and text entities.
    }
}
