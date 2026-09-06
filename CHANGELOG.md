# Changelog

All notable changes to Cove are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
semantic versioning (see the release lifecycle docs for how versions and tags work).

The About page in the app shows the most recent entries by parsing this file directly
(via `ui/src/data/changelog.ts`), so this is the single source of truth — edit only
here. Keep the `## [version] - date` heading format below so the parser can read it.

## [Unreleased]

## [1.4.0] - 2026-09-06

Personal dashboards, richer custom metadata, customizable shortcuts, deeper search, and safer long-running library work.

- The home page is now a set of personal dashboards. Create and name dashboards, arrange their widgets, reuse saved filters in grids, feeds, walls, and carousels, and let extensions contribute widget types alongside Cove's own.
- The User Guide has been overhauled with broader, more detailed guidance, and the website and in-app versions now share similarly expanded content.
- Custom fields add unbounded Long Text and structured JSON values. JSON fields support validated editing, readable detail-page presentation, and typed JSON Pointer targets for filtering and sorting. First-run Stash imports now migrate custom fields into Cove with readable keys, detect compatible Number and JSON values, and otherwise preserve values as Long Text.
- Keyboard shortcuts now use a central, context-aware system with a searchable reference, Cove and Stash-compatible presets, editable personal copies, multi-key chords, optional chord hints, JSON import and export, and extension-contributed actions and presets. Shortcut settings separate Cove and extension actions by source, and personal presets can be renamed.
- Filters can now express nested Boolean and quantified relationships across related media. Video and audio searches can match distinct related performers, their occurrence tags, countries, and other connected criteria, while the editor keeps the full expression visible and directly editable.
- Duplicate searches and bulk deletions now run as durable, observable background jobs. Duplicate results and keeper decisions survive navigation and retries, deletion coordinates dependent records and physical files safely, and the UI refreshes affected library views when work finishes.
- Search is faster and ranks contiguous phrases and direct matches more naturally, list pagination is stable when sort values tie, and recursive parent-tag filters include every descendant. Saved-filter names remain readable across screen sizes, and dashboards render every saved-filter display mode.
- Audio and text gain broader parity with other media through favorite workflows, shared detail presentation, performer counts, and consistent tag ordering. Narrative metadata can optionally render as Markdown, gallery cards show assigned dates and hover scrubbing, and performer dates preserve partial precision and stop age calculations at death.
- Scanning and media maintenance better handle large libraries and changing files: scan exclusions accept glob patterns, forced rescans protect fingerprints, changed gallery archives refresh correctly, missing files and Windows deletion races do not remove surviving media, and incompatible video containers transcode by default.
- Face, cover, metadata, and access workflows are more dependable. Face crops can be assigned to performers, rejected suggestions stay rejected, cover edits are hardened, video tagging preserves existing performer metadata, invite links retain their public origin, and deleting a user cascades through user-owned library data.
- Extension and API behavior is more consistent: enums outside controllers and in OpenAPI use camel-case strings, extension navigation icons load dynamically from Lucide, host library data access is documented, extension logging survives reloads, and unavailable extension measurements no longer appear as zero counts.

## [1.3.1] - 2026-08-24

Stronger access boundaries, safer metadata workflows, and more reliable face and media operations.

- Tag, performer, and studio merges now share documented transfer rules for Cove-owned relationships, metadata, JSON references, engagement, security, artwork, and extension safeguards. Foreign keys in extension-owned tables block source deletion, uninspectable locations fail closed, and opaque non-foreign-key data remains the extension's responsibility.
- Metadata-server, scraper, Stash, and Cove metadata-import paths now use the enforced performer and studio identity rules. Normalized duplicate Stash identities collapse deterministically without losing mapped relationships, Cove metadata JSON restores run transactionally, and non-unique performer aliases are never treated as identity keys.
- Tag metadata refreshes preserve the local canonical name and skip newly supplied aliases when those remote claims belong to another tag, saving the remaining metadata and reporting the omitted claims as warnings instead of failing the entire refresh.
- Scoped accounts now consistently respect content visibility across streams, group items, bulk edits, file operations, library-wide jobs, administrative transfers and maintenance, derived discovery, and telemetry. Sensitive configuration and observability data is redacted, access-artifact ownership is enforced, API token scopes are preserved, and unsafe AI data selectors are rejected.
- Face workflows add occurrence splitting and batch actions across images and videos, preserve face evidence through reversible merges, and restore permitted similarity results and completed AI-run review for members.
- Video segment updates preserve tags, while video merges retain relationships, hierarchy, spans, and child videos and reject unsafe ancestry. Persisted sub-videos inherit playable media correctly, compilation clips honor API bounds, and targeted rescans refresh replaced video metadata and image dimensions.
- Audio and text cards now align with video cards and contribute to tag and studio usage counts. Multi-value filter summaries preserve their spacing, and audio and text updates return their persisted URLs.
- Legacy plugin lifecycle transitions, performer scraper collections, UI configuration updates, display-rule tags, metadata imports, failed downloads, and database maintenance are more resilient and deterministic.

