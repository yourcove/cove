using Cove.Core.Entities;
using Cove.Core.Common;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class FilePathFilterHelperTests
{
    [Fact]
    public async Task ApplyFilePath_UnderPath_WorksForAudioAndTextCollections()
    {
        await using var context = CreateContext();
        context.Audios.AddRange(
            CreateAudio("audio-match", @"C:\library\matching\nested", "track.mp3"),
            CreateAudio("audio-prefix", @"C:\library\matching-other", "track.mp3"));
        context.TextDocuments.AddRange(
            CreateText("text-match", @"C:\library\matching", "document.txt"),
            CreateText("text-prefix", @"C:\library\matching-other", "document.txt"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var criterion = new StringCriterion { Value = @"C:\library\matching\", Modifier = CriterionModifier.UnderPath };
        var audios = await FilterHelpers.ApplyFilePath(context.Audios, criterion, audio => audio.Files).Select(audio => audio.Title).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        var texts = await FilterHelpers.ApplyFilePath(context.TextDocuments, criterion, text => text.Files).Select(text => text.Title).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["audio-match"], audios);
        Assert.Equal(["text-match"], texts);
    }

    [Fact]
    public async Task ApplyFilePath_UnderPath_UsesPlatformPathCaseSemantics()
    {
        await using var context = CreateContext();
        context.Audios.AddRange(
            CreateAudio("exact-case", "/library/Media", "track.mp3"),
            CreateAudio("different-case", "/library/media", "track.mp3"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var criterion = new StringCriterion { Value = "/library/Media", Modifier = CriterionModifier.UnderPath };
        var titles = await FilterHelpers.ApplyFilePath(context.Audios, criterion, audio => audio.Files)
            .Select(audio => audio.Title)
            .OrderBy(title => title)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase ? ["different-case", "exact-case"] : ["exact-case"], titles);
    }

    private static Audio CreateAudio(string title, string folderPath, string basename)
    {
        var audio = new Audio { Title = title };
        audio.Files.Add(new AudioFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });
        return audio;
    }

    private static TextDocument CreateText(string title, string folderPath, string basename)
    {
        var text = new TextDocument { Title = title };
        text.Files.Add(new TextFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });
        return text;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"file-path-helper-{Guid.NewGuid():N}")
            .Options;
        return new CoveContext(options);
    }
}
