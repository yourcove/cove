using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Tests;

/// <summary>
/// Saved filters are per-user: one user must never see, fetch, edit, or delete another user's filters.
/// These tests exercise the controller against an in-memory repository and a fixed principal.
/// </summary>
public class SavedFilterPerUserTests
{
    private static SavedFiltersController ControllerFor(int? userId, FakeSavedFilterRepo repo)
        => new(repo, new FakePrincipalAccessor(userId));

    private static SavedFilterCreateDto CreateDto(string name) => new("videos", name, null, null, null);

    [Fact]
    public async Task Create_stamps_the_current_user()
    {
        var repo = new FakeSavedFilterRepo();
        var result = await ControllerFor(7, repo).Create(CreateDto("mine"), TestContext.Current.CancellationToken);

        var created = Assert.IsType<SavedFilterDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(7, repo.Items.Single(f => f.Id == created.Id).UserId);
    }

    [Theory]
    [InlineData("segments", "segments")]
    [InlineData("rawsegments", "rawsegments")]
    [InlineData("groupitems", "groupitems")]
    [InlineData(" EXT:Com.Example.Tools:Missing-Videos ", "ext:com.example.tools:missing-videos")]
    public async Task Create_accepts_distinct_filter_modes(string mode, string expected)
    {
        var repo = new FakeSavedFilterRepo();

        var result = await ControllerFor(7, repo).Create(new SavedFilterCreateDto(mode, "segment filter", null, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SavedFilterDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(expected, repo.Items.Single().Mode);
    }

    [Theory]
    [InlineData("ext:missing-view")]
    [InlineData("ext:com.example:bad view")]
    [InlineData("ext:com.example:view:extra")]
    [InlineData("ext::view")]
    [InlineData("999")]
    [InlineData("0")]
    public async Task Create_rejects_invalid_extension_filter_modes(string mode)
    {
        var result = await ControllerFor(7, new FakeSavedFilterRepo()).Create(new SavedFilterCreateDto(mode, "filter", null, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_only_returns_the_current_users_filters()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto("a-one"), TestContext.Current.CancellationToken);
        await ControllerFor(2, repo).Create(CreateDto("b-one"), TestContext.Current.CancellationToken);
        await ControllerFor(1, repo).Create(CreateDto("a-two"), TestContext.Current.CancellationToken);

        var list = Assert.IsAssignableFrom<IReadOnlyList<SavedFilterDto>>(
            Assert.IsType<OkObjectResult>((await ControllerFor(1, repo).GetAll(null, TestContext.Current.CancellationToken)).Result).Value);

        Assert.Equal(new[] { "a-one", "a-two" }, list.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task GetAll_returns_filters_sorted_by_name()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto("Zulu"), TestContext.Current.CancellationToken);
        await ControllerFor(1, repo).Create(CreateDto("alpha"), TestContext.Current.CancellationToken);
        await ControllerFor(1, repo).Create(CreateDto("Bravo"), TestContext.Current.CancellationToken);

        var list = Assert.IsAssignableFrom<IReadOnlyList<SavedFilterDto>>(
            Assert.IsType<OkObjectResult>((await ControllerFor(1, repo).GetAll("videos", TestContext.Current.CancellationToken)).Result).Value);

        Assert.Equal(new[] { "alpha", "Bravo", "Zulu" }, list.Select(f => f.Name));
    }

    [Fact]
    public async Task GetAll_rejects_an_explicit_unknown_mode_instead_of_returning_every_filter()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto("private filter"), TestContext.Current.CancellationToken);

        var result = await ControllerFor(1, repo).GetAll("unknown-mode", TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_rejects_duplicate_name_for_the_same_user_and_mode()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto(" Favorites "), TestContext.Current.CancellationToken);

        var result = await ControllerFor(1, repo).Create(CreateDto("favorites"), TestContext.Current.CancellationToken);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Single(repo.Items);
        Assert.Equal("Favorites", repo.Items[0].Name);
    }

    [Fact]
    public async Task Create_allows_the_same_name_for_a_different_user_or_mode()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto("Favorites"), TestContext.Current.CancellationToken);