## [1.3.0] - 2026-08-19

Safer entity naming with a guided upgrade path for existing libraries.

- Tag names and aliases now share one unique namespace, performer names are unique within each disambiguation, and studio names are unique.
- Before upgrading, Cove checks for conflicting tag, performer, and studio names without changing the database or creating a backup. Libraries with conflicts are directed to the cleanup tools in the latest Cove 1.2.x release; libraries without conflicts upgrade directly.
- The upgrade trims affected names, applies deterministic safe cleanup, guards against concurrent changes, validates the result, and enforces the new rules atomically.

## [1.2.1] - 2026-08-25

Safer and faster preparation for Cove 1.3's unique-name migration.

- The Name Conflicts operation can apply every reviewed tag plan in one aggregate confirmation while preserving each selected survivor, rename, alias, and extension-reference decision.
- Performer and studio conflicts support the same reviewed batch workflow, and all cleanup succeeds or rolls back atomically.
- Failed confirmations no longer leak into later reviews, stale or linked plans are rejected safely, and validation guidance remains visible without duplicate global alerts.

## [1.2.0] - 2026-08-19

More powerful discovery and filtering, more resilient media workflows, and broader extension support.

- Upgrading to 1.2.0 before 1.3.0 is strongly recommended. Its Name Conflicts tool finds and resolves duplicate tag, performer, and studio names; 1.3.0 cannot upgrade a library while those conflicts remain, although libraries without conflicts can upgrade directly.
- Lists now support multi-level sorting, consistent filters and sorts across entity types, library-path filtering, editable applied filters, clearer filter pins, and more stable loading and pagination behavior.
- Media discovery adds metadata-aware remote ID filters, video-segment presence and tag filters, recorded-like sorting and history, aggregate media totals, and faster, better-ranked global search.
- Video and gallery workflows preserve navigation and editor state more reliably, restore timestamp and popover links, improve mobile playback controls, and handle missing or migrated covers safely.
- Scans better tolerate overlapping filesystem changes, skip verified unchanged work, limit asset generation to changed files, and provide stronger validation, cancellation, and FFprobe handling.
- Clients recover from server outages and session-refresh races more reliably, while external authentication gains host-managed identity links and explicit password requirements.
- Extensions can use shared list filtering, install directly from ZIP files, contribute floating UI, receive accurate entity events, and integrate with host authentication.
- The tagger adds previews and seeking, metadata matching is configurable, saved-filter display and zoom preferences persist, and autocomplete and bulk-edit feedback are steadier.
- Nightly development builds now use ordered, change-aware versions, and trace logging provides more useful diagnostics across high-volume operations.

## [1.1.0] - 2026-07-29

Smarter browsing and editing, smoother compilations, and a major expansion of Cove's extension platform.

- Group item views now support random sorting and saved filters, preserve filters in the URL, and play compilations in the chosen order. Compilations also honor autoplay and switch items without pausing or flashing posters.
- Saved filters are now private to each user and list type, can be updated in place, and give clearer feedback for duplicate names or failed saves.
- Continue Watching now paginates correctly and filters completed or unavailable items before counting them. Detail searches keep focus while loading, and pagination recovers when filtering or deletion leaves the current page out of range.
- Tag artwork previews now appear when hovering tag references across cards, feeds, lists, and related-item popovers.
- Metadata refreshes now save performer, studio, and tag results through the correct source. Video imports refresh related views, and overlapping video saves preserve relationships.
- Optional metadata fields can now be cleared across Cove's edit forms. Removing a performer image now also removes its stored cover and generated thumbnails.
- First-run Stash imports now require the Owner account first, ensuring imported ratings, favorites, and watch activity have an owner.
- Extensions can now customize artwork and cover editing throughout Cove, add tag filters and nested pages, and contribute media-player controls and overlays.
- Extension pages, tabs, and APIs now respect Cove permissions and authentication. Extension reloads are better isolated, and extension database migrations are atomic and retry-safe.
- Authentication is more reliable for personal access tokens, share links, and API-token media URLs, including redirected group and gallery covers.
- Documentation has been reorganized around real tasks, with clearer installation and media-mount guidance, expanded user and developer references, screenshots, and an extension tutorial.

## [1.0.0] - 2026-07-18

- Last minute performance enhancements for list pages
- Date filter fixes for "is null"

## [0.9.1] - 2026-07-18

- Extensions: settings tabs can now render as a full page. Passing `SettingsTabLayout.Page` to
  `AddSettingsTab` renders the tab's contributed panels full-width with no per-panel card chrome —
  for rich, app-like configuration that doesn't fit a stack of uniform cards. Layout is purely
  presentational: a page sources its content from the panels targeting it, exactly like the default
  `panels` layout, which is unchanged.
- Many various UI bug fixes
- Consolidate DB migrations
- Improve Stash migration

## [0.9.0] - 2026-07-07

