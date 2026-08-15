# Fluent API tests

These tests launch the real Cove application as a Kestrel process and exercise it over HTTP with real access tokens. Two parallel test lanes each own an isolated process, data root, PostgreSQL database, metadata-service simulator, download-source simulator, and temporary library folder. Tests within a lane are serialized; before each test, the fixture drains background work, resets public database state and caches, restores required built-in state, and creates the standard owner and member personas.

Put every behavior test class in `ApiTestLane1Collection` or `ApiTestLane2Collection`, derive it from `ApiTest`, and distribute classes roughly evenly between lanes. Use:

- `AsUser()` for the owner-authenticated `CoveClient`.
- `AsUser(ApiTestUsers.Eva)` or `AsUser(ApiTestUsers.Anthony)` for standard members.
- `AsMetadataService()` and `AsDownloadSource()` for deterministic external services.
- `AsTestFileSystem()` for real filesystem fixtures.

Tests live under `Tests/` and are grouped by behavior: `Catalog`, `Contracts`, `Downloads`, `Entities`, `Extensions`, `Files`, `Interactions`, and `Metadata`. Harness mechanics belong in `Tests/Harness`. Namespaces mirror the directory structure; reusable support belongs in `Assertions`, `Builders`, `ExampleData`, or `Infrastructure`.

`EndpointCoverageTests` inventories every attributed controller action as a normalized `VERB /route-template` identifier. A public action must be mapped by the active read-catalog theory or a non-skipped `[CoversEndpoint]` behavioral test, remain in the checked-in temporary backlog, or have a reviewed exception with a technical reason. When a test starts exercising a backlog endpoint, add the exact attribute, remove the matching backlog entry, and update both expected progress counts; do not use a controller-wide marker.

## Test conventions

Name tests with concise Given/When/Then clauses:

```text
GivenPrecondition_WhenAction_ThenOutcome
```

- Use PascalCase and underscores only between clauses.
- Include only state that materially changes the scenario.
- Keep `When` focused on one action or coherent state transition, and `Then` on an observable result.
- Prefer `GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag` over filler such as `GivenAnExistingPerformer...`.

Order tests with the main happy paths first, followed by sad paths and edge cases. Establish that the primary workflow works before covering invalid input, missing entities, conflicts, unusual limits, or other defensive scenarios. This keeps the class centered on the behavior users rely on and makes failures easier to interpret.

Keep each test easy to scan: arrange the required state, perform the action, and assert externally observable API behavior. Separate major phases with blank lines; add phase comments only when the structure is not obvious. A state-transition test may use one compact `Act & Assert` block when actions and assertions intentionally alternate.

- Create and read application entities through `CoveClient`; do not seed them through Entity Framework when the public API can express the scenario.
- Do not mock, replace, decorate, or configure application services in this project.
- Do not query an empty collection merely to prove fixture isolation. Assert emptiness only when it is the behavior under test.
- Await API work that intentionally continues in the background. Use an isolated host for extension scenarios that start workers outside Cove's job lifecycle.
- Add focused builders, client methods, and assertion extensions when they improve readability; keep raw HTTP mechanics out of individual tests.

```csharp
[Fact]
public async Task GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag()
{
    var performer = await AsUser().CreatePerformerAsync(
        new PerformerBuilder().WithName("Example Performer").Build());
    var tag = await AsUser().CreateTagAsync("Example Tag");

    await AsUser().LinkTagToPerformerAsync(tag, performer);

    var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
    performerAfter.ShouldHaveTag(tag);
}
```

## Running the tests

```sh
dotnet test src/Cove.ApiTests/Cove.ApiTests.csproj
```

The PostgreSQL account must be able to create and drop databases and install the `vector` extension. Set `COVE_API_TEST_PG_ADMIN_CONNECTION_STRING`, or configure `COVE_API_TEST_PG_HOST`, `COVE_API_TEST_PG_PORT`, `COVE_API_TEST_PG_USER`, `COVE_API_TEST_PG_PASSWORD`, and `COVE_API_TEST_PG_ADMIN_DB`. Host, port, and password fall back to `PGHOST`, `PGPORT`, and `PGPASSWORD`; other defaults are user `postgres`, database `postgres`, and no password.

For authenticated debugging, use the random base URI printed in test output together with `ApiUri`, `AsUser().AccessToken`, or `AsUser().CreateHttpClient()` while the test process is running.

To collect production-assembly coverage:

```sh
dotnet tool restore
dotnet tool run dotnet-coverage -- collect --settings src/Cove.ApiTests/coverage.config --output artifacts/coverage/api-tests.cobertura.xml --output-format cobertura dotnet test src/Cove.ApiTests/Cove.ApiTests.csproj -c Release --no-restore --verbosity normal
```

Run `dotnet restore src/Cove.slnx` first when dependencies have not been restored.
