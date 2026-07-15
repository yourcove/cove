using Cove.Core.Interfaces;
using Cove.Data.Repositories;

namespace Cove.Tests;

public class RequiredMultiIdCriterionTests
{
    private sealed record Item(int Id, int[] TagIds);
    private sealed record ScalarItem(int Id, int? StudioId);

    [Fact]
    public void RequiredIds_Preserve_IncludesAny_Semantics()
    {
        var items = new[]
        {
            new Item(1, [1206, 999]),
            new Item(2, [1206, 1000]),
            new Item(3, [1206, 999, 1000]),
            new Item(4, [999]),
            new Item(5, [1206]),
        }.AsQueryable();
        var criterion = new MultiIdCriterion
        {
            Value = [999, 1000],
            Modifier = CriterionModifier.Includes,
            RequiredIds = [1206],
        };

        var result = FilterHelpers.ApplyMultiId(items, criterion, item => item.TagIds).Select(item => item.Id).ToArray();

        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void RequiredIds_Preserve_ExcludesAll_Semantics()
    {
        var items = new[]
        {
            new Item(1, [1206, 999]),
            new Item(2, [1206, 999, 1000]),
            new Item(3, [999]),
        }.AsQueryable();
        var criterion = new MultiIdCriterion
        {
            Value = [999, 1000],
            Modifier = CriterionModifier.ExcludesAll,
            RequiredIds = [1206],
        };

        var result = FilterHelpers.ApplyMultiId(items, criterion, item => item.TagIds).Select(item => item.Id).ToArray();

        Assert.Equal([1], result);
    }

    [Fact]
    public void RequiredIds_Preserve_Scalar_IncludesAll_Semantics()
    {
        var items = new[]
        {
            new ScalarItem(1, 99),
            new ScalarItem(2, 100),
        }.AsQueryable();
        var criterion = new MultiIdCriterion
        {
            Value = [99, 100],
            Modifier = CriterionModifier.IncludesAll,
            RequiredIds = [99],
        };

        var result = FilterHelpers.ApplyStudioCriterion(items, criterion, item => item.StudioId).Select(item => item.Id).ToArray();

        Assert.Empty(result);
    }

    [Fact]
    public void RequiredIds_Preserve_Scalar_ExcludesAll_Semantics()
    {
        var items = new[]
        {
            new ScalarItem(1, 99),
            new ScalarItem(2, 100),
        }.AsQueryable();
        var criterion = new MultiIdCriterion
        {
            Value = [99, 100],
            Modifier = CriterionModifier.ExcludesAll,
            RequiredIds = [99],
        };

        var result = FilterHelpers.ApplyStudioCriterion(items, criterion, item => item.StudioId).Select(item => item.Id).ToArray();

        Assert.Equal([1], result);
    }
}
