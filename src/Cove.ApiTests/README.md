# Fluent API tests

These tests run the real Cove application on Kestrel and bind it to an operating-system-assigned loopback port. Each test gets a newly created PostgreSQL database, applies the production migrations and startup initialization, creates an owner through the public bootstrap endpoint, and sends requests over HTTP with a real access token. The harness does not replace application services or use `ConfigureTestServices`.

Derive a test from `ApiTest`. `InitializeAsync` owns the server and database lifecycle, `AsUser()` exposes the fluent authenticated API, and builders keep arrange code focused on values that matter to the test.

## Test conventions

Name tests with concise Given/When/Then clauses:

```text
GivenPrecondition_WhenAction_ThenOutcome
```

- Write each clause in PascalCase and use underscores only to separate the clauses.
- Keep the Given clause to state that materially affects the behavior. Omit filler such as `AnExisting`, `TheUser`, and setup details that do not change the scenario.
- Keep the When clause to one action.
- Describe an observable result in the Then clause, not an implementation detail.
- Prefer `GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag` over sentence-style names or longer forms such as `GivenAnExistingPerformerAndAnExistingTag_WhenTheUserLinksTheTag_ThenThePerformerShouldContainTheTag`.

Keep the test body visibly arranged as Given, When, and Then, separated by blank lines. Add comments only when the phases are not already obvious from the code.

- Derive API tests from `ApiTest` so each test receives its own clean PostgreSQL database and real Kestrel application host.
- Exercise behavior through the fluent `AsUser()` API. Do not seed application entities directly through Entity Framework when the public API can create the required state.
- Do not mock, replace, or decorate application services. Do not use `ConfigureTestServices` in this project.
- Assert externally observable API behavior. Direct database queries are reserved for test-host lifecycle checks that cannot be observed through the API.
- Add focused builders, fluent operations, and assertion extensions when they make the scenario read more clearly; do not expose raw HTTP mechanics in individual tests.

```csharp
public sealed class PerformerTagApiTests(ITestOutputHelper output) : ApiTest(output)
{
    [Fact]
    public async Task GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag()
    {
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName("Example Performer").Build());
        var tag = await AsUser().CreateTagAsync(
            new TagBuilder().WithName("Example Tag").Build());

        await AsUser().LinkTagToPerformerAsync(tag, performer);

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }
}
```

Run the suite with:

```sh
dotnet test src/Cove.ApiTests/Cove.ApiTests.csproj
```

The PostgreSQL account must be able to create and drop databases and install the `vector` extension. Set `COVE_API_TEST_PG_ADMIN_CONNECTION_STRING` to an administrative database connection string, or configure `COVE_API_TEST_PG_HOST`, `COVE_API_TEST_PG_PORT`, `COVE_API_TEST_PG_USER`, `COVE_API_TEST_PG_PASSWORD`, and `COVE_API_TEST_PG_ADMIN_DB`. Host, port, and password fall back to `PGHOST`, `PGPORT`, and `PGPASSWORD`; other defaults are user `postgres`, database `postgres`, and no password.

The test output includes the random base URI. Because this is a real listener, pause at a breakpoint while the test is running and call its health endpoint with `curl`. `ApiUri`, `AsUser().AccessToken`, and `AsUser().CreateHttpClient()` are available to inspect or use for authenticated debugging.
