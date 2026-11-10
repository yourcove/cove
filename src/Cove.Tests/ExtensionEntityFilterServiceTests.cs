using System.Text.Json;
using Cove.Api.Services;
using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Cove.Plugins;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class ExtensionEntityFilterServiceTests
{
    [Fact]
    public async Task ApplyAsync_intersects_batches_and_preserves_candidate_order()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "spoofed", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds.Where(id => id % 2 == 0).ToArray(), "revision-1"));
        var service = new ExtensionEntityFilterService(runtime, batchSize: 2);

        var result = await service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [5, 4, 3, 2, 1], CovePrincipal.System(), TestContext.Current.CancellationToken);

        Assert.Equal([4, 2], result);
        Assert.Equal(1, runtime.OpenCount);
        Assert.Equal(3, runtime.Requests.Count);
        Assert.All(runtime.Requests, request => Assert.Equal("owner.actual", request.ExtensionId));
    }

    [Theory]
    [InlineData("owner.disabled", "has-preview", "equals", "true")]
    [InlineData("owner.actual", "undeclared", "equals", "true")]
    [InlineData("owner.actual", "has-preview", "includes", "true")]
    [InlineData("owner.actual", "has-preview", "equals", "\"true\"")]
    public async Task ApplyAsync_fails_closed_for_unavailable_or_invalid_criteria(
        string extensionId,
        string filterId,
        string modifier,
        string valueJson)
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var service = new ExtensionEntityFilterService(runtime, batchSize: 2);

        var criterion = new ExtensionFilterCriterion
        {
            ExtensionId = extensionId,
            FilterId = filterId,
            Modifier = modifier,
            Value = JsonDocument.Parse(valueJson).RootElement.Clone(),
        };

        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("tags", [criterion], [1, 2], CovePrincipal.System(), TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ApplyAsync_rejects_oversized_client_controlled_criterion_fields()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, "unused-revision"));
        var service = new ExtensionEntityFilterService(runtime);

        var oversizedOwner = Criterion(new string('x', ExtensionEntityFilterService.DefaultIdentifierLengthLimit + 1), "has-preview", true);
        var oversizedFilter = Criterion("owner.actual", new string('x', ExtensionEntityFilterService.DefaultIdentifierLengthLimit + 1), true);
        var oversizedModifier = Criterion("owner.actual", "has-preview", true);
        oversizedModifier.Modifier = new string('x', ExtensionEntityFilterService.DefaultModifierLengthLimit + 1);

        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("tags", [oversizedOwner], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("tags", [oversizedFilter], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("tags", [oversizedModifier], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ApplyAsync_does_not_impose_a_global_candidate_limit()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var service = new ExtensionEntityFilterService(runtime);
        var candidates = Enumerable.Range(1, 5_001).ToArray();

        var result = await service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], candidates, CovePrincipal.System(), TestContext.Current.CancellationToken);

        Assert.Equal(candidates, result);
    }

    [Fact]
    public async Task ApplyAsync_rejects_provider_result_and_criteria_limits()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            _ => new ExtensionEntityFilterResult([999], "revision-1"));
        var service = new ExtensionEntityFilterService(runtime, batchSize: 2);

        await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1, 2], CovePrincipal.System(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExtensionEntityFilterLimitException>(() =>
            service.ApplyAsync("tags", Enumerable.Repeat(Criterion("owner.actual", "has-preview", true), ExtensionEntityFilterService.DefaultCriteriaLimit + 1).ToArray(), [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("includes", """{"value":[3,4],"excludes":[9],"depth":-1}""")]
    [InlineData("includes_all", """{"value":[3,4]}""")]
    [InlineData("includes", """{"value":[],"excludes":[9]}""")]
    [InlineData("excludes", """{"value":[3]}""")]
    [InlineData("is_null", """{}""")]
    [InlineData("not_null", """{"value":[]}""")]
    public async Task ApplyAsync_passes_valid_multi_id_values_to_the_provider_unchanged(string modifier, string valueJson)
    {
        var runtime = new FakeRuntime(
            MultiIdContribution(),
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var service = new ExtensionEntityFilterService(runtime);

        var result = await service.ApplyAsync(
            "videos", [MultiIdCriterion(modifier, valueJson)], [1, 2], CovePrincipal.System(), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], result);
        var request = Assert.Single(runtime.Requests);
        Assert.Equal(valueJson, request.Value.GetRawText());
    }

    [Theory]
    [InlineData("includes", """[3,4]""")]
    [InlineData("includes", """{"value":"3"}""")]
    [InlineData("includes", """{"value":[3.5]}""")]
    [InlineData("includes", """{"value":[0]}""")]
    [InlineData("includes", """{"value":[3],"excludes":[-1]}""")]
    [InlineData("includes", """{"value":[3],"depth":-2}""")]
    [InlineData("includes", """{"value":[3],"_names":{"3":"Kissing"}}""")]
    [InlineData("includes", """{"value":[]}""")]
    [InlineData("includes", """{}""")]
    [InlineData("is_null", """{"value":[3]}""")]
    [InlineData("not_null", """{"excludes":[3]}""")]
    [InlineData("equals", """{"value":[3]}""")]
    public async Task ApplyAsync_rejects_malformed_multi_id_values(string modifier, string valueJson)
    {
        var runtime = new FakeRuntime(
            MultiIdContribution(),
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var service = new ExtensionEntityFilterService(runtime);

        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("videos", [MultiIdCriterion(modifier, valueJson)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ApplyAsync_rejects_a_multi_id_declaration_without_an_entity_reference_type()
    {
        var runtime = new FakeRuntime(
            MultiIdContribution() with { EntityReferenceType = null },
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var service = new ExtensionEntityFilterService(runtime);

        await Assert.ThrowsAsync<ExtensionEntityFilterValidationException>(() =>
            service.ApplyAsync("videos", [MultiIdCriterion("includes", """{"value":[3]}""")], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ApplyAsync_composes_multiple_extension_filters_deterministically()
    {
        var runtime = new MultiFilterRuntime();
        var service = new ExtensionEntityFilterService(runtime, batchSize: 2);

        var result = await service.ApplyAsync("tags", [Criterion("owner.actual", "even", true), Criterion("owner.actual", "over-two", true)], [5, 4, 3, 2, 1], CovePrincipal.System(), TestContext.Current.CancellationToken);

        Assert.Equal([4], result);
        Assert.Equal(["even", "even", "even", "over-two"], runtime.Requests.Select(request => request.FilterId));
    }

    [Fact]
    public async Task ApplyAsync_converts_provider_failures_to_bounded_errors()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            _ => throw new InvalidOperationException("provider internals must not escape"));
        var service = new ExtensionEntityFilterService(runtime);

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));

        Assert.Equal("The extension filter provider failed.", error.Message);
    }

    [Fact]
    public async Task ApplyAsync_converts_null_provider_results_to_bounded_errors()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            _ => null!);
        var service = new ExtensionEntityFilterService(runtime);

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));

        Assert.Contains("revision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Tags_find_composes_core_sort_count_and_pagination_after_extension_membership()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        db.Tags.AddRange(
            new Tag { Name = "Alpha" },
            new Tag { Name = "Beta" },
            new Tag { Name = "Gamma" },
            new Tag { Name = "Delta" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds.Where(id => id % 2 == 0).ToArray(), "revision-1"));
        var accessor = new CurrentPrincipalAccessor();
        accessor.Set(CovePrincipal.System());
        var controller = new TagsController(
            new TagRepository(db),
            db,
            new CustomFieldService(db),
            null!,
            extensionFilters: new ExtensionEntityFilterService(runtime),
            principalAccessor: accessor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = await controller.FindPost(new FilteredQueryRequest<TagFilter>
        {
            FindFilter = new FindFilter { Page = 2, PerPage = 1, Sort = "name", Direction = Cove.Core.Enums.SortDirection.Desc },
            ObjectFilter = new TagFilter
            {
                NameCriterion = new StringCriterion { Value = "a", Modifier = CriterionModifier.Includes },
                ExtensionCriteria = [Criterion("owner.actual", "has-preview", true)],
            },
        }, TestContext.Current.CancellationToken);

        var page = Assert.IsType<PaginatedResponse<TagListDto>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal("Beta", Assert.Single(page.Items).Name);
    }

    // Providers may answer per user (Segment Studio by the user's mode), and the find cache key does
    // not identify the user, so a request with extension criteria must reach the provider every time.
    [Fact]
    [RequiresPostgres]
    public async Task Videos_find_does_not_cache_results_that_depend_on_an_extension_provider()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        db.Videos.AddRange(new Video { Title = "Alpha" }, new Video { Title = "Beta" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var matchEverything = true;
        var runtime = new FakeRuntime(
            new UIListFilterContribution("segments", "videos", "Has segments", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-segments" },
            request => new ExtensionEntityFilterResult(matchEverything ? request.CandidateIds : [], "revision-1"));
        var accessor = new CurrentPrincipalAccessor();
        accessor.Set(CovePrincipal.System());
        var controller = new VideosController(
            new VideoRepository(db), db, null!, null!, null!, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), null!, null!,
            new NoOpUserEngagementService(), new CustomFieldService(db), new Cove.Core.Events.EventBus(),
            principalAccessor: accessor,
            extensionFilters: new ExtensionEntityFilterService(runtime))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var query = new VideoFilteredQueryRequest
        {
            ObjectFilter = new VideoFilter { ExtensionCriteria = [Criterion("owner.actual", "has-segments", true)] },
        };

        async Task<int> CountAsync()
        {
            var response = Assert.IsType<OkObjectResult>(await controller.FindPost(query, TestContext.Current.CancellationToken));
            return Assert.IsType<PaginatedResponse<VideoDto>>(response.Value).TotalCount;
        }

        Assert.Equal(2, await CountAsync());
        matchEverything = false;
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Tags_find_keeps_the_candidate_limit_at_its_query_planning_boundary()
    {
        // The candidate limit rejects the request before any query, so it needs no server.
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql("Host=localhost;Database=unused", npgsql => npgsql.UseVector())
            .Options);
        var tags = Enumerable.Range(1, 5_001)
            .Select(id => new Tag { Id = id, Name = $"Tag {id}" })
            .ToArray();
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, "revision-1"));
        var accessor = new CurrentPrincipalAccessor();
        accessor.Set(CovePrincipal.System());
        var controller = new TagsController(
            new PagedTagRepository(tags),
            db,
            new CustomFieldService(db),
            null!,
            extensionFilters: new ExtensionEntityFilterService(runtime),
            principalAccessor: accessor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = await controller.FindPost(new FilteredQueryRequest<TagFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 25 },
            ObjectFilter = new TagFilter
            {
                ExtensionCriteria = [Criterion("owner.actual", "has-preview", true)],
            },
        }, TestContext.Current.CancellationToken);

        var invalid = Assert.IsType<UnprocessableEntityObjectResult>(response.Result);
        var problem = Assert.IsType<ProblemDetails>(invalid.Value);
        Assert.Contains("5000", problem.Detail, StringComparison.Ordinal);
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Tags_graph_filters_the_full_candidate_set_and_authorizes_before_provider_and_node_limit()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accessor = new CurrentPrincipalAccessor();
        await using var db = new CoveContext(database.CreateOptions(), accessor);
        var tags = new[]
        {
            new Tag { Name = "Alpha denied" },
            new Tag { Name = "Beta no preview" },
            new Tag { Name = "Gamma preview" },
            new Tag { Name = "Omega preview" },
        };
        var role = new Role { Name = "Scoped tag reader" };
        db.Tags.AddRange(tags);
        db.Roles.Add(role);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.RoleEntityOverrides.Add(new RoleEntityOverride
        {
            RoleId = role.Id,
            EntityKind = EntityKinds.Tag,
            EntityId = tags[0].Id.ToString(),
            Effect = "deny",
            AppliesTo = "read",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var principal = new CovePrincipal
        {
            UserId = 42,
            Username = "scoped-reader",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>([role.Name], StringComparer.OrdinalIgnoreCase),
            Permissions = new HashSet<string>([Permissions.TagsRead], StringComparer.OrdinalIgnoreCase),
            ReadRestrictedEntityKinds = new HashSet<string>([EntityKinds.Tag], StringComparer.OrdinalIgnoreCase),
        };
        accessor.Set(principal);
        try
        {
            // The deny override hides the first tag from this principal in the database itself.
            Assert.Equal(
                tags.Skip(1).Select(tag => tag.Id).Order(),
                (await db.Tags.AsNoTracking().Select(tag => tag.Id).ToListAsync(TestContext.Current.CancellationToken)).Order());
            var runtime = new FakeRuntime(
                new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
                request => new ExtensionEntityFilterResult(request.CandidateIds.Where(id => id >= tags[2].Id).ToArray(), "revision-1"));
            var controller = new TagsController(
                new PagedTagRepository(tags),
                db,
                new CustomFieldService(db),
                null!,
                extensionFilters: new ExtensionEntityFilterService(runtime),
                principalAccessor: accessor)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };

            var response = await controller.Graph(new FilteredQueryRequest<TagFilter>
            {
                FindFilter = new FindFilter { Page = 1, PerPage = 1, Sort = "name" },
                ObjectFilter = new TagFilter
                {
                    ExtensionCriteria = [Criterion("owner.actual", "has-preview", true)],
                },
            }, TestContext.Current.CancellationToken);

            var graph = Assert.IsType<TagGraphResponseDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(2, graph.TotalCount);
            Assert.Equal(tags[2].Id, Assert.Single(graph.Items).Id);
            var providerCandidates = Assert.Single(runtime.Requests).CandidateIds;
            Assert.DoesNotContain(tags[0].Id, providerCandidates);
            Assert.Contains(tags[3].Id, providerCandidates);
        }
        finally
        {
            accessor.Set(null);
        }
    }

    [Fact]
    public async Task ApplyAsync_bounds_provider_timeout_and_preserves_request_cancellation()
    {
        var runtime = new NonCooperativeRuntime();
        var service = new ExtensionEntityFilterService(runtime, providerTimeout: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), cancelled.Token));
    }

    [Fact]
    public async Task ApplyAsync_applies_the_query_deadline_while_waiting_for_a_provider_generation()
    {
        var service = new ExtensionEntityFilterService(
            new BlockedOpenRuntime(),
            providerTimeout: TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_enforces_one_deadline_across_all_batches_and_criteria()
    {
        var runtime = new DelayedRuntime(TimeSpan.FromMilliseconds(30));
        var service = new ExtensionEntityFilterService(
            runtime,
            batchSize: 1,
            providerTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() => service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true), Criterion("owner.actual", "has-preview", true)], [1, 2], CovePrincipal.System(), TestContext.Current.CancellationToken));

        // Loaded CI may consume most of the deadline in any one awaited call. The bounded failure
        // with four otherwise-successful calls is the behavioral assertion; only require that the
        // provider was entered rather than coupling the test to scheduler timing.
        Assert.InRange(runtime.CallCount, 1, 4);
    }

    [Fact]
    public async Task ApplyAsync_rejects_revision_changes_between_batches_of_one_criterion()
    {
        var calls = 0;
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, ++calls == 1 ? "revision-1" : "revision-2"));
        var service = new ExtensionEntityFilterService(runtime, batchSize: 1);

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1, 2], CovePrincipal.System(), TestContext.Current.CancellationToken));

        Assert.Contains("revision changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_requires_a_bounded_nonempty_revision_for_every_batch()
    {
        var runtime = new FakeRuntime(
            new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"]) { FilterId = "has-preview" },
            request => new ExtensionEntityFilterResult(request.CandidateIds, ""));
        var service = new ExtensionEntityFilterService(runtime);

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            service.ApplyAsync("tags", [Criterion("owner.actual", "has-preview", true)], [1], CovePrincipal.System(), TestContext.Current.CancellationToken));

        Assert.Contains("revision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExtensionFilterCriterion Criterion(string extensionId, string filterId, bool value)
        => new()
        {
            ExtensionId = extensionId,
            FilterId = filterId,
            Modifier = "equals",
            Value = JsonSerializer.SerializeToElement(value),
        };

    private static UIListFilterContribution MultiIdContribution()
        => new("segment-tags", "videos", "Segment tags", "multiid", "owner.actual",
            EntityReferenceType: "tags",
            Modifiers: ["INCLUDES", "INCLUDES_ALL", "EXCLUDES", "IS_NULL", "NOT_NULL"])
        {
            FilterId = "segment-tags",
        };

    private static ExtensionFilterCriterion MultiIdCriterion(string modifier, string valueJson)
        => new()
        {
            ExtensionId = "owner.actual",
            FilterId = "segment-tags",
            Modifier = modifier,
            Value = JsonDocument.Parse(valueJson).RootElement.Clone(),
        };

    private sealed class FakeRuntime(
        UIListFilterContribution contribution,
        Func<ExtensionEntityFilterRequest, ExtensionEntityFilterResult> resolve) : IExtensionEntityFilterRuntime
    {
        public int OpenCount { get; private set; }
        public List<ExtensionEntityFilterRequest> Requests { get; } = [];

        public Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
            string extensionId,
            string entityType,
            string filterId,
            CancellationToken ct)
        {
            OpenCount++;
            IExtensionEntityFilterExecution? execution = string.Equals(extensionId, "owner.actual", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entityType, contribution.EntityType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(filterId, contribution.FilterId, StringComparison.OrdinalIgnoreCase)
                ? new TestFilterExecution(
                    contribution with { ExtensionId = extensionId },
                    (request, _) =>
                    {
                        Requests.Add(request);
                        return Task.FromResult(resolve(request));
                    })
                : null;
            return Task.FromResult(execution);
        }
    }

    private sealed class NonCooperativeRuntime : IExtensionEntityFilterRuntime
    {
        private readonly TaskCompletionSource<ExtensionEntityFilterResult> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
            string extensionId,
            string entityType,
            string filterId,
            CancellationToken ct)
            => Task.FromResult<IExtensionEntityFilterExecution?>(new TestFilterExecution(
                new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"])
                {
                    FilterId = "has-preview",
                },
                (_, _) => _never.Task));
    }

    private sealed class BlockedOpenRuntime : IExtensionEntityFilterRuntime
    {
        public async Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
            string extensionId,
            string entityType,
            string filterId,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }

    private sealed class DelayedRuntime(TimeSpan delay) : IExtensionEntityFilterRuntime
    {
        public int CallCount { get; private set; }

        public Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
            string extensionId,
            string entityType,
            string filterId,
            CancellationToken ct)
        {
            return Task.FromResult<IExtensionEntityFilterExecution?>(new TestFilterExecution(
                new UIListFilterContribution("preview", "tags", "Animated preview", "boolean", "owner.actual", Modifiers: ["equals"])
                {
                    FilterId = "has-preview",
                },
                async (request, resolveCt) =>
                {
                    CallCount++;
                    await Task.Delay(delay, resolveCt);
                    return new ExtensionEntityFilterResult(request.CandidateIds, "stable-revision");
                }));
        }
    }

    private sealed class TestFilterExecution(
        UIListFilterContribution declaration,
        Func<ExtensionEntityFilterRequest, CancellationToken, Task<ExtensionEntityFilterResult>> resolve)
        : IExtensionEntityFilterExecution
    {
        public UIListFilterContribution Declaration { get; } = declaration;

        public Task<ExtensionEntityFilterResult> ResolveAsync(
            ExtensionEntityFilterRequest request,
            CancellationToken ct)
            => resolve(request, ct);

        public void Dispose()
        {
        }
    }

    private sealed class PagedTagRepository(IReadOnlyList<Tag> tags) : ITagRepository
    {
        public Task<(IReadOnlyList<Tag> Items, int TotalCount)> FindAsync(TagFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
        {
            var ordered = tags.OrderBy(tag => tag.Name).ThenBy(tag => tag.Id).ToArray();
            var page = Math.Max(1, findFilter?.Page ?? 1);
            var perPage = Math.Max(0, findFilter?.PerPage ?? 25);
            IReadOnlyList<Tag> items = perPage == 0
                ? []
                : ordered.Skip((page - 1) * perPage).Take(perPage).ToArray();
            return Task.FromResult((items, ordered.Length));
        }

        public Task<Tag?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(tags.FirstOrDefault(tag => tag.Id == id));
        public Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(tags);
        public Task<Tag> AddAsync(Tag entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Tag entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(tags.Count);
        public Task<Tag?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default) => GetByIdAsync(id, ct);
        public Task<Tag?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(tags.FirstOrDefault(tag => tag.Name == name));
        public Task<IReadOnlyList<Tag>> FindByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, Tag>> FindOrCreateByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MultiFilterRuntime : IExtensionEntityFilterRuntime
    {
        public List<ExtensionEntityFilterRequest> Requests { get; } = [];

        public Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
            string extensionId,
            string entityType,
            string filterId,
            CancellationToken ct)
        {
            IExtensionEntityFilterExecution? execution = extensionId == "owner.actual"
                && entityType == "tags"
                && filterId is "even" or "over-two"
                ? new TestFilterExecution(
                    new UIListFilterContribution(filterId, "tags", filterId, "boolean", extensionId, Modifiers: ["equals"])
                    {
                        FilterId = filterId,
                    },
                    (request, _) =>
                    {
                        Requests.Add(request);
                        var matches = request.FilterId switch
                        {
                            "even" => request.CandidateIds.Where(id => id % 2 == 0),
                            "over-two" => request.CandidateIds.Where(id => id > 2),
                            _ => [],
                        };
                        return Task.FromResult(new ExtensionEntityFilterResult(matches.ToArray(), "stable-revision"));
                    })
                : null;
            return Task.FromResult(execution);
        }
    }
}
