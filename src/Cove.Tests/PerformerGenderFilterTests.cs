using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Cove.Tests;

public class PerformerGenderFilterTests
{
    // The filter UI sends a multi-select gender choice as `^(?:A|B)$`. Matching that regex against the
    // enum rendered as text left PostgreSQL estimating a single matching performer, and a related
    // performer filter built on that estimate re-scanned every occurrence tag per link until it timed out.
    [Theory]
    [InlineData(CriterionModifier.MatchesRegex)]
    [InlineData(CriterionModifier.NotMatchesRegex)]
    [InlineData(CriterionModifier.Equals)]
    [InlineData(CriterionModifier.NotEquals)]
    public async Task SelectedGendersCompareTheEnumColumn(CriterionModifier modifier)
    {
        using var db = CreatePostgresContext();
        var value = modifier is CriterionModifier.MatchesRegex or CriterionModifier.NotMatchesRegex
            ? "^(?:Female|TransgenderFemale)$"
            : "Female";

        var sql = (await new PerformerRepository(db).BuildFilteredQueryAsync(
                new PerformerFilter { GenderCriterion = new StringCriterion { Modifier = modifier, Value = value } },
                null,
                includeRelatedFilters: false,
                allowReadScopeOptimization: false,
                ct: TestContext.Current.CancellationToken))
            .Select(performer => performer.Id)
            .ToQueryString();

        Assert.DoesNotContain("'TransgenderMale'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("~", sql, StringComparison.Ordinal);
        Assert.Contains("\"Gender\" = ANY (", sql, StringComparison.Ordinal);
        Assert.Contains(
            modifier is CriterionModifier.Equals or CriterionModifier.MatchesRegex ? "\"Gender\" IS NOT NULL AND" : "\"Gender\" IS NULL OR",
            sql,
            StringComparison.Ordinal);
    }

    [Theory]
    [RequiresPostgres]
    [InlineData(CriterionModifier.MatchesRegex, "^(?:Female|TransgenderFemale)$", new[] { "Female", "TransgenderFemale" })]
    [InlineData(CriterionModifier.MatchesRegex, "^(?:female)$", new[] { "Female" })]
    [InlineData(CriterionModifier.MatchesRegex, "^(?:Female|Unknown)$", new[] { "Female" })]
    [InlineData(CriterionModifier.MatchesRegex, "male$", new[] { "Male", "Female", "TransgenderMale", "TransgenderFemale" })]
    [InlineData(CriterionModifier.NotMatchesRegex, "^(?:Female|TransgenderFemale)$", new[] { "Male", "TransgenderMale", "Intersex", "NonBinary", "99", null })]
    [InlineData(CriterionModifier.Equals, "Female", new[] { "Female" })]
    [InlineData(CriterionModifier.Equals, "female", new string?[0])]
    [InlineData(CriterionModifier.NotEquals, "Female", new[] { "Male", "TransgenderMale", "TransgenderFemale", "Intersex", "NonBinary", "99", null })]
    [InlineData(CriterionModifier.Equals, "99", new[] { "99" })]
    [InlineData(CriterionModifier.NotEquals, "99", new[] { "Male", "Female", "TransgenderMale", "TransgenderFemale", "Intersex", "NonBinary", null })]
    [InlineData(CriterionModifier.Equals, null, new string?[] { null })]
    public async Task GenderCriterionKeepsItsMatchingSemantics(CriterionModifier modifier, string? value, string?[] expectedGenders)
    {
        await using var fixture = await EntityListSortBehaviorHarnessTests.SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();
        // (GenderEnum)99 stands for a stored value outside the enum, which the API's Enum.TryParse accepts.
        GenderEnum?[] genders = [.. Enum.GetValues<GenderEnum>().Select(gender => (GenderEnum?)gender), (GenderEnum)99, null];
        var performers = genders.Select((gender, index) => new Performer { Id = 9001 + index, Name = $"Gender {index}", Gender = gender }).ToArray();
        fixture.Context.AddRange(performers);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matchingIds = await (await new PerformerRepository(fixture.Context).BuildFilteredQueryAsync(
                new PerformerFilter { GenderCriterion = new StringCriterion { Modifier = modifier, Value = value! } },
                null,
                includeRelatedFilters: false,
                allowReadScopeOptimization: false,
                ct: TestContext.Current.CancellationToken))
            .Select(performer => performer.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        var expectedIds = performers
            .Where(performer => expectedGenders.Contains(performer.Gender?.ToString()))
            .Select(performer => performer.Id);
        var actualIds = performers
            .Where(performer => matchingIds.Contains(performer.Id))
            .Select(performer => performer.Id);
        Assert.Equal(expectedIds.Order(), actualIds.Order());
    }

    private static CoveContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=query_shape;Username=query_shape;Password=query_shape",
                npgsql => npgsql.UseVector())
            .Options;

        return new CoveContext(options);
    }
}
