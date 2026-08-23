using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Cove.ApiTests.Infrastructure;

public sealed class ApiTestFileSystem
{
    private static readonly string[] EmptyStashTables =
    [
        "scenes",
        "performers",
        "tags",
        "studios",
        "groups",
        "images",
        "galleries",
    ];

    internal ApiTestFileSystem(string libraryPath, string generatedPath)
    {
        LibraryPath = libraryPath;
        GeneratedPath = generatedPath;
    }

    public string LibraryPath { get; }

    public string GeneratedPath { get; }

    internal void Reset()
    {
        ResetDirectory(LibraryPath);

        // Generated artifacts are mutable test state too. Clear them between cases so a live-id
        // artifact from one database generation cannot look orphaned after the next database reset.
        ResetDirectory(GeneratedPath);
    }

    public string CreateTextFile(string contents)
    {
        var path = Path.Combine(LibraryPath, $"api-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        return path;
    }

    public string CreateLibraryFile(string fileName, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(contents);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(fileName), "API test media files must use a leaf filename under the library root.");

        var path = Path.Combine(LibraryPath, fileName);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public async Task<string> CreateSyntheticVideoAsync(
        string ffmpegPath,
        string fileName,
        int width,
        int height,
        double durationSeconds,
        string color,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(fileName), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(fileName), "Synthetic videos must use a leaf .mp4 filename under the library root.");
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Synthetic video dimensions must be positive even numbers.");
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Synthetic video duration must be finite and positive.");
        if (color.Any(character => !char.IsAsciiLetter(character)))
            throw new ArgumentOutOfRangeException(nameof(color), "Synthetic video colors must use a named ASCII color.");

        if (!File.Exists(ffmpegPath))
            throw new FileNotFoundException("The Cove host's resolved FFmpeg executable was not found.", ffmpegPath);
        var path = Path.Combine(LibraryPath, fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsLinux())
        {
            var ffmpegDirectory = Path.GetDirectoryName(ffmpegPath)!;
            startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var inheritedLibraryPath);
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(inheritedLibraryPath)
                ? ffmpegDirectory
                : $"{ffmpegDirectory}{Path.PathSeparator}{inheritedLibraryPath}";
        }
        foreach (var argument in new[]
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "lavfi",
            "-i", $"color=c={color}:s={width}x{height}:r=10:d={durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-threads", "1",
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-an",
            path,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg could not be started for the synthetic API-test video fixture.");
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("FFmpeg did not create the synthetic API-test video within 20 seconds.");
        }

