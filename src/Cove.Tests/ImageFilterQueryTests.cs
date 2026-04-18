using System.Linq.Expressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Tests;

/// <summary>
/// Tests the ApplyImageMultiIdCriterion expression tree logic using plain LINQ-to-Objects.
/// This validates the expression trees produce correct filter predicates.
/// </summary>
public class ImageFilterQueryTests
{
    private static IQueryable<Image> CreateTestData()
    {
        var images = new List<Image>
        {
            // Image 1: has "4k available" tag (id=1)
            new() { Id = 1, Title = "Image One", ImageTags = [new() { TagId = 1, ImageId = 1 }] },
            // Image 2: has "HD" tag only (id=2)
            new() { Id = 2, Title = "Image Two", ImageTags = [new() { TagId = 2, ImageId = 2 }] },
            // Image 3: has both tags
            new() { Id = 3, Title = "Image Three", ImageTags = [new() { TagId = 1, ImageId = 3 }, new() { TagId = 2, ImageId = 3 }] },
            // Image 4: has no tags
            new() { Id = 4, Title = "Image Four", ImageTags = [] },
        };
        return images.AsQueryable();
    }

    /// <summary>
    /// Reimplementation of the private ApplyImageMultiIdCriterion to test in isolation.
    /// This is the EXACT same expression tree logic from ImageRepository.
    /// </summary>
    private static IQueryable<Image> ApplyMultiIdCriterion(
        IQueryable<Image> query, MultiIdCriterion criterion,
        Expression<Func<Image, IEnumerable<int>>> idsSelector)
    {
        if (criterion.Value.Count == 0) return query;
        var ids = criterion.Value;
        var imageParam = idsSelector.Parameters[0];
        var imageIds = idsSelector.Body;
        var idParam = Expression.Parameter(typeof(int), "id");
        var selectedConst = Expression.Constant(ids);

        var anyInImage = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Any), [typeof(int)],
            imageIds,
            Expression.Lambda<Func<int, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], selectedConst, idParam), idParam));

        var allIdParam = Expression.Parameter(typeof(int), "allId");
        var allInImage = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.All), [typeof(int)],
            selectedConst,
            Expression.Lambda<Func<int, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], imageIds, allIdParam), allIdParam));

        Expression body = criterion.Modifier switch
        {
            CriterionModifier.Includes => anyInImage,
            CriterionModifier.Excludes => Expression.Not(anyInImage),
            CriterionModifier.IncludesAll => allInImage,
            CriterionModifier.ExcludesAll => Expression.Not(allInImage),
            _ => anyInImage,
        };

        return query.Where(Expression.Lambda<Func<Image, bool>>(body, imageParam));
    }

    [Fact]
    public void TagsCriterion_Includes_SingleTag()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.Includes };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Id == 1);
        Assert.Contains(result, i => i.Id == 3);
    }

    [Fact]
    public void TagsCriterion_IncludesAll_SingleTag()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.IncludesAll };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        // IncludesAll with single tag = same as Includes
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Id == 1);
        Assert.Contains(result, i => i.Id == 3);
    }

    [Fact]
    public void TagsCriterion_IncludesAll_MultipleTags()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [1, 2], Modifier = CriterionModifier.IncludesAll };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        // Only image 3 has BOTH tags
        Assert.Single(result);
        Assert.Equal(3, result[0].Id);
    }

    [Fact]
    public void TagsCriterion_Excludes_SingleTag()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [1], Modifier = CriterionModifier.Excludes };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Id == 2);
        Assert.Contains(result, i => i.Id == 4);
    }

    [Fact]
    public void TagsCriterion_ExcludesAll_MultipleTags()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [1, 2], Modifier = CriterionModifier.ExcludesAll };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        // ExcludesAll = NOT all tags present. Images 1 (has tag 1 only), 2 (has tag 2 only), 4 (no tags) should match.
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, i => i.Id == 3); // Image 3 has both, so excluded
    }

    [Fact]
    public void EmptyTagList_ReturnsAllImages()
    {
        var query = CreateTestData();
        var criterion = new MultiIdCriterion { Value = [], Modifier = CriterionModifier.Includes };
        var result = ApplyMultiIdCriterion(query, criterion, i => i.ImageTags.Select(it => it.TagId)).ToList();

        Assert.Equal(4, result.Count); // Empty filter = no filtering
    }
}
