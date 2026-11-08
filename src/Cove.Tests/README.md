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

The run migrates one template database the first time a test asks for one, and every `CovePostgresDatabase` is a copy of it. Create one per test when tests must not see each other's rows, or share one across a class with `IClassFixture<CovePostgresDatabase>`. `CreateContext` and `CreateOptions` take a callback that receives the configured options builder, for adding interceptors or warnings; to enable retry-on-failure, call `UseNpgsql(database.DataSource, npgsql => npgsql.EnableRetryOnFailure())` in it, which keeps the earlier provider settings.

Rows saved with explicit ids do not advance PostgreSQL's identity sequences, so the next row saved without an id collides with them. After seeding with explicit ids, call `database.SyncIdentitySequencesAsync()`. Two other differences have broken tests moved to PostgreSQL:
- **NULL ordering:** PostgreSQL sorts NULLs last when ascending and first when descending, while LINQ to Objects treats null as the smallest value. When a sort test fails on a missing value, decide where missing values belong and follow the existing conventions: missing dates count as oldest (`?? DateOnly.MinValue`), missing activity timestamps go last in both directions (`?? (desc ? DateTime.MinValue : DateTime.MaxValue)`), and missing strings count as empty (`?? string.Empty`). Do not coalesce a key an index serves unless a matching expression index exists, and make the test's expected order spell out where missing values go.
- **Timestamp precision:** PostgreSQL stores microseconds, not .NET's 100 ns ticks.

Some older tests still run on SQLite or the EF Core in-memory provider. Neither builds the model production uses: authorization filters, search vectors, collations, constraints and row locks only exist on PostgreSQL. Do not add new tests on them; move a test to `CovePostgresDatabase` when you change it.

## Running the tests

```sh
dotnet test src/Cove.Tests/Cove.Tests.csproj
```

PostgreSQL-backed tests need a server where the test account can create databases and the `vector` extension is available. They read the same settings as the API tests: `COVE_API_TEST_PG_ADMIN_CONNECTION_STRING`, or `COVE_API_TEST_PG_HOST`, `COVE_API_TEST_PG_PORT`, `COVE_API_TEST_PG_USER`, `COVE_API_TEST_PG_PASSWORD` and `COVE_API_TEST_PG_ADMIN_DB`, falling back to `PGHOST`, `PGPORT` and `PGPASSWORD`. Without a server they fail rather than skip. To run only the tests that need no server, add `--filter "Requires!=PostgreSQL"`.