        var error = await standardError;
        if (process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg failed to create the synthetic API-test video (exit {process.ExitCode}): {error}");
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    public string CreateLibraryDirectory(string relativePath)
    {
        var path = ResolveLibraryChildPath(relativePath, nameof(relativePath));
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateLibraryNestedFile(string relativePath, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var path = ResolveLibraryChildPath(relativePath, nameof(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public string CreateGalleryArchive(string fileName, string imageFileName, byte[] imageContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFileName);
        ArgumentNullException.ThrowIfNull(imageContents);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(fileName), "Gallery test archives must use a leaf .zip filename under the library root.");
        if (!string.Equals(Path.GetFileName(imageFileName), imageFileName, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(imageFileName), "Gallery archive entries must use a leaf filename.");

        var path = Path.Combine(LibraryPath, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(imageFileName, CompressionLevel.NoCompression);
        entry.LastWriteTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var entryStream = entry.Open();
        entryStream.Write(imageContents);
        return path;
    }

    public void DeleteLibraryFile(string path)
        => File.Delete(ResolveLibraryExistingPath(path, nameof(path)));

    public bool LibraryFileExists(string path)
        => File.Exists(ResolveLibraryExistingPath(path, nameof(path)));

    public void ReplaceLibraryFile(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        var fullPath = Path.GetFullPath(path);
        var libraryRoot = Path.GetFullPath(LibraryPath) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(libraryRoot, StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(LibraryPath), StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(path), "API test media files must stay directly under the library root.");

        File.WriteAllBytes(fullPath, contents);
    }

    public string CreateVideoScreenshot(int videoId, double seconds, byte[] contents)
        => CreateGeneratedFile(
            Path.Combine("screenshots", GetVideoBucket(videoId), $"{videoId}_t{(int)seconds}.jpg"),
            contents);

    public string CreateVideoSegmentPreview(int videoId, double seconds, byte[] contents)
        => CreateGeneratedFile(
            Path.Combine("segment-previews", GetVideoBucket(videoId), $"{videoId}_t{(int)seconds}.webp"),
            contents);

    public string CreateVideoPreview(int videoId, byte[] contents)
        => CreateGeneratedFile(
            Path.Combine("previews", GetVideoBucket(videoId), $"{videoId}.mp4"),
            contents);

    public string CreateVideoSprite(int videoId, byte[] contents)
        => CreateGeneratedFile(
            Path.Combine("vtt", GetVideoBucket(videoId), $"{videoId}_sprite.jpg"),
            contents);

    public string CreateVideoSpriteVtt(int videoId, string contents)
        => CreateGeneratedFile(
            Path.Combine("vtt", GetVideoBucket(videoId), $"{videoId}_thumbs.vtt"),
            System.Text.Encoding.UTF8.GetBytes(contents));

    public string CreatePcmWaveFile(string fileName, int sampleFrames, int sampleRate = 8_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(fileName), "PCM wave test files must use a leaf filename under the library root.");
        if (!string.Equals(Path.GetExtension(fileName), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(fileName), "PCM wave test files must use the .wav extension.");

        var path = Path.Combine(LibraryPath, fileName);
        WritePcmWaveFile(path, sampleFrames, sampleRate);
        return path;
    }

    public void ReplacePcmWaveFile(string path, int sampleFrames, int sampleRate = 8_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(LibraryPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(path), "PCM wave test files must stay under the library root.");

        WritePcmWaveFile(path, sampleFrames, sampleRate);
    }

    public string CreateGeneratedFile(string relativePath, byte[] contents)
    {
        var path = Path.GetFullPath(Path.Combine(GeneratedPath, relativePath));
        var relative = Path.GetRelativePath(GeneratedPath, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(relativePath), "Generated test files must stay under the generated root.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public async Task<string> CreateEmptyStashDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(LibraryPath, $"stash-{Guid.NewGuid():N}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var table in EmptyStashTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{table}\" (\"id\" INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return path;
    }

    private static void ResetDirectory(string path)
    {
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(path))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(path))
            Directory.Delete(directory, recursive: true);
    }

    private static string GetVideoBucket(int videoId)
        => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)))[..2];

    private string ResolveLibraryChildPath(string relativePath, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, parameterName);
        if (Path.IsPathFullyQualified(relativePath))
            throw new ArgumentOutOfRangeException(parameterName, "API test library paths must be relative to the disposable library root.");

        return ResolveLibraryPath(Path.Combine(LibraryPath, relativePath), parameterName);
    }

    private string ResolveLibraryExistingPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return ResolveLibraryPath(path, parameterName);
    }

    private string ResolveLibraryPath(string path, string parameterName)
    {
        var root = Path.GetFullPath(LibraryPath);
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison))
            throw new ArgumentOutOfRangeException(parameterName, "API test filesystem paths must stay under the disposable library root.");

        return fullPath;
    }

    private static void WritePcmWaveFile(string path, int sampleFrames, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        const short channels = 1;
        const short bitsPerSample = 16;
        const int headerLength = 44;
        var dataLength = checked(sampleFrames * channels * (bitsPerSample / 8));
        var bytes = new byte[checked(headerLength + dataLength)];
        var header = bytes.AsSpan();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(header.Slice(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), sampleRate);
        var blockAlign = checked((short)(channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28), checked(sampleRate * blockAlign));
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34), bitsPerSample);
        "data"u8.CopyTo(header.Slice(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40), dataLength);

        File.WriteAllBytes(path, bytes);
    }
}
