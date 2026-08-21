using System.Buffers.Binary;
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