- Scan/Rescan Fixes
- Backend Cleanup
- Scape/Identify consolidation
- Fix "Continue Watching" behavior from home page
- Deepen Recommendation Extensions capabilities

## [0.8.0] - 2026-07-01

- Add rating support for tags
- Fix video engagement tracking bugs
- Add engagement clear button in settings
- Fix Video buffering jumpt to start issues
- Fix clean job issues
- Fix optimize/wipe issues
- Fix generation issues preventing the run completing from a single bad file under specific cases
- Improve ffmpeg cleanup after finishing
- Improve ffmpeg support with foreign languages
- For faces allow a non-ideal face cover image when its the only image present
- DB cleanup

## [0.7.1] - 2026-06-27

- Extension library updates to simplify extension version declarations

## [0.7.0] - 2026-06-27

- Scan title fix
- Batch scrape fixes
- Generate cancel fix
- Timeout error resilience
- Orphaned file fix/improvements
- Face thumbnail fixes
- Audio playback fixes 
- Player cursor goes invisible on inactivity in full screen
- Video buffering fix

## [0.6.2] - 2026-06-20

- More ffmpeg fixes
- Tag exclusion filter fixes
- UI improvements/cleanup
- Gallery image view now uses a separate default filter to images list page

## [0.6.1] - 2026-06-19

- External ffmpeg hwaccel fix
- Improve identify matching logic
- Make tagger view icons clearer

## [0.6.0] - 2026-06-18

- Improve ffmpeg transcoding
- Reorganize UI settings
- Improve face organization (merging, list view, naming)
- Add faces section to performers page
- Improve segments list page
- Improve stash migration memory efficiency to prevent crashes with massive libraries
- Add ability to replace cove logo with custom logo
- Fix face "appears in" bugs
- Fix star ratings on mobile to look and function better
- Ensure tag remote ids are imported correctly on stash migration
- Improve clean
- Add gallery detail view saved filters
- 

## [0.5.0] - 2026-06-17

- ffmpeg fallback/fixes for filepaths with special characters
- Improved selective scan/generate folderpath selection
- Fix updated at date not transferring properly for some entity types in stash migration
- Add sort by path for file-backed entities
- Improve setup process of owner user/password for new installs where the auth failsafe is immediately triggered (such as behind a reverse proxy)

## [0.4.3] - 2026-06-16

- Ffmpeg fixes & improvements
- Fix transcoding of some video file types
- Fix special stash migration edge cases
- Fix Extension db migration runtime on install

## [0.4.2] - 2026-06-15

- Improve performer new dialog
- Improve how cove packages are used and consumed by extensions to prevent conflicts
- Remove autotag

## [0.4.1] - 2026-06-15

- Docker postgres 18 bugfix
- Extension installation bugfixes

## [0.4.0] - 2026-06-15

- Further Stash migration fixes
- Integrate new icon
- Flesh out in-app manual
- Jobs progress improvement

## [0.3.0] - 2026-06-13

- Scan fixes
- Stash migration generated preview
- Complete redesign of Segments on video pages
- Face improvements/fixes
- ETA redesign
- Jobs page redesign
- Settings tabs remember last collapse/expand
- Fix lightbox on mobile margins
- Add random sort for audio/texts
- Fix docker crashes if config folder isnt writeable
- Make home page customization and default saved-filters user-specific
- Auth failsfae improvements
- Stash migration performer images fix
- Assortment of UI fixes and improvements

## [0.2.0] - 2026-06-10

- Security settings panel ui improvements
- Face improvements
- Homepage fixes
- Log visibility improvements in the UI
- Improvements to setup of the owner account password
- Homepage fixes
- Scan duplicate fixes
- Group/subgroup improvements
- Stash migration fixes
- Saved filter random now doesnt store seed (random on every load)
- Scan memory leak fixed
- Fix instancemanager leaving orphaned postgres processes


## [0.1.0] - 2026-06-09

- Further scan speed improvements
- Face improvements
- Homepage fixes
- Log visibility improvements in the UI
- Improvements to setup of the owner account password

## [0.0.37] - 2026-06-08

- Improvements to Scan resiliency
- Fix scan issues with certain paths containing specific emojis
- Significantly improve scan speed (improved further with additional max tasks setting)

## [0.0.36] - 2026-06-06

- Release notes/versioning fixes
- Clean Task/Job fixes

## [0.0.35] - 2026-06-06

Extensions runtime redesign and a round of settings fixes.

- Redesigned the extension runtime for more reliable loading and isolation
- FFmpeg & Transcoding settings now save and persist correctly
- Version reporting is now driven by the release tag across the app and extension compatibility checks
- Added a Copy debug info button to the Runtime Status page
- Numerous smaller fixes and UI improvements

## [0.0.34] - 2026-06-05

Stability and data-layer fixes.

- Database repository and migration handling improvements
- Additional bug fixes across the API

## [0.0.33] - 2026-06-04

Extension loading and UI polish.

- Fixed extension loading edge cases
- UI improvements across components and pages
- Assorted bug fixes
