using Cove.Core.Entities;
using Cove.Core.Common;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

[RequiresPostgres]
public class FilePathFilterHelperTests
{
    [Fact]
    public async Task ApplyFilePath_UnderPath_WorksForAudioAndTextCollections()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        // Folder paths are unique, so the audio and the text under the prefix-only folder share it.
        var prefixOnly = CreateFolder(@"C:\library\matching-other");
        context.Audios.AddRange(
            CreateAudio("audio-match", CreateFolder(@"C:\library\matching\nested"), "track.mp3"),
            CreateAudio("audio-prefix", prefixOnly, "track.mp3"));
        context.TextDocuments.AddRange(
            CreateText("text-match", CreateFolder(@"C:\library\matching"), "document.txt"),
            CreateText("text-prefix", prefixOnly, "document.txt"));
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        context.Audios.AddRange(
            CreateAudio("exact-case", CreateFolder("/library/Media"), "track.mp3"),
            CreateAudio("different-case", CreateFolder("/library/media"), "track.mp3"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var criterion = new StringCriterion { Value = "/library/Media", Modifier = CriterionModifier.UnderPath };
        var titles = await FilterHelpers.ApplyFilePath(context.Audios, criterion, audio => audio.Files)
            .Select(audio => audio.Title)
            .OrderBy(title => title)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase ? ["different-case", "exact-case"] : ["exact-case"], titles);
    }

    private static Folder CreateFolder(string path) => new() { Path = path, ModTime = DateTime.UtcNow };

    private static Audio CreateAudio(string title, Folder folder, string basename)
    {
        var audio = new Audio { Title = title };
        audio.Files.Add(new AudioFile
        {
            Basename = basename,
            ParentFolder = folder,
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });
        return audio;
    }

    private static TextDocument CreateText(string title, Folder folder, string basename)
    {
        var text = new TextDocument { Title = title };
        text.Files.Add(new TextFile
        {
            Basename = basename,
            ParentFolder = folder,
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });
        return text;
    }
}
