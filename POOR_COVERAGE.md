# Poor Coverage Areas

## Scope and current measurements

The latest full coverage artifact is [`artifacts/coverage/api-tests-411.cobertura.xml`](artifacts/coverage/api-tests-411.cobertura.xml), collected on 2026-08-16 from `Cove.ApiTests`. It instruments the production assemblies `Cove`, `Cove.Core`, `Cove.Data`, `Cove.Plugins`, and `Cove.Sdk`.

The checked-in coverage ratchet currently evaluates only unique source lines under `src/Cove.Api/Controllers/**/*.cs`. It does not enforce whole-application coverage.

| Metric | Coverage | Covered / total |
|---|---:|---:|
| Controller unique source lines | 72.588% | 10,216 / 14,074 |
| Controller branches | 50.424% | 5,351 / 10,612 |
| Whole-application lines reported by Cobertura | 62.558% | 68,751 / 109,900 |
| Whole-application unique source lines | 62.911% | 68,057 / 108,179 |
| Whole-application branches reported by Cobertura | 35.764% | 15,670 / 43,815 |

The whole-application result above reflects only code reached by `Cove.ApiTests`. Coverage from `Cove.Tests` and other test projects is not merged into this artifact, so it is not yet a complete measure of the repository's test coverage.

## Assembly-level gaps

Line coverage below uses deduplicated source-file and line-number pairs. Branch coverage is the package value reported by Cobertura.

| Assembly | Unique line coverage | Uncovered unique lines | Branch coverage | Why controller/API tests are insufficient |
|---|---:|---:|---:|---|
| `Cove.Sdk` | 6.609% | 325 | 12.500% | API tests generally do not execute SDK clients. SDK request construction, serialization, response handling, and compatibility require SDK-focused tests. |
| `Cove.Plugins` | 39.633% | 2,070 | 32.143% | Extension loading, dependency resolution, lifecycle transitions, registry failures, and recovery paths require component tests with controlled extension and registry fixtures. |
| `Cove` (`Cove.Api`) | 45.207% | 21,736 | 30.996% | Controllers are substantially better covered than background services, scanners, downloaders, media processing, migration services, and fault-handling paths. API tests can reach happy paths but cannot economically force every process, network, timing, and failure branch. |
| `Cove.Core` | 69.167% | 1,044 | 23.692% | Algorithms, normalization, validation, and conversion branches are better exercised through focused unit tests. |
| `Cove.Data` | 75.635% | 14,947 | 46.214% | The aggregate is inflated by migrations executing during test database setup. Repository, authorization, query-building, merge, and rewrite behavior needs PostgreSQL-backed service and repository tests. |

### `Cove.Data` denominator warning

`Cove.Data` contains 43,383 measured migration lines, of which 37,425 execute while API-test databases are created. Its non-migration source is only 8,975 / 17,964 lines covered, or 49.961%.

`CoveContextModelSnapshot.cs` alone contributes 5,661 uncovered lines. It is generated EF model state and should not be treated as handwritten behavior that needs direct tests. Migration designer files and other generated EF artifacts should be explicitly classified or excluded before establishing a whole-application target.

## Largest non-controller hotspots

These are the largest uncovered unique-source-line areas in the latest artifact, excluding controller files.

