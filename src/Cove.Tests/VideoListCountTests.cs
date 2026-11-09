using System.Data.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class VideoListCountTests
{
    // Counting reads every match. The video list takes its total from the aggregate request,
    // so a skipped count must not reach the database at all.
    [Fact]
    public async Task SkipCount_ReturnsThePageWithoutRunningACountQuery()
    {
        var commands = new CommandRecorder();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext(options => options.AddInterceptors(commands));
        db.Videos.AddRange(new Video { Title = "Needle one" }, new Video { Title = "Needle two" }, new Video { Title = "Other" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new VideoRepository(db);

        var counted = await repository.FindAsync(null, new FindFilter { Q = "needle", Sort = "title" }, TestContext.Current.CancellationToken);
        Assert.Contains(commands.Texts, text => text.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        commands.Clear();
        var uncounted = await repository.FindAsync(null, new FindFilter { Q = "needle", Sort = "title", SkipCount = true }, TestContext.Current.CancellationToken);

        Assert.Equal(2, counted.TotalCount);
        Assert.Equal(FindFilter.UncountedTotal, uncounted.TotalCount);
        Assert.Equal(counted.Items.Select(video => video.Id), uncounted.Items.Select(video => video.Id));
        Assert.DoesNotContain(commands.Texts, text => text.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> texts = [];
        public IReadOnlyList<string> Texts => texts;
        public void Clear() => texts.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            texts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            texts.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
