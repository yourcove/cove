# In-process tests

These tests call Cove's services, controllers and data layer directly. Prefer the fluent API tests in [`src/Cove.ApiTests`](../Cove.ApiTests/README.md) for behavior the public API can show; use this project when a test needs internal code, fakes, interceptors or injected failures.

## Databases

Production runs only on PostgreSQL. A test that touches `CoveContext` should use `CovePostgresDatabase`, which gives it a database with the schema production's migrations build:

```csharp
[RequiresPostgres]
public sealed class ExampleTests
{
    [Fact]
    public async Task SavedTag_CanBeReadBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = database.CreateContext();

        db.Tags.Add(new Tag { Name = "Example" });
        await db.SaveChangesAsync(ct);

        Assert.Equal(1, await db.Tags.CountAsync(ct));
    }
}
```

The run migrates one template database the first time a test asks for one, and every `CovePostgresDatabase` is a copy of it. The template is created with UTF8 encoding and the C locale, as managed PostgreSQL initializes its cluster, whatever the server's default locale is, because `upper()`, `lower()` and text ordering follow the database locale. Disposing a copy queues it to be dropped in the background, because every `DROP DATABASE` waits for a checkpoint; a few queued drops run at once so they mostly wait on the same checkpoints, and the run fails at the end if any could not be dropped. Create one per test when tests must not see each other's rows, or share one across a class with `IClassFixture<CovePostgresDatabase>`. Sharing a seed is safe for cases that only read it when they connect with `default_transaction_read_only`, so the server refuses any write; `StablePaginationTests` seeds each of its datasets once that way. Cases that change data can instead seed and run inside a transaction that is rolled back on one shared database, as `EntityListSortBehaviorHarnessTests` does, but only for list queries and plain saves: code that opens its own transaction or connection, or whose effect depends on a commit (outbox rows, post-commit cleanup, locks held between commits), is not tested that way. Each save there must still check the deferred unique-name constraints a commit would check, and the fixture fails a test that commits or ends its transaction. `CreateContext` and `CreateOptions` take a callback that receives the configured options builder, for adding interceptors or warnings; to enable retry-on-failure, call `UseNpgsql(database.DataSource, npgsql => npgsql.EnableRetryOnFailure())` in it, which keeps the earlier provider settings.

Rows saved with explicit ids do not advance PostgreSQL's identity sequences, so the next row saved without an id collides with them. After seeding with explicit ids, call `database.SyncIdentitySequencesAsync()`. When the seed is in a transaction that has not committed, call `CovePostgresDatabase.SyncIdentitySequencesAsync(context)` instead, which runs on that context's connection; sequences do not roll back, so the move outlives the transaction. Two other differences have broken tests moved to PostgreSQL:
- **NULL ordering:** PostgreSQL sorts NULLs last when ascending and first when descending, while LINQ to Objects treats null as the smallest value. When a sort test fails on a missing value, decide where missing values belong and follow the existing conventions: missing dates count as oldest (`?? DateOnly.MinValue`), missing activity timestamps go last in both directions (`?? (desc ? DateTime.MinValue : DateTime.MaxValue)`), and missing strings count as empty (`?? string.Empty`). Do not coalesce a key an index serves unless a matching expression index exists, and make the test's expected order spell out where missing values go.
- **Timestamp precision:** PostgreSQL stores microseconds, not .NET's 100 ns ticks.

Do not test `CoveContext` on SQLite or the EF Core in-memory provider; the project no longer references either. Neither builds the model production uses: authorization filters, search vectors, collations, constraints and row locks only exist on PostgreSQL, and the in-memory provider gives an entity its id on `Add` rather than on insert, which hid bugs in code that looked new rows up by id. `StashMigrationMetadataTests` still uses `Microsoft.Data.Sqlite`, but only to build the Stash databases a Stash import reads.

## Running the tests

```sh
dotnet test src/Cove.Tests/Cove.Tests.csproj
```

PostgreSQL-backed tests need a server where the test account can create databases and the `vector` extension is available. They read the same settings as the API tests: `COVE_API_TEST_PG_ADMIN_CONNECTION_STRING`, or `COVE_API_TEST_PG_HOST`, `COVE_API_TEST_PG_PORT`, `COVE_API_TEST_PG_USER`, `COVE_API_TEST_PG_PASSWORD` and `COVE_API_TEST_PG_ADMIN_DB`, falling back to `PGHOST`, `PGPORT` and `PGPASSWORD`. Without a server they fail rather than skip. To run only the tests that need no server, add `--filter "Requires!=PostgreSQL"`.
