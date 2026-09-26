# Changelog

All notable changes to Cove are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
semantic versioning (see the release lifecycle docs for how versions and tags work).

The About page in the app shows the most recent entries by parsing this file directly
(via `ui/src/data/changelog.ts`), so this is the single source of truth — edit only
here. Keep the `## [version] - date` heading format below so the parser can read it.

## [Unreleased]

- VR videos can be watched in a headset. A VR video's page has an **Enter VR** button wherever the browser supports WebXR: the Quest Browser, or Chrome and Edge on a PC running SteamVR or the Oculus runtime. On the Quest the video goes to an equirect media layer that the headset composites directly, so 8K plays as smoothly as in a native player. Pull the trigger to play or pause, flick the thumbstick to seek, and press B or Y to go back to the page. Fisheye and MKX200 videos play only on a PC for now.
- VR videos now record their layout: projection (equirectangular, fisheye or MKX200), field of view, and stereo packing (side by side, top/bottom or mono). The layout is read from studio file-name conventions such as `_180_LR`, `_360_TB`, `_3dh`, `_MKX200` and `_FISHEYE190`, falling back to the frame's proportions. New files with such names are marked as VR when they are scanned. The API returns the layout as `vr` on videos and accepts an explicit one on update. Clear it with `"vr"` in `clearFields` to go back to detection.
- Covers, preview clips, scrub-bar sprite sheets and segment previews of VR videos now show a flat view of the scene's centre from one eye, instead of both eyes' warped frames side by side. Regenerate them to update existing VR videos.
- Cove can serve HTTPS alongside HTTP, for headsets and other devices that only allow WebXR on a secure origin. Set `Cove:HttpsPort` (for example `Cove__HttpsPort=5443`) to turn it on. Cove creates a local certificate authority in its data folder and issues a certificate for this machine's names and addresses, which it reissues when the machine's address changes. Devices can install the authority from `/api/https/ca.crt`, or accept the browser's warning. In Docker, list the host's LAN names or addresses in `Cove:HttpsHostNames`.
- Holding the stick left or right in the headset speeds the seek up like fast-forward on a remote: 10 s steps, then 30 s after two seconds, then 2 min after five, with the position and rate shown on the timeline as it moves; the video itself is only asked to seek once its previous seek has finished, so a long hold on an 8K file no longer stalls the picture.
- The "View in VR" button on video lists appears only when a headset can be used from the browser.
- In the headset, the stick up and down zooms the picture (remembered per browser), and a timeline with the title, position, zoom and controls appears in front of you while seeking or paused, then stays put in the room rather than following your head. A plays and pauses like the trigger; B or clicking the thumbstick goes back, since some runtimes leave B unmapped. Both on the PC path for now.
- The video's centre now lines up with where you are looking, pitch included, instead of only your heading. It is taken once your head has held still for a moment after the video appears (not at the click, when you are still looking at the screen), and again whenever you hold the trigger. Lying back or looking up no longer leaves the scene tilted away.
- Leaving a VR video's page stops its immersive playback; a session borrowed from a gallery goes back to the gallery.
- In the headset, seeking and the timeline now go through the player's own playhead, so a video streamed as a transcode seeks correctly and shows its real length instead of the segment's. The page's copy of the video is hidden while the headset shows it. Only the thumbstick steers; the touchpad is ignored on controllers that have both, since a resting thumb registers on the Index's pad.
- VR videos get a stereoscopic preview clip alongside the ordinary preview when previews are generated, served by `GET /api/stream/video/{id}/vr-preview` (404 until generated), so galleries can preview in 3D.
- Settings → Preview has "VR covers and previews": one eye flattened (2D) or both eyes side by side (3D) for VR videos on flat screens. Regenerate after changing it.
- A VR video's page shows its layout next to the code and director, and the edit form lets you set the projection, field of view and stereo packing by hand when detection gets it wrong. The fields only appear for videos marked VR.
- `GET /api/stream/video/{id}/vr-card` returns a stereoscopic card for a VR video: each eye's flat view of the scene's centre, side by side, for galleries that show VR covers in 3D. The generate job makes it alongside covers; nothing is generated on request.
- **View in VR** on every video list: the Videos page and the video lists on tag, performer, studio, group and gallery pages have a button that shows the list's current page as a wall of cards in the headset, with the same items, sort and filters the browser shows. Move with the thumbstick or by pointing, rest on a card for its preview, and pull the trigger or press A to open it; the browser follows to the video's page, and back returns both to the list. The wall pages a full grid at a time whatever the browser's page size, with S, M and L card sizes, a 3D/2D card toggle and a toggle to show 2D videos as well as VR ones (VR only asks the server, so the pages and count are of VR videos). Changing the filters, sort or page in the browser, or moving to another list page, updates the wall; turning pages in the headset keeps the browser on the page holding the wall's first card.
- Every video's page has the Enter VR button, not only VR videos: an ordinary video plays on a large virtual screen, which the stick up and down resizes. Pressing back in a video started from its own page brings the browser back to the list it came from and shows that list in the headset.
- Extensions can use Cove's VR playback through the new `@cove/runtime/webxr` runtime module, including handing a video off inside an immersive session the extension opened itself, or handing the session to the video's page so the browser follows what the headset plays.
- 3D films (stereoscopic flat video) play in the headset as a flat screen with depth, each eye seeing its own half of the frame, instead of being wrapped onto a 180° sphere. Mark the video VR and pick **Flat screen (3D film)** as its projection, or let the file name decide: `HSBS`, `FSBS`, `HOU`, `FOU` and similar tags, or `3D` next to `SBS` or `OU`, lay out a video marked VR as a 3D film (they do not mark a file VR on scan). Half-width and half-height packing is recognised by the frame's proportions and restored. Covers, previews and cards show one eye, or both side by side, without reprojection. The API returns and accepts `flat` as the projection.
- Fixed coming back from a video to a gallery that handed its session over: the gallery now lends its renderer to the video page (`presenter` on the handoff), so its own frame loop is never displaced and the wall returns as soon as the video ends.
- Fixed entering VR a second time without reloading the page: sessions now share one WebGL context and a new request waits for the previous session to finish ending.

