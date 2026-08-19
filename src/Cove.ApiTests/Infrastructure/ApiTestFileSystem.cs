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

    internal ApiTestFileSystem(string libraryPath)
    {
        LibraryPath = libraryPath;
    }

    public string LibraryPath { get; }

    internal void Reset()
    {
        foreach (var file in Directory.EnumerateFiles(LibraryPath))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(LibraryPath))
            Directory.Delete(directory, recursive: true);
    }

    public string CreateTextFile(string contents)
    {
        var path = Path.Combine(LibraryPath, $"api-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
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
}
