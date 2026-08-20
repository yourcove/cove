using Cove.Api.Controllers;

namespace Cove.Tests;

public sealed class DatabaseControllerTests
{
    [Fact]
    public void BuildVacuumAnalyzeCommand_QualifiesAndQuotesOnlyDiscoveredRelations()
    {
        var command = DatabaseController.BuildVacuumAnalyzeCommand(
        [
            new("extension_data", "events"),
            new("public", "ordinary"),
            new("public", "odd\"name"),
        ]);

        Assert.Equal(
            "VACUUM ANALYZE \"extension_data\".\"events\", \"public\".\"ordinary\", \"public\".\"odd\"\"name\"",
            command);
        Assert.DoesNotContain("pg_catalog", command, StringComparison.Ordinal);
    }
}