## [1.5.1] - 2026-09-25

Fixed extension upgrades and uninstalls that could leave extensions stuck disabled.

- Updating an extension together with a dependency it requires no longer fails partway and leaves the updated extensions disabled. Earlier versions unloaded the dependency first, which disabled its dependents, and then could not unload those dependents because a disabled extension no longer had the services its uninstall step runs with. The same failure affected uninstalling or updating any extension that had been disabled since the server last started, and every later attempt failed the same way until the server restarted. Cove now sets up the services just for the uninstall step, and the extension is unloaded even when that step cannot run. Extensions left disabled by this failure can be updated from Discover after upgrading. Other extensions that depend on an updated one are still disabled during the update and need to be enabled again afterwards.

## [1.5.0] - 2026-09-24

- Videos can now be converted to another codec or container from the video list or a video's own page, using the FFmpeg that Cove already has. Pick a quality, optionally lower the frame rate, and convert a selection in the background; the converted file is attached to the video, and can optionally replace the original once it has been verified to decode cleanly and to show the same footage. Conversion measures quality rather than guessing a bitrate. For each video, Cove encodes a few short samples, scores them against the original, and searches for the smallest file that keeps the chosen quality.
- Generating covers, previews, sprites, and perceptual hashes is substantially faster. Frames are now extracted by batching many seeks into a few ffmpeg invocations instead of launching one ffmpeg per frame, which cuts sprite extraction for a long 4K video from over two minutes to around twenty seconds, and previews are assembled in a single encode rather than one per segment. Extracted frames are byte-for-byte identical to before, so existing sprites and perceptual hashes stay comparable.
- Generate now recognises incomplete source files before decoding them. A download that stopped early still reports its full duration, so Cove used to spend tens of seconds per asset discovering it could not be read, on every run. Such files are now identified from their metadata, skipped with an explanation naming the size on disk against the size the duration implies, and shown on the video's file details so you know what to re-download. The verdict is re-evaluated automatically if the file changes.
- A video with a few unreadable frames now still gets a sprite. Missing frames are filled from their nearest readable neighbour as long as most of the sheet decoded, instead of the whole scrubbing preview failing.
- Perceptual hashes are now produced by one extraction path rather than whichever of three strategies happened to run, so hashing the same file twice gives the same answer. One of the old strategies drifted far enough from the others that a video could fail to match itself.
- Hardware acceleration is now used where it actually helps. Preview encoding uses the configured hardware encoder, and VAAPI encoding works for the first time (it was missing the device and frame-upload setup, so it always fell back to the CPU). Frame extraction stays on the CPU because it is dominated by seeking rather than decoding, where hardware decoding measured two to four times slower.
- Breaking change for extensions: the in-process ("managed") frame extractor has been removed along with the `FrameExtractionMode` setting. Batching made the ffmpeg process path faster than in-process decoding, and a fault in the native decoder could terminate Cove outright. Extensions that read `CoveConfiguration.FrameExtractionMode` or `CoveConfigDto.FrameExtractionMode` must be rebuilt; extensions that do not touch it are unaffected.
- The video list view is now a table with a column picker. Choose, reorder, and resize columns such as studio, date, duration, resolution, performers, tags, rating, play count, or file path, and the column layout is saved with saved filters and the default filter.
- Breaking change for extensions: `PerformerFilter.CountryCriterion` is now typed as `CountryCriterion` so performer filters can select several countries. Extensions compiled against Cove 1.4 that read or set that property must be rebuilt against this release; extensions that do not touch it are unaffected. `VideoDto.PrimaryFileId` is an init property instead of a positional parameter, so `VideoDto` keeps its Cove 1.4 constructor, and the records that Cove 1.4 extended again accept their Cove 1.3 constructors. Builds of the extension ABI projects now fail on further binary breaks against the latest release.
- The Duplicate Finder has been rebuilt around reviewing and resolving duplicates quickly and safely. Search by visual similarity with accuracy presets, identical files, titles, or scene IDs, limited to or excluding library folders and ignoring short clips. Settings are remembered, searches show live progress, and recent searches can be resumed where you left off.
- Duplicate keeper rules pick the best copy in every group by resolution, duration, bitrate, frame rate, preferred codec, file size, metadata, watch history, organized state, preferred folder, or date added. Each group records why its keeper was chosen, and the rules can be re-applied to all groups at any time.
- Duplicate groups line copies up side by side with hover previews, highlighted best values, and visual-distance and length hints. A compare view adds a draggable slider, synchronized playback with an offset for trimmed copies, and aligned frame strips. Groups can be filtered, sorted by space to free, paged, quick-viewed without leaving the page, and reviewed with keyboard shortcuts.
- Resolve one duplicate group or all of them while you keep reviewing; resolution runs in the background. Tags, performers, galleries, groups, links, remote IDs, ratings, favorites, play counts, and markers can be merged into the kept copy before the others are removed, optionally with their files.
- Groups marked as not duplicates are remembered, so later searches never group those videos together again.
- The duplicate compare view zooms and pans both copies together, down to one screen pixel for every source pixel, and shows how much of each copy's resolution actually reaches the screen, so you can tell whether a 4K copy is really sharper than a 1080p one. Keep either copy or go full screen without leaving the comparison.
- The Duplicate Finder can review videos that have more than one file attached, such as a 4K download next to the original. Each video's files are compared side by side with the same keeper rules, slider, zoom and playback, and resolving keeps the chosen file as the video's primary file (with its cover, previews and markers when it is the same footage) and removes the others, optionally deleting them from disk. Files you keep together are remembered by later searches.
- Downloader settings can store site logins, so downloaders such as yt-dlp can sign in to sites that only offer some content, like higher resolutions, to logged-in accounts. Passwords are write-only and never returned by the settings API.
- Downloader quality options now report the width and height of the stream they download, so tools can compare an available resolution with the file already in the library.
- The Docker images now include Python, in a virtual environment that extensions can install into, so extensions that drive Python tooling work in a container without a custom image.
- Filters contributed by extensions now run on the Videos list, not only on Tags, so an extension can narrow the normal video list (with previews, sorting, and every built-in filter) to the videos it knows about. List totals count the same rows the list shows.
- Changing a video's primary file to another copy of the same footage now keeps its cover, sprite, and previews instead of clearing them, so list thumbnails and hover previews keep working without waiting for a new generate run. Aligning a timeline to different footage still clears them.
- A batch download no longer stops when one URL cannot be matched to a downloader, such as a link whose page has been taken down. That URL is reported as a failed item in the job summary and the rest of the batch continues.
- Bulk video and performer updates can now add, remove, set, or clear custom field values on every selected item in one request, the same way they already handle tags and performers. Only the named fields are touched, unknown fields or values of the wrong type reject the whole request, and the last-updated time changes only on items whose values actually changed.
- The bulk Edit dialog for selected videos and performers now includes a collapsible Custom fields section. Expand it, tick a field, enter a value with the same control the single-record editor uses, choose Overwrite, Add, or Remove for the group, or clear a field on every selected item. Untouched fields are left alone.
- Video merges from the videos page, the video detail page, and the Duplicate Finder now run through one merge service with one default policy. A library merge fills empty fields from the merged copy, carries over ratings, favorites, bookmarks, play history, and custom fields, and records provenance for every field it changes. The merge request can ask for the merged copy's file to be removed instead of attached, with the same delete options as the Duplicate Finder, and a single duplicate group can be resolved with field-level metadata choices. Markers and timed group items follow a removed copy onto the kept video only when the two files are equivalent, using the same check as the primary-file flow instead of a fixed running-time tolerance.
- The merge review now compares the files side by side with the best value marked, lets you attach the merged copy's files or remove them with the same delete options as the Duplicate Finder, and reviews a merge of three or more videos as one combined incoming side with an origin badge where the copies disagree. The destination picker and the detail-page merge dialog say which video is kept and which are merged in, then removed, and the primary action reads "Merge & remove N copies".
- The merge review's Files section lets you make the merged copy's file the kept video's primary. After the merge, Cove switches directly when the two files are equivalent or nothing timed is affected, and otherwise opens the set-primary-file dialog so markers and clips can be aligned to the new timeline.
- Resolving a single duplicate group with Merge & remove now opens the same merge review when confirmation is on, so you can keep the removed copy's title or studio, drop a wrong tag, and see before confirming which markers will not move because the files are not equivalent. The group-by-group shortcut flow, Resolve all, Compare and Not duplicates are unchanged, and the Resolve all dialog describes the merge policy in the review's words.
- The video tagger reviews a scrape result with the same rows as a merge: From <server> and Current side by side with a Filled, Replaced or Kept outcome per field, chip lists with Combine, Only current and Only <server> presets, amber chips for tags and performers that will be created, a badge on fields you edited by hand (which now stay put unless you choose the scraped value), a summary of every change, and an "Apply N changes" action.
- Tags, performers, galleries and links in a merge review, and tags and performers in the tagger, are edited as in the video's edit form: every item is a chip with an x, kept ones included, chips sit in alphabetical order, and a search box adds anything from the library (or creates it) beside what the other side brings. The tagger sends those hand edits with the scrape, so a StashDB match can gain a tag the server does not know.
- The video tagger is easier to read and works on a phone. Each row is the thumbnail, title, query and one Search button, with fingerprint search and StashDB submissions behind a menu, and the toolbar keeps Source and Search all (the bulk action was called Scrape All) with the rest behind a menu. A match opens as a short list of facts: a check mark for every field the scrape fills, an inline Keep or Use choice where the video already has a different value, added tags and performers as chips, and the unchanged fields on one line. Adjust opens the full side-by-side review for per-item chips and hand edits, other matches sit behind one link, and the whole row wraps so nothing is squeezed beside the thumbnail on narrow screens.
- Scraping or refreshing a performer now uses the same review as videos: the image first and large, a check mark for every field the source fills, an inline Keep or Use choice where the performer already has a different value, added aliases, links and tags as chips, and Adjust for the full side-by-side rows. When StashDB, TPDB or another metadata server has several images for the performer, the review lets you step through all of them and stores the one on screen when you apply, instead of always taking the first. Only the image being looked at is loaded, its neighbours are fetched once it is showing, and an image that cannot load leaves a placeholder without blocking the rest of the review.
- Portrait videos and photos taken on a phone are now filed with the width and height you actually see. Such a file stores a landscape frame plus a note to turn it upright, and Cove recorded the frame while ignoring the note, so a portrait recording was stored as landscape and its tall thumbnail was shown inside a wide card. The stored size now matches the picture, and those videos and images get a portrait card. Resolution badges and resolution filters are unaffected, since they already read the longer and shorter edge rather than width and height. Files scanned before this release keep their old size until they are read again: turn on "Force rescan (ignore mtime)" in Scan to correct a library in place.
- Audio filters can now match tags applied to one performer's appearance in an audio, the way video filters already could. A performer criterion on the audio list combines exact performers and their occurrence tags on the same link, so "this performer, tagged this way, in this audio" no longer matches an audio where a different performer carries the tag. Tag applications on a video never answer an audio occurrence.
- Bulk audio updates accept custom field values with the same Add, Remove, Set, and clear behavior as videos and performers, so one key can be merged into selected audios without reading and resending their other fields. An unknown key or a value of the wrong type still rejects the whole request before anything is applied, and the last-updated time changes only on audios whose values actually changed.
- Extensions can now import Cove's audio player, audio card, audio filter criteria and audio sort options from `@cove/runtime/components`, so an extension can build an audio workspace out of the same pieces the native audio pages use.


## [1.4.1] - 2026-09-07

Restored compatibility for existing extensions and improved extension-owned infinite scrolling.

- Extensions compiled against Cove 1.3 and earlier can again call video and performer repository searches and library cleanup without `MissingMethodException`. Cove 1.4.0 had appended optional parameters to these public interface methods, which changed their binary signatures; hidden forwarding overloads now preserve the original contracts while retaining the new filtering and path-selection capabilities.
- Extension result layouts can now reuse Cove's infinite-scroll sentinel so grid, wall, feed, and vertical display modes continue loading when an extension owns the rendered content.

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
