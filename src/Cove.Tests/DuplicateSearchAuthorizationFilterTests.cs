using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pgvector.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class DuplicateSearchAuthorizationFilterTests
{
    [Fact]
    public void DuplicateSearchVideoRelationships_HaveMatchingQueryFilters()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=model_validation;Username=model_validation;Password=model_validation",
                npgsql => npgsql.UseVector())
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;

        using var db = new CoveContext(options);

        Assert.NotEmpty(db.Model.FindEntityType(typeof(DuplicateSearchItem))!.GetDeclaredQueryFilters());
        Assert.NotEmpty(db.Model.FindEntityType(typeof(DuplicateDeletionKeeperReservation))!.GetDeclaredQueryFilters());
    }
}
