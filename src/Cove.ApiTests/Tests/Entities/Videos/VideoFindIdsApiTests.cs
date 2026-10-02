using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoFindIdsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/find-ids")]
    public async Task GivenAPerformerScopedFilter_WhenFindingIds_ThenTheIdsMatchTheListForEveryCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Find ids focal {suffix}").Build(), ct);
        var partner = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Find ids partner {suffix}").Build(), ct);
        var kissing = await owner.CreateTagAsync($"Find ids kissing {suffix}", ct);
        var hiddenTag = await owner.CreateTagAsync($"Find ids hidden {suffix}", ct);
        var tagged = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Find ids tagged {suffix}")
            .WithPerformers([focal, partner])
            .WithTags([kissing])
            .WithRating(80)
            .Build(), ct);
        var taggedHidden = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Find ids tagged hidden {suffix}")
            .WithPerformers([focal, partner])
            .WithTags([kissing, hiddenTag])
            .Build(), ct);
        var untagged = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Find ids untagged {suffix}")
            .WithPerformers([focal, partner])
            .WithRating(80)
            .Build(), ct);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Find ids tagged without focal {suffix}")
            .WithPerformers([partner])
            .WithTags([kissing])
            .Build(), ct);
        var request = new VideoFilteredQueryRequest
        {
            ObjectFilter = new VideoFilter
            {
                PerformersCriterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Value = [focal.Id] },
                TagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [kissing.Id] },
            },
        };

        var ids = await owner.FindVideoIdsAsync(request, ct);
        var list = await owner.FindVideosAsync(request, ct);

        ids.Ids.Should().BeEquivalentTo([tagged.Id, taggedHidden.Id]);
        ids.Ids.Should().BeEquivalentTo(list.Items.Select(video => video.Id));

        // An expression narrows the ids as it narrows the list: tagged or highly rated, among the focal's videos.
        var expressionIds = await owner.FindVideoIdsAsync(new VideoFilteredQueryRequest
        {
            ObjectFilter = new VideoFilter
            {
                PerformersCriterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Value = [focal.Id] },
            },
            FilterExpression = new FilterExpression<VideoFilter>
            {
                Operator = FilterExpressionOperator.Or,
                Children =
                [
                    new() { Filter = new VideoFilter { TagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [kissing.Id] } } },
                    new() { Filter = new VideoFilter { RatingCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 60 } } },
                ],
            },
        }, ct);
        expressionIds.Ids.Should().BeEquivalentTo([tagged.Id, taggedHidden.Id, untagged.Id]);

        var roleName = $"Find ids restricted {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Hides videos with one tag.",
            [Permissions.VideosRead, Permissions.PerformersRead, Permissions.TagsRead]), ct);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), ct);
        var username = $"find-ids-restricted-{suffix}";
        const string password = "Find ids restricted password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), ct);
        using var session = await owner.CreateAuthSessionAsync(username, password, ct);

        var restricted = await session.Client.FindVideoIdsAsync(request, ct);

        restricted.Ids.Should().Equal(tagged.Id);

        var invalidExpression = () => owner.FindVideoIdsAsync(new VideoFilteredQueryRequest
        {
            FilterExpression = new FilterExpression<VideoFilter> { Children = [new()] },
        }, ct);
        await invalidExpression.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }
}
