# Fluent API tests

These tests launch the real Cove application as a Kestrel process and exercise it over HTTP with real access tokens. An assembly fixture starts a bounded pool of isolated servers, with `CoveApiTestPool.MaxParallelThreads` providing the single shared value for both the server count and xUnit's conservative parallel-test limit. Every server has a distinct process, data root, PostgreSQL database, metadata-service simulator, download-source simulator, extension-registry simulator, and temporary library folder. Every `ApiTest` class holds one exclusive server lease for its lifetime, while methods within each class remain serial. Before each test, the leased server drains background work, resets public database state and caches, restores required built-in state, and creates the standard owner and member personas. Database restore and wipe tests explicitly mark their server for retirement before the destructive request, so the next class receives a fresh process, database, and data root.

Derive ordinary behavior test classes from `ApiTest` and accept their `CoveApiTestFixture` through the constructor. Do not add explicit collections for database restore, wipe, configuration, extension, or filesystem scenarios; their class-scoped lease already isolates those resources. Tests that self-host `CoveApiServer` directly belong to `SelfHostedApiTestCollection`, which remains explicitly non-parallel. Use:

- `AsUser()` for the owner-authenticated `CoveClient`.
- `AsUser(ApiTestUsers.Eva)` or `AsUser(ApiTestUsers.Anthony)` for standard members.
- `AsMetadataService()` and `AsDownloadSource()` for deterministic external services.
- `AsTestFileSystem()` for real filesystem fixtures.
- `AsDbUser()` only for lifecycle evidence that the public API cannot create or observe.

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

`CoveClient` is split into partial files by API ownership so parallel coverage slices can extend separate files with minimal conflicts. Keep shared HTTP transport in `CoveClient.cs`; put user, read-catalog, downloader, and job methods in `CoveClient.System.cs`; entity methods in the matching performer, studio, media, or video file; group, detection, and segment methods in `CoveClient.Relationships.cs`; engagement, bookmark, like-mutation, and playback methods in `CoveClient.Interactions.cs`; tag and tag-group methods in `CoveClient.Tags.cs`; and file, migration, custom-field, scraping, streaming, and external-metadata methods in `CoveClient.FilesAndMetadata.cs`.

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
node scripts/check-api-controller-coverage.mjs artifacts/coverage/api-tests.cobertura.xml
```

To collect coverage from every .NET test assembly in one instrumentation session and generate a Coverage Gutters-compatible report:

```sh
source gitignored/dev/agent.env
scripts/collect-backend-coverage
```

The command runs the exact Release solution build followed by `Cove.ApiTests`, `Cove.Tests`, and `Cove.PerformanceTests` sequentially from those compiled outputs. Performance tests use explicit `COVE_PERF_PG_*` settings when present and otherwise use the generated devbox PostgreSQL sidecar. It writes the authoritative report to `artifacts/coverage/all-dotnet-tests-single-session.cobertura.xml`, derives `artifacts/coverage/all-dotnet-tests-single-session.coverage-gutters.xml` without modifying the raw report, and prints the controller coverage analysis. The single collection session preserves subprocess and branch coverage that can be lost when separately generated Cobertura reports are merged.

The VS Code workspace recommends Coverage Gutters and watches the compatible report automatically after the extension is installed. After collecting fresh coverage, use **Coverage Gutters: Display Coverage** if the gutters are not already visible.

The coverage check deduplicates compiler-generated async and aggregate records by normalized controller source filename and line. It prints aggregate and per-controller line and branch diagnostics, highlights the largest uncovered controller files, enforces the checked-in API-test line-coverage baseline, and reports progress toward the 90% controller line target. When intentionally raising the ratchet after adding tests, update `controller-coverage-baseline.json` with the newly measured covered and total line counts; never lower it to accommodate a regression.

Run the coverage tools' synthetic Cobertura tests with:

```sh
node --test scripts/check-api-controller-coverage.test.mjs scripts/prepare-coverage-gutters.test.mjs
```

Run `dotnet restore src/Cove.slnx` first when dependencies have not been restored.
