using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;

namespace Cove.Tests;

/// <summary>
/// The video path regex filters run over a search text that holds every file path on its own line, so ^ and $
/// have to match at the start and end of each path and a pattern matches a video when it matches any of its paths.
/// </summary>
[RequiresPostgres]
public class VideoPathRegexFilterBehaviorTests
{
    [Theory]
    [InlineData(CriterionModifier.MatchesRegex, @"^/media/.*[.]mp4$", "media-mp4|mixed")]
    [InlineData(CriterionModifier.MatchesRegex, @"[.]mp4$", "media-mp4|mixed")]
    [InlineData(CriterionModifier.MatchesRegex, @"[.]MP4$", "media-mp4|mixed")]
    [InlineData(CriterionModifier.MatchesRegex, @"^(?!/media/)", "mixed|other-mkv")]
    [InlineData(CriterionModifier.MatchesRegex, @"^(?!.*[.]mp4$)", "mixed|other-mkv")]
    [InlineData(CriterionModifier.MatchesRegex, @"^$", "")]
    [InlineData(CriterionModifier.NotMatchesRegex, @"^/media/", "no-file|other-mkv")]
    [InlineData(CriterionModifier.NotMatchesRegex, @"^(?!/media/)", "media-mp4|no-file")]
    [InlineData(CriterionModifier.NotMatchesRegex, @"^$", "media-mp4|mixed|no-file|other-mkv")]
    public async Task PathRegexCriterion_AnchorsMatchEachFilePath(CriterionModifier modifier, string pattern, string expectedTitles)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        context.Videos.AddRange(
            CreateVideo("media-mp4", ("/media/one", "a.mp4")),
            CreateVideo("other-mkv", ("/other/two", "b.mkv")),
            CreateVideo("mixed", ("/other/three", "c.mkv"), ("/media/four", "d.mp4")),
            new Video { Title = "no-file" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            new VideoFilter { PathCriterion = new StringCriterion { Value = pattern, Modifier = modifier } },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        string[] expected = expectedTitles.Length == 0 ? [] : expectedTitles.Split('|');
        Assert.Equal(expected, items.Select(video => video.Title ?? string.Empty).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, totalCount);
    }

    private static Video CreateVideo(string title, params (string FolderPath, string Basename)[] files)
    {
        var video = new Video { Title = title };
        foreach (var (folderPath, basename) in files)
        {
            video.Files.Add(new VideoFile
            {
                Basename = basename,
                ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
                Size = 1024,
                ModTime = DateTime.UtcNow,
            });
        }

        return video;
    }
}
