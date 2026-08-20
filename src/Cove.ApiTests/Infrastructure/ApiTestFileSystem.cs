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
}