| File | Covered / total | Uncovered | Likely test layer |
|---|---:|---:|---|
| `Cove.Data/Migrations/CoveContextModelSnapshot.cs` | 0 / 5,661 | 5,661 | Exclude as generated coverage state |
| `Cove.Api/Services/ScrapeAttemptService.cs` | 12 / 1,604 | 1,592 | Service/component tests with scraper fixtures |
| `Cove.Data/Repositories/Repositories.cs` | 896 / 2,179 | 1,283 | PostgreSQL-backed repository tests |
| `Cove.Api/Services/DownloaderService.cs` | 566 / 1,824 | 1,258 | Component tests with deterministic downloader and HTTP fixtures |
| `Cove.Api/Services/ScraperService.cs` | 213 / 1,396 | 1,183 | Component tests with deterministic scraper extensions |
| `Cove.Api/Services/MetadataServerService.cs` | 977 / 2,047 | 1,070 | Service tests against the metadata simulator |
| `Cove.Plugins/ExtensionManager.cs` | 939 / 1,858 | 919 | Extension lifecycle and dependency component tests |
| `Cove.Api/Services/ThumbnailService.cs` | 253 / 1,046 | 793 | Media service tests with controlled files and process seams |
| `Cove.Api/Services/DynamicGroups.cs` | 444 / 1,182 | 738 | Focused resolver/query tests |
| `Cove.Data/PostgresManagerService.cs` | 0 / 706 | 706 | Disposable-PostgreSQL integration tests |
| `Cove.Api/Services/StashMigrationService.Infrastructure.cs` | 0 / 695 | 695 | Migration component tests with deterministic source databases |
| `Cove.Data/CoveContext.cs` | 1,383 / 2,018 | 635 | PostgreSQL-backed invariant and change-tracking tests |
| `Cove.Api/Services/AiDataPurgeService.cs` | 149 / 780 | 631 | Service tests with seeded AI data graphs |
| `Cove.Data/Repositories/FilterHelpers.cs` | 56 / 671 | 615 | Query-construction and PostgreSQL execution tests |
| `Cove.Data/Repositories/VideoRepository.cs` | 233 / 800 | 567 | PostgreSQL-backed repository tests |
| `Cove.Api/Services/FingerprintService.cs` | 47 / 525 | 478 | Unit/component tests with process and file probes |
| `Cove.Api/Services/ScanAssetGenerationService.cs` | 14 / 487 | 473 | Scanner/media component tests |
| `Cove.Data/Repositories/ReadScopeListOptimization.cs` | 18 / 467 | 449 | Authorization-query integration tests |
| `Cove.Plugins/GitHubExtensionRegistry.cs` | 15 / 446 | 431 | Registry tests with a fake HTTP server |

Additional near-zero areas include Stash migration entity handlers, performer scraping, backup orchestration, FFmpeg integration, reference JSON rewriters, and name-rule enforcement.

## Recommended coverage strategy

### 1. Define the whole-application metric

- Cover all handwritten production code in `Cove`, `Cove.Core`, `Cove.Data`, `Cove.Plugins`, and `Cove.Sdk`.
- Deduplicate compiler-generated async/state-machine records by normalized source filename and line number, as the controller checker already does.
- Exclude generated EF model snapshots and migration designer output, or report migrations as a separate category.
- Decide whether executable migration bodies are part of the target rather than allowing database setup to dominate the aggregate.

### 2. Merge all relevant test suites

The whole-application target should merge compatible coverage from:

- `Cove.Tests` for unit, service, controller, repository, and component behavior.
- `Cove.ApiTests` for real HTTP workflows, authorization boundaries, persistence, and cross-service integration.
- Extension or SDK test projects for code that the API test host does not naturally execute.

### 3. Ratchet aggregate and per-assembly coverage

- Maintain a whole-application unique-line baseline in addition to the controller baseline.
- Track each production assembly separately so heavily executed migrations cannot hide weak SDK, plugin, or service coverage.
- Report unique line coverage and branch coverage together.
- Never lower a baseline to accept a regression.

### 4. Use the appropriate test layer

| Area | Preferred tests |
|---|---|
| Controllers and authorization boundaries | End-to-end API tests |
| Core algorithms, normalization, and validation | Fast unit tests |
| SDK serialization and HTTP contracts | SDK unit and contract tests with a fake handler/server |
| EF repositories, query filters, authorization SQL, merge services | Disposable-PostgreSQL integration tests |
| Extension manager and registry | Component tests with controlled extension packages and fake registries |
| Scanners, media services, FFmpeg, downloaders, and scrapers | Component tests with deterministic filesystem, HTTP, provider, and process seams |
| Background jobs and migration orchestration | Service/component tests with controlled clocks, queues, and source databases |

## Initial priorities

1. Update the coverage tooling to calculate and ratchet unique source lines across all production assemblies, while excluding agreed generated artifacts.
2. Collect and merge coverage from the complete existing test portfolio before treating the API-test-only gaps as definitive.
3. Add SDK tests, because its 6.6% line coverage cannot be repaired meaningfully through controllers.
4. Add plugin lifecycle and registry component tests.
5. Target high-volume API service gaps such as scraping, downloading, thumbnail generation, fingerprinting, and Stash migration with focused component tests.
6. Add PostgreSQL-backed tests for repository filters, read-scope optimization, authorization behavior, and merge/rewrite services.
