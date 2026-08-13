# Fluent API tests

These tests launch the real Cove application as an operating-system process running Kestrel on an operating-system-assigned loopback port. Two parallel test lanes each own a process, isolated environment and data root, and dedicated PostgreSQL database. Tests within a lane are serialized and reuse its process: before every test, a test-environment-only lifecycle endpoint drains queued audit writes and jobs, resets every public table with PostgreSQL `TRUNCATE ... RESTART IDENTITY CASCADE`, clears database-derived host caches, reruns Cove's real auth and built-in-group initialization, and then the harness creates a fresh owner through the public bootstrap endpoint. Tests send requests over HTTP with a real access token; the application is not hosted in the test process and the harness does not replace application services.

Put each test class in `ApiTestLane1Collection` or `ApiTestLane2Collection` and derive it from `ApiTest`. Each lane fixture owns its server and database, while `ApiTest.InitializeAsync` resets application state before every test. Distribute classes roughly evenly between the lanes; classes in different lanes run concurrently. `AsUser()` exposes the fluent authenticated API, and builders keep arrange code focused on values that matter to the test.

`ApiTestLaneHarnessTests` independently starts two isolated hosts and verifies that their process startup intervals overlap. It is kept outside the behavior lanes so focused execution of either behavior class does not depend on unrelated test discovery. Each lane also owns a loopback metadata-service simulator; use `AsMetadataService().CreateScene(new MetadataServiceSceneBuilder()...Build())` to arrange remote records that Cove can import through its real configured metadata-server client. `AsTestFileSystem()` exposes the lane's configured temporary library folder and can create filesystem fixtures, including an empty Stash database, for endpoints that require real files.

`EndpointCoverageTests` discovers Cove's controller groups and requires every one to be represented by either the read-endpoint catalog or a non-skipped `[CoversEndpoints]` happy-path test. This is a controller-group completeness guard rather than a claim that every action has full behavioral coverage: destructive maintenance actions, external-provider workflows, and media-processing variants still need focused scenarios when their behavior changes.

The reset contract is the test precondition, so tests should not query a collection only to assert that isolation worked. Assert an empty result only when emptiness is the endpoint behavior under test. A clean test database may still contain deterministic baseline state: the reset endpoint recreates Cove's required built-in state, and the harness then bootstraps the owner and member personas. If tests later need shared domain fixtures, add them through an explicit baseline seeder after owner bootstrap and expose their returned DTOs or IDs to tests; prefer a separate seeded collection so the default lanes retain an empty domain baseline.

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

- Put API test classes in one of the two lane collections and derive them from `ApiTest` so every test starts from clean PostgreSQL state while expensive hosts are shared and independent classes can run concurrently.
- Exercise behavior through the fluent `AsUser()` API. Do not seed application entities directly through Entity Framework when the public API can create the required state.
- Do not mock, replace, or decorate application services. Do not use `ConfigureTestServices` in this project.
- Assert externally observable API behavior. Direct database queries are reserved for test-host lifecycle checks that cannot be observed through the API.
- Await API work that intentionally continues in the background. The reset cancels and drains active Cove jobs before truncation; extension lifecycle scenarios that start independent workers should use an isolated host instead of this shared collection.
- Add focused builders, fluent operations, and assertion extensions when they make the scenario read more clearly; do not expose raw HTTP mechanics in individual tests.

```csharp
[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerTagApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
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
