using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Filters;

public sealed class AiAnalysisFilterApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string Core = "ext:ai.core";
    private const string Other = "ext:other.tagger";
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenRunsInEveryState_WhenFilteredByLatestState_ThenEachVideoMatchesItsLatestRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var db = AsDbUser();
        var never = await CreateVideoAsync("never");
        var completed = await CreateVideoAsync("completed");
        var failed = await CreateVideoAsync("failed");
        var cancelled = await CreateVideoAsync("cancelled");
        var running = await CreateVideoAsync("running");
        var newerWins = await CreateVideoAsync("newer wins");
        var tie = await CreateVideoAsync("tie");
        var otherOnly = await CreateVideoAsync("other source only");
        var mixed = await CreateVideoAsync("mixed sources");

        await db.CreateAiRunAsync(AiRunTargetType.Video, completed, Core, AiRunStatus.Completed, Now.AddHours(-3), Now.AddHours(-2), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, failed, Core, AiRunStatus.Failed, Now.AddHours(-3), Now.AddHours(-2), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, cancelled, Core, AiRunStatus.Cancelled, Now.AddHours(-3), Now.AddHours(-2), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, running, Core, AiRunStatus.Running, Now.AddMinutes(-5), cancellationToken: ct);
        // A newer completed run supersedes an older failure.
        await db.CreateAiRunAsync(AiRunTargetType.Video, newerWins, Core, AiRunStatus.Failed, Now.AddDays(-2), Now.AddDays(-2), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, newerWins, Core, AiRunStatus.Completed, Now.AddDays(-1), Now.AddDays(-1), cancellationToken: ct);
        // Same start: the higher id (recorded later) is the latest.
        var tieStart = Now.AddHours(-6);
        await db.CreateAiRunAsync(AiRunTargetType.Video, tie, Core, AiRunStatus.Completed, tieStart, tieStart.AddMinutes(1), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, tie, Core, AiRunStatus.Failed, tieStart, tieStart.AddMinutes(2), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, otherOnly, Other, AiRunStatus.Completed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, mixed, Core, AiRunStatus.Completed, Now.AddDays(-3), Now.AddDays(-3), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, mixed, Other, AiRunStatus.Failed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        int[] all = [never, completed, failed, cancelled, running, newerWins, tie, otherOnly, mixed];

        var cases = new (string? Source, AiAnalysisState[] States, int[] Expected)[]
        {
            (Core, [AiAnalysisState.NeverRun], [never, otherOnly]),
            (Core, [AiAnalysisState.Completed], [completed, newerWins, mixed]),
            (Core, [AiAnalysisState.Failed], [failed, tie]),
            (Core, [AiAnalysisState.Cancelled], [cancelled]),
            (Core, [AiAnalysisState.InProgress], [running]),
            (Core, [AiAnalysisState.NeverRun, AiAnalysisState.Failed], [never, otherOnly, failed, tie]),
            (Core, [AiAnalysisState.NeverRun, AiAnalysisState.Cancelled], [never, otherOnly, cancelled]),
            (Core, [], all),
            // Without a source the latest run across every source decides.
            (null, [AiAnalysisState.NeverRun], [never]),
            (null, [AiAnalysisState.Completed], [completed, newerWins, otherOnly]),
            (null, [AiAnalysisState.Failed], [failed, tie, mixed]),
        };

        foreach (var (source, states, expected) in cases)
        {
            var ids = await FindVideoIdsAsync(owner, all, new AiAnalysisCriterion { SourceKey = source, States = [.. states] });
            ids.Should().BeEquivalentTo(expected, $"source {source ?? "any"} with states {string.Join(",", states)}");
        }

        // The criterion is an ordinary leaf in filter expressions too.
        var expression = await owner.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [.. all] },
            FilterExpression = new FilterExpression<VideoFilter>
            {
                Operator = FilterExpressionOperator.Or,
                Children =
                [
                    new() { Filter = new VideoFilter { AiAnalysisCriterion = new AiAnalysisCriterion { SourceKey = Core, States = [AiAnalysisState.Failed] } } },
                    new() { Filter = new VideoFilter { AiAnalysisCriterion = new AiAnalysisCriterion { SourceKey = Core, States = [AiAnalysisState.InProgress] } } },
                ],
            },
            FindFilter = new FindFilter { PerPage = 50 },
        }, ct);
        expression.Items.Select(video => video.Id).Should().BeEquivalentTo([failed, tie, running]);

        async Task<int> CreateVideoAsync(string name)
            => (await owner.CreateVideoAsync($"AI analysis {name} {Guid.NewGuid():N}", ct)).Id;
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenModelsAcrossRuns_WhenFilteredByModel_ThenAnyCompletedRunCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var db = AsDbUser();
        var reRun = (await owner.CreateVideoAsync($"AI model re-run {Guid.NewGuid():N}", ct)).Id;
        var otherModel = (await owner.CreateVideoAsync($"AI model other {Guid.NewGuid():N}", ct)).Id;
        var failedOnly = (await owner.CreateVideoAsync($"AI model failed {Guid.NewGuid():N}", ct)).Id;
        var never = (await owner.CreateVideoAsync($"AI model never {Guid.NewGuid():N}", ct)).Id;
        var named = (await owner.CreateVideoAsync($"AI model named {Guid.NewGuid():N}", ct)).Id;

        // The planner re-ran only the superseded tagger, so the latest run lists a subset of the models.
        await db.CreateAiRunAsync(AiRunTargetType.Video, reRun, Core, AiRunStatus.Completed, Now.AddDays(-5), Now.AddDays(-5),
            """[{"config_name":"clip_v1","name":"CLIP"},{"config_name":"tagger_v1","name":"Tagger"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, reRun, Core, AiRunStatus.Completed, Now.AddDays(-1), Now.AddDays(-1),
            """[{"config_name":"tagger_v2","name":"Tagger"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, otherModel, Core, AiRunStatus.Completed, Now.AddDays(-1), Now.AddDays(-1),
            """[{"config_name":"clip_v2","name":"CLIP"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, failedOnly, Core, AiRunStatus.Failed, Now.AddDays(-1), Now.AddDays(-1),
            """[{"config_name":"clip_v1","name":"CLIP"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, named, Core, AiRunStatus.Completed, Now.AddDays(-1), Now.AddDays(-1),
            """[{"config_name":"faces_cfg","name":"Face Finder"}]""", ct);
        int[] all = [reRun, otherModel, failedOnly, never, named];

        (await FindVideoIdsAsync(owner, all, Models(CriterionModifier.Includes, "clip_v1"))).Should().BeEquivalentTo([reRun]);
        (await FindVideoIdsAsync(owner, all, Models(CriterionModifier.Includes, "tagger_v2", "clip_v2"))).Should().BeEquivalentTo([reRun, otherModel]);
        // "Not used" also matches items never analysed and items whose only run failed.
        (await FindVideoIdsAsync(owner, all, Models(CriterionModifier.Excludes, "clip_v1"))).Should().BeEquivalentTo([otherModel, failedOnly, never, named]);
        // A display name matches as well as the configuration name.
        (await FindVideoIdsAsync(owner, all, Models(CriterionModifier.Includes, "Face Finder"))).Should().BeEquivalentTo([named]);
        // Combined with a state: completed most recently, yet never analysed with clip_v1.
        (await FindVideoIdsAsync(owner, all, new AiAnalysisCriterion
        {
            SourceKey = Core,
            States = [AiAnalysisState.Completed],
            ModelKeys = ["clip_v1"],
            ModelModifier = CriterionModifier.Excludes,
        })).Should().BeEquivalentTo([otherModel, named]);

        static AiAnalysisCriterion Models(CriterionModifier modifier, params string[] keys)
            => new() { SourceKey = Core, ModelKeys = [.. keys], ModelModifier = modifier };
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenCompletionTimes_WhenFilteredByLastAnalysed_ThenTheLatestCompletionIsCompared()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var db = AsDbUser();
        var old = (await owner.CreateVideoAsync($"AI date old {Guid.NewGuid():N}", ct)).Id;
        var recent = (await owner.CreateVideoAsync($"AI date recent {Guid.NewGuid():N}", ct)).Id;
        var failedOnly = (await owner.CreateVideoAsync($"AI date failed {Guid.NewGuid():N}", ct)).Id;
        var never = (await owner.CreateVideoAsync($"AI date never {Guid.NewGuid():N}", ct)).Id;

        await db.CreateAiRunAsync(AiRunTargetType.Video, old, Core, AiRunStatus.Completed, Now.AddDays(-10), Now.AddDays(-10), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, recent, Core, AiRunStatus.Completed, Now.AddDays(-20), Now.AddDays(-20), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, recent, Core, AiRunStatus.Completed, Now.AddHours(-2), Now.AddHours(-1), cancellationToken: ct);
        // A later failure does not move "last analysed".
        await db.CreateAiRunAsync(AiRunTargetType.Video, old, Core, AiRunStatus.Failed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, failedOnly, Core, AiRunStatus.Failed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        int[] all = [old, recent, failedOnly, never];

        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.GreaterThan, "-2d"))).Should().BeEquivalentTo([recent]);
        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.LessThan, "-2d"))).Should().BeEquivalentTo([old]);
        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.IsNull, ""))).Should().BeEquivalentTo([failedOnly, never]);
        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.NotNull, ""))).Should().BeEquivalentTo([old, recent]);
        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.Between, Now.AddDays(-11).ToString("O"), Now.AddDays(-9).ToString("O"))))
            .Should().BeEquivalentTo([old]);
        (await FindVideoIdsAsync(owner, all, Date(CriterionModifier.LessThan, Now.AddDays(-5).ToString("O")))).Should().BeEquivalentTo([old]);

        static AiAnalysisCriterion Date(CriterionModifier modifier, string value, string? value2 = null)
            => new() { SourceKey = Core, AnalysedAtCriterion = new TimestampCriterion { Value = value, Value2 = value2, Modifier = modifier } };
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/find")]
    public async Task GivenImageRuns_WhenImagesAreFilteredByLatestState_ThenOnlyImageRunsCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var db = AsDbUser();
        var failed = (await owner.CreateImageAsync($"AI image failed {Guid.NewGuid():N}", ct)).Id;
        var never = (await owner.CreateImageAsync($"AI image never {Guid.NewGuid():N}", ct)).Id;
        await db.CreateAiRunAsync(AiRunTargetType.Image, failed, Core, AiRunStatus.Failed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        // A video run with the same id must not count for the image.
        await db.CreateAiRunAsync(AiRunTargetType.Video, never, Core, AiRunStatus.Completed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);

        var result = await owner.FindImagesAsync(new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter
            {
                Ids = [failed, never],
                AiAnalysisCriterion = new AiAnalysisCriterion { SourceKey = Core, States = [AiAnalysisState.NeverRun, AiAnalysisState.Failed] },
            },
            FindFilter = new FindFilter { PerPage = 20 },
        }, ct);
        result.Items.Select(image => image.Id).Should().BeEquivalentTo([failed, never]);

        var completed = await owner.FindImagesAsync(new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter { Ids = [failed, never], AiAnalysisCriterion = new AiAnalysisCriterion { States = [AiAnalysisState.Completed] } },
            FindFilter = new FindFilter { PerPage = 20 },
        }, ct);
        completed.Items.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("GET", "/api/ai-runs/facets")]
    public async Task GivenRuns_WhenFacetsAreRead_ThenSourcesAndCompletedModelsAreListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var db = AsDbUser();
        var suffix = Guid.NewGuid().ToString("N");
        var source = $"ext:facets.{suffix}";
        var video = (await owner.CreateVideoAsync($"AI facets {suffix}", ct)).Id;
        await db.CreateAiRunAsync(AiRunTargetType.Video, video, source, AiRunStatus.Completed, Now.AddHours(-2), Now.AddHours(-2),
            """[{"config_name":"clip_v2","name":"CLIP"},{"name":"Unnamed config"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, video, source, AiRunStatus.Completed, Now.AddHours(-1), Now.AddHours(-1),
            """[{"config_name":"clip_v2","name":"CLIP"},{"name":"Unnamed config"}]""", ct);
        await db.CreateAiRunAsync(AiRunTargetType.Video, video, source, AiRunStatus.Failed, Now, Now,
            """[{"config_name":"broken_v9"}]""", ct);

        var facets = await owner.GetAiRunFacetsAsync(AiRunTargetType.Video, source, includeModels: true, ct);

        facets.Sources.Should().ContainSingle(item => item.SourceKey == source).Which.RunCount.Should().Be(3);
        facets.Models.Should().NotBeNull();
        facets.Models!.Select(model => model.Key).Should().BeEquivalentTo(["clip_v2", "Unnamed config"]);
        facets.Models!.Single(model => model.Key == "clip_v2").Name.Should().Be("CLIP");
        (await owner.GetAiRunFacetsAsync(AiRunTargetType.Image, cancellationToken: ct)).Sources.Should().NotContain(item => item.SourceKey == source);
        (await owner.GetAiRunFacetsAsync(AiRunTargetType.Video, cancellationToken: ct)).Models.Should().BeNull();
        using (var reader = await CreateVideoReaderWithoutAiRunsAsync(ct))
            await reader.Client.AssertResponseAsync("/api/ai-runs/facets?targetType=video", HttpStatusCode.Forbidden, ct);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenACallerWithoutAiRunAccess_WhenFilteringByAiAnalysis_ThenTheRequestIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { AiAnalysisCriterion = new AiAnalysisCriterion { States = [AiAnalysisState.NeverRun] } },
        };

        // This caller reads videos but not AI runs; "never run" would otherwise match their whole library.
        using (var reader = await CreateVideoReaderWithoutAiRunsAsync(ct))
            (await reader.Client.SendStatusAsync(HttpMethod.Post, "/api/videos/find", request, ct)).Should().Be(HttpStatusCode.Forbidden);
        (await AsUser().SendStatusAsync(HttpMethod.Post, "/api/videos/find", new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { AiAnalysisCriterion = new AiAnalysisCriterion { Analysis = "tags", States = [AiAnalysisState.Completed] } },
        }, ct)).Should().Be(HttpStatusCode.BadRequest);
    }

    // The built-in Member and Viewer roles both grant airuns.read, so a caller without it needs a role of its own.
    private async Task<CoveAuthSession> CreateVideoReaderWithoutAiRunsAsync(CancellationToken ct)
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var roleName = $"Video reader without AI runs {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(roleName, "Reads videos but not AI runs.", [Permissions.VideosRead]), ct);
        var username = $"video-reader-{suffix}";
        const string password = "Video reader password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), ct);
        return await owner.CreateAuthSessionAsync(username, password, ct);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenARestrictedReader_WhenFilteringByAiAnalysis_ThenHiddenVideosStayHidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"AI analysis hidden tag {suffix}", ct);
        var visibleNever = (await owner.CreateVideoAsync($"AI visible never {suffix}", ct)).Id;
        var visibleDone = (await owner.CreateVideoAsync($"AI visible done {suffix}", ct)).Id;
        var hiddenNever = (await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"AI hidden never {suffix}").WithTags([hiddenTag]).Build(), ct)).Id;
        var hiddenDone = (await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"AI hidden done {suffix}").WithTags([hiddenTag]).Build(), ct)).Id;
        await AsDbUser().CreateAiRunAsync(AiRunTargetType.Video, visibleDone, Core, AiRunStatus.Completed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);
        await AsDbUser().CreateAiRunAsync(AiRunTargetType.Video, hiddenDone, Core, AiRunStatus.Completed, Now.AddHours(-1), Now.AddHours(-1), cancellationToken: ct);

        var roleName = $"AI analysis reader {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(roleName, "Reads videos and AI runs except one tag.",
            [Permissions.VideosRead, Permissions.AiRunsRead]), ct);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), ct);
        var username = $"ai-analysis-reader-{suffix}";
        const string password = "AI analysis reader password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), ct);
        using var session = await owner.CreateAuthSessionAsync(username, password, ct);
        int[] all = [visibleNever, visibleDone, hiddenNever, hiddenDone];

        (await FindVideoIdsAsync(session.Client, all, new AiAnalysisCriterion { SourceKey = Core, States = [AiAnalysisState.NeverRun] }))
            .Should().BeEquivalentTo([visibleNever]);
        (await FindVideoIdsAsync(session.Client, all, new AiAnalysisCriterion { SourceKey = Core, States = [AiAnalysisState.Completed] }))
            .Should().BeEquivalentTo([visibleDone]);
    }

    private static async Task<int[]> FindVideoIdsAsync(CoveClient client, int[] ids, AiAnalysisCriterion criterion)
    {
        var result = await client.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [.. ids], AiAnalysisCriterion = criterion },
            FindFilter = new FindFilter { PerPage = 50 },
        }, TestContext.Current.CancellationToken);
        result.TotalCount.Should().Be(result.Items.Count);
        return [.. result.Items.Select(video => video.Id)];
    }
}
