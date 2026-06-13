using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Enums;
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
        var result = await ControllerFor(7, repo).Create(CreateDto("mine"), default);

        var created = Assert.IsType<SavedFilterDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(7, repo.Items.Single(f => f.Id == created.Id).UserId);
    }

    [Fact]
    public async Task GetAll_only_returns_the_current_users_filters()
    {
        var repo = new FakeSavedFilterRepo();
        await ControllerFor(1, repo).Create(CreateDto("a-one"), default);
        await ControllerFor(2, repo).Create(CreateDto("b-one"), default);
        await ControllerFor(1, repo).Create(CreateDto("a-two"), default);

        var list = Assert.IsAssignableFrom<IReadOnlyList<SavedFilterDto>>(
            Assert.IsType<OkObjectResult>((await ControllerFor(1, repo).GetAll(null, default)).Result).Value);

        Assert.Equal(new[] { "a-one", "a-two" }, list.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task GetById_and_Delete_and_Update_reject_another_users_filter()
    {
        var repo = new FakeSavedFilterRepo();
        var created = Assert.IsType<SavedFilterDto>(
            Assert.IsType<CreatedAtActionResult>((await ControllerFor(1, repo).Create(CreateDto("owned"), default)).Result).Value);

        var intruder = ControllerFor(2, repo);
        Assert.IsType<NotFoundResult>((await intruder.GetById(created.Id, default)).Result);
        Assert.IsType<NotFoundResult>(await intruder.Delete(created.Id, default));
        Assert.IsType<NotFoundResult>((await intruder.Update(created.Id, new SavedFilterUpdateDto(null, "hijacked", null, null, null), default)).Result);

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

        public Task<IReadOnlyList<SavedFilter>> GetByModeAsync(FilterMode mode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.Where(f => f.Mode == mode).ToList());

        public Task<IReadOnlyList<SavedFilter>> GetAllForUserAsync(int? userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedFilter>>(Items.Where(f => f.UserId == userId).ToList());

        public Task<IReadOnlyList<SavedFilter>> GetByModeForUserAsync(FilterMode mode, int? userId, CancellationToken ct = default)
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