        var otherUser = await ControllerFor(2, repo).Create(CreateDto(" favorites "), TestContext.Current.CancellationToken);
        var otherMode = await ControllerFor(1, repo).Create(new SavedFilterCreateDto("images", " FAVORITES ", null, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(otherUser.Result);
        Assert.IsType<CreatedAtActionResult>(otherMode.Result);
        Assert.Equal(3, repo.Items.Count);
    }

    [Fact]
    public async Task Update_rejects_a_duplicate_name_but_allows_keeping_its_own_name()
    {
        var repo = new FakeSavedFilterRepo();
        var controller = ControllerFor(1, repo);
        var first = Assert.IsType<SavedFilterDto>(
            Assert.IsType<CreatedAtActionResult>((await controller.Create(CreateDto("First"), TestContext.Current.CancellationToken)).Result).Value);
        var second = Assert.IsType<SavedFilterDto>(
            Assert.IsType<CreatedAtActionResult>((await controller.Create(CreateDto("Second"), TestContext.Current.CancellationToken)).Result).Value);

        var unchanged = await controller.Update(first.Id, new SavedFilterUpdateDto(null, " first ", "{}", "{}", "{}"), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(unchanged.Result);

        var duplicate = await controller.Update(second.Id, new SavedFilterUpdateDto(null, "FIRST", "{}", "{}", "{}"), TestContext.Current.CancellationToken);
        Assert.IsType<ConflictObjectResult>(duplicate.Result);
        Assert.Equal("Second", repo.Items.Single(f => f.Id == second.Id).Name);
    }

    [Fact]
    public async Task Update_trims_the_retained_name_before_persistence()
    {
        var repo = new FakeSavedFilterRepo();
        repo.Items.Add(new SavedFilter
        {
            Id = 1,
            UserId = 1,
            Mode = "videos",
            Name = " Favorites ",
        });

        var result = await ControllerFor(1, repo).Update(1, new SavedFilterUpdateDto(null, null, "{}", "{}", "{}"), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Favorites", repo.Items.Single().Name);
    }

    [Fact]
    public async Task GetById_and_Delete_and_Update_reject_another_users_filter()
    {
        var repo = new FakeSavedFilterRepo();
        var created = Assert.IsType<SavedFilterDto>(
            Assert.IsType<CreatedAtActionResult>((await ControllerFor(1, repo).Create(CreateDto("owned"), TestContext.Current.CancellationToken)).Result).Value);

        var intruder = ControllerFor(2, repo);
        Assert.IsType<NotFoundResult>((await intruder.GetById(created.Id, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<NotFoundResult>(await intruder.Delete(created.Id, TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>((await intruder.Update(created.Id, new SavedFilterUpdateDto(null, "hijacked", null, null, null), TestContext.Current.CancellationToken)).Result);

        // The owner's filter is untouched.
        Assert.Single(repo.Items);
        Assert.Equal("owned", repo.Items[0].Name);
    }

    private sealed class FakePrincipalAccessor(int? userId) : ICurrentPrincipalAccessor
    {
        public CovePrincipal? Current { get; private set; } = new()
        {
            UserId = userId,
            Username = userId?.ToString() ?? "anon",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { "*" },
        };

        public void Set(CovePrincipal? principal) => Current = principal;
    }

    private sealed class FakeSavedFilterRepo : ISavedFilterRepository
    {
        public List<SavedFilter> Items { get; } = new();
        private int _nextId = 1;

        public Task<SavedFilter> AddAsync(SavedFilter entity, CancellationToken ct = default)
        {
            entity.Id = _nextId++;
            Items.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<SavedFilter?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(f => f.Id == id));

        public Task<IReadOnlyList<SavedFilter>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.ToList());

        public Task<IReadOnlyList<SavedFilter>> GetByModeAsync(string mode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.Where(f => f.Mode == mode).ToList());

        public Task<IReadOnlyList<SavedFilter>> GetAllForUserAsync(int? userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.Where(f => f.UserId == userId).ToList());

        public Task<IReadOnlyList<SavedFilter>> GetByModeForUserAsync(string mode, int? userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.Where(f => f.Mode == mode && f.UserId == userId).ToList());

        public Task UpdateAsync(SavedFilter entity, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(int id, CancellationToken ct = default)
        {
            Items.RemoveAll(f => f.Id == id);
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Items.Count);
    }
}
