using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Images;

public sealed class ImageFindIdsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/images/find-ids")]
    public async Task GivenAPerformerScopedFilter_WhenFindingIds_ThenTheIdsMatchTheListForEveryCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Find image ids focal {suffix}").Build(), ct);
        var partner = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Find image ids partner {suffix}").Build(), ct);
        var kissing = await owner.CreateTagAsync($"Find image ids kissing {suffix}", ct);
        var hiddenTag = await owner.CreateTagAsync($"Find image ids hidden {suffix}", ct);
        var tagged = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Find image ids tagged {suffix}")
            .WithPerformer(focal).WithPerformer(partner)
            .WithTag(kissing)
            .WithRating(80)
            .Build(), ct);
        var taggedHidden = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Find image ids tagged hidden {suffix}")
            .WithPerformer(focal).WithPerformer(partner)
            .WithTag(kissing).WithTag(hiddenTag)
            .Build(), ct);
        var untagged = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Find image ids untagged {suffix}")
            .WithPerformer(focal).WithPerformer(partner)
            .WithRating(80)
            .Build(), ct);
        await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Find image ids tagged without focal {suffix}")
            .WithPerformer(partner)
            .WithTag(kissing)
            .Build(), ct);
        var request = new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter
            {
                PerformersCriterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Value = [focal.Id] },
                TagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [kissing.Id] },
            },
        };

        var ids = await owner.FindImageIdsAsync(request, ct);
        var list = await owner.FindImagesAsync(request, ct);

        ids.Ids.Should().BeEquivalentTo([tagged.Id, taggedHidden.Id]);
        ids.Ids.Should().BeEquivalentTo(list.Items.Select(image => image.Id));

        // The ids follow the list's sort, and a page size on the request does not cut them short.
        var sortedIds = await owner.FindImageIdsAsync(new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter
            {
                PerformersCriterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Value = [focal.Id] },
            },
            FindFilter = new FindFilter { Sort = "title", Direction = SortDirection.Desc, Page = 1, PerPage = 1 },
        }, ct);
        sortedIds.Ids.Should().Equal(untagged.Id, taggedHidden.Id, tagged.Id);

        var roleName = $"Find image ids restricted {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Hides images with one tag.",
            [Permissions.ImagesRead, Permissions.PerformersRead, Permissions.TagsRead]), ct);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Image, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), ct);
        var username = $"find-image-ids-restricted-{suffix}";
        const string password = "Find image ids restricted password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), ct);
        using var session = await owner.CreateAuthSessionAsync(username, password, ct);

        var restricted = await session.Client.FindImageIdsAsync(request, ct);

        restricted.Ids.Should().Equal(tagged.Id);
    }
}
