# Changelog

All notable changes to Cove are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
semantic versioning (see the release lifecycle docs for how versions and tags work).

The About page in the app shows the most recent entries by parsing this file directly
(via `ui/src/data/changelog.ts`), so this is the single source of truth — edit only
here. Keep the `## [version] - date` heading format below so the parser can read it.

## [Unreleased]

- Fixed: a fresh standalone install on Linux failed to start because the pgvector package Cove downloaded had been removed from the PostgreSQL apt server. Managed PostgreSQL now downloads its binaries from a release on Cove's own GitHub repository for every platform, verifies them against a checksum, and no longer depends on Debian or Ubuntu package names, so it also works on distributions it did not recognise before, such as Debian 13. The Linux download bundles the libraries PostgreSQL needs and runs on any distribution with glibc 2.35 or newer (Ubuntu 22.04, Debian 12 and later). The Windows download is much smaller because it leaves out pgAdmin and StackBuilder, which Cove never used.
- Starting Cove as `root` on Linux or macOS with managed PostgreSQL now stops with a message explaining that PostgreSQL refuses to run as root, instead of failing later during database initialisation.
- Managed PostgreSQL runs with `jit = off`. The query compiler never helped Cove's short queries, and leaving it out keeps the Linux download small.
- Fixed: a newly installed extension that adds a page to the navigation bar now shows it straight away. Before, the page started switched off in Settings → Interface → Navigation, so the extension seemed to be missing. An update that adds a new page shows that page too; pages you have switched off stay off when you update or reinstall the extension.

## [1.7.0] - 2026-10-24

- Large libraries load much faster, especially the Segments page, which could take 30 seconds or more per page and often failed with a server error on libraries with millions of segments. On a copy of a library with 5.9 million segments, the raw segment list loads in about 1 second instead of 45, its other sorts in 0.5 to 2 seconds instead of 26 to 31, a segment text search in under 5 seconds instead of 40, and a filter by performer almost instantly instead of 33 seconds.
- Cove checks for new releases itself and shows a notice when one is available. Settings → System Info → **Updates** shows your version, release channel and last check, with **Check now** and the release notes. On a standalone (native) install, **Download & install** downloads and verifies the release, backs up the database and config, swaps the program files, and restarts Cove on the same port, whether you started Cove directly, through the Instance Manager, or as a systemd service. If the new version does not start, Cove puts the previous version back and shows why the update failed, and **Revert** goes back to the previous version afterwards, as long as the update has not changed the database. Docker installs get the notice with `docker compose pull` instructions instead of an install button. Updates can be installed automatically inside a time window you choose, either all of them or only those with the same major version and no database changes; automatic updates are off by default, never run while jobs are running, and never retry a version that failed. Only the owner can see and install updates by default, through the new `system.update` permission.
- Fifteen new built-in themes: Abyss, After Dark, Broadcast, Espresso, Federation, Groove, Lounge, Reel, Terminal and Velvet, plus Honeycomb, Sakura, Burgundy, Nebula and Rainfall, which come with a background pattern or image. The new **Pattern** component style puts a repeating pattern, or an image you upload, behind the page on any theme, with settings for colour, opacity, scale, fit and dimming. The Glass style was also refined. Extension themes can recommend options for component styles through the new `UIThemeDefinition.StyleOptions` property, applied when the user picks the theme.
- Dashboard widgets can be sized. Each widget can take the full width, a half, a third, a quarter or a fifth of the dashboard, so several sit side by side, and card rows can show more than one row of cards, a card size of their own, and a limit on how many pages they page through. A single-row widget scrolls sideways; a taller one pages through whole pages of cards. On small screens widgets stack at full width. Dashboard rows now use the same cards as list pages, and card size is one setting shared by videos, performers, studios, tags, groups, galleries, audios and texts everywhere in Cove; images and faces keep their own. Each row shows how many items its query matches beside its title, and opening a video from a video row queues the rest of that row's query for playback, as the Videos list does.
- Videos and images can be filtered by **AI Analysis**: whether an item's most recent AI run for a source failed, was cancelled, is in progress, completed or never ran, which models its completed runs used or did not use, and when it was last analysed, including relative dates such as `-30d`. **Needs analysis** selects items never analysed or whose latest run was cancelled. The criterion needs the `airuns.read` permission. The upgrade adds an index for it.
- Videos and images can be filtered by **File Size**, comparing each item's largest file in bytes, as audios and texts already could. The video and image file-size criteria use a new `LongCriterion`, so sizes above 2 GB work.
- Selected videos, images, audios and texts can be sent to **Generate** from the selection toolbar on their list pages and in lists on detail pages, instead of one item at a time from its detail page.
- Finished jobs are kept across restarts: the 100 most recent stay in the jobs drawer and in Settings. A finished job started from Cove's UI or the API can be run again with **Retry**, which repeats the same request as you. Jobs started by other jobs, by uploads, or by a request that carried a password, token or key cannot be retried.
- Videos have a **Production Date** beside their release date, for when a video was shot rather than when it came out. It accepts a year, a year and month, or a full date, like the release date, and shows on a video's Details tab. The Videos list can filter by it, including relative dates such as `-1y`, sort by it, alone or in a compound sort, and show it as an optional **Production Date** column. Performer ages on a video count from its production date when it has one and from its release date otherwise: the **Performer Age** filter and sort, the **Age (then)** condition on related performers, and the ages on a video's performer cards. Edit it in the video edit form; the tagger, Identify, and video merge review show it as a separate field. Importing from a metadata server such as StashDB now fills it from the scene's production date, and a Stash migration brings over the production dates that newer Stash versions record on scenes. Video drafts submitted to a metadata server include it. A metadata server that does not know scene production dates, such as a stash-box older than the end of 2024, is queried without them instead of failing. Sub-videos take the production date of their parent video, and a video split keeps it unless the split describes the new video itself. `cove-cli` shows it in video details and sorts video lists by `production_date`. The API returns it as `productionDate` on videos and accepts it on `POST /api/videos` and `PUT /api/videos/{id}`; clear it with `"productionDate"` in `clearFields`. Video queries take `productionDateCriterion` and the `production_date` sort. Extensions get it as `ProductionDate` on `Video`, `VideoDto`, `VideoCreateDto`, `VideoUpdateDto`, and `MetadataServerVideoMatchDto`, and can filter with `VideoFilter.ProductionDateCriterion`.
- Sorting videos by release date or production date counts undated videos as the oldest: they come first from oldest and last from newest, as single-column date sorts of images, galleries, and groups already do. Before, videos without a release date came last in both directions. This applies to single and multi-column sorts and to the Videos list with compilations. New indexes let both date sorts read the first page from an index instead of ordering every matching video; on a library of about 30,000 videos, the first newest-first page went from 20 ms to under 1 ms.
- Settings → Library → Custom Fields offers a **Timestamp** type for a date and time. Records show its value in UTC to the second, it can be sorted, and its filter supports the usual comparisons plus relative offsets. Values entered without an offset are read as UTC. Timestamp filters compare whole seconds, so **Equals** matches anything recorded within that second and **>** starts at the next second.
- Relative date filters on Date and Timestamp custom fields now work, such as `-7d` on a Date field or `-1h30min` on a Timestamp field. Every timestamp filter, built-in or custom, also accepts `min` for minutes and `s` for seconds after hours, so `-1h30min15s` means one hour, thirty minutes and fifteen seconds ago; `m` still means months.
- Behavior change for API callers and extensions: a Date or Timestamp custom-field filter value that looks like a relative offset, such as `-1d` or `10s`, is no longer ignored. A valid offset is applied, and a malformed one, such as `-30s1min` or a time unit on a Date field, now returns 400. Saved filters with such values are rejected the same way when saved.
- Faces, audios and texts now have the same repository query videos have, so extensions list them the way Cove's own pages do instead of rebuilding the filters. `IFaceRepository.FindAsync(FaceFilter, FindFilter)` runs the faces list query: `FaceFilter` gains the filter dialog's criteria (linked, label, source key, path, has cover, ignored, performers, top suggestion performers, detection, appearance, frame-sample, video and image counts, suggestion confidence) and the list's scoping fields, and `POST /api/faces/find` takes the same request body as the video list. `IAudioRepository` and `ITextDocumentRepository` are new, with `FindAsync`, `AggregateAsync`, `FindIdsAsync` and `GetByIdWithRelationsAsync` in the shape of `IVideoRepository`; the audio and text list query code moved out of the controllers into them, and the text list now honors filter expressions like the audio list. `GET /api/faces`, `/api/audios` and `/api/texts` and their `find` and `aggregate` endpoints behave as before.
- Extensions can query the segment library the way the Segments page does. `ISegmentRepository.FindAsync(SegmentFilter, FindFilter)` applies every criterion of the Segments page's filter dialog (title, kind, source, confidence, duration, start and end time, created and updated at, tags, videos, video tags, performers, faces) plus search, sort, seeded random order and paging, and returns each page's rows with their video, tag and performer names; `AggregateAsync` returns the match count and summed duration. `SegmentFilter` carries those criteria and the flat scoping fields of `GET /api/segments`, including the new `ids` and `excludeIds`. `POST /api/segments/find` and `POST /api/segments/aggregate` take the same request body as the video list (`objectFilter` plus `findFilter`) and the Segments page now uses them; `GET /api/segments` is unchanged. `DoubleCriterion` is new in Cove.Core for fractional number criteria. Extensions that build a segment list on Cove's list components get `createSegmentCriteria`, `readRawSegmentListFilter` and `buildSegmentFilteredQuery` from `@cove/runtime/components` to turn the filter dialog's object filter into that request.
- `cove-cli extensions install` installs extension packages from local ZIP files or package URLs, replacing installed copies of the same extensions, for example `cove-cli extensions install core.zip faces.zip --trust-unverified --profile personal`. Packages are installed in the order given and the first failure stops the run, naming the packages already installed. It reports each installed version, any dependencies still missing, and why Cove could not activate a package, such as locked files or a required restart.
- Video filters that exclude tags respond much faster when they also narrow the list sharply, for example with related-performer conditions. Excluding a tag checked its manual and derived tags together, which PostgreSQL could plan as a rescan of every derived video tag for each candidate video; on a library of about 30,000 videos, a saved filter combining two age conditions on related performers with an excluded tag went from about 4 seconds per page to under half a second, and on larger libraries it could time out.
- An extension running a prerelease build, such as `0.8.1-dev.5` or `0.8.1-rc.1`, is now offered the `0.8.1` release from the extension registry as an update. Before, the update check compared prerelease versions as text and did not offer it.
- When the NVIDIA driver is too old for the bundled FFmpeg's NVENC, Cove now logs a warning that quotes FFmpeg's required driver version, instead of silently encoding in software. The bundled FFmpeg currently needs driver 610 or newer for NVENC; on the widely packaged 580 series it cannot encode, though decoding still works. The Docker README documents the requirement.
- Clicking **+ Add** in a video's Segments panel starts the new segment at the current playback time instead of `0:00`. The new **Add segment at current time** keyboard shortcut, `n` by default, opens the Segments tab and does the same; rebind it in Settings → Keyboard shortcuts.
- Groups can be shown in or hidden from video browsing in bulk: the groups bulk edit has a **Video Browsing** field.
- Settings → Extensions → Discover has a **Sort** menu: default order, recently updated, or name. Sorting by name no longer depends on the server's language or puts capitalized names first.
- The library folder tree opens the folders above the selected path, so reopening a path filter on a deeply nested folder no longer means expanding every level again. This applies to the path filter, the scan and generate folder pickers, and duplicate search.
- Navigation links that do not fit in the top bar move into a **More** menu. Before, links past the search box were cut off at medium window widths and could not be reached.
- Identify remembers its sources, their order, and its options in each browser. Sources are now opt-in: a source you have not chosen before, such as a newly installed scraper, starts unticked. **Skip multiple matches** is on by default, so a video with several candidate matches is left alone instead of getting the top match.
- When a metadata source fails during Identify, Cove now tries the remaining sources instead of stopping, and the job reports the failure: it finishes as completed with failures, and its summary says how many videos were identified, failed and unmatched, with each source's errors. A metadata server URL that returns a web page instead of an API response is now reported as such instead of as an unknown error or an invalid API key.
- The tagger can link a scraped performer to a performer already in your library instead of creating a duplicate, as it already could for studios and tags. The library performer then remembers that metadata server's ID, so later matches from that server find it automatically.
- Typing a tag's alias into a tag picker now picks that tag instead of offering to create a new tag, and if the tag is already on the item, the picker says so. If the server refuses to create a tag from the picker, its reason now shows in the picker instead of a generic error.
- Edit forms show **Save** in the accent colour only when there are unsaved changes.
- Long Text custom field values wrap in the edit form and on the Details tab instead of being cut off.
- A video's History tab collapses playback sessions with more than four watched ranges, and hides watched ranges shorter than 2 seconds, which come from seeking. Watch totals and recommendations still count everything.
- The Faces pages show the file name for untitled videos and images instead of `Video 123` or `Image 123`.
- The Audios, Texts, Segments and Faces lists, and the Videos list with compilation groups shown, now return the 500 and 1,000 items per page the page-size menu offers. They stopped at 250, so selecting all items on the page and editing them changed only some of what was selected.
- Expanding a large or slow folder in the library folder picker no longer gives up after 15 seconds.
- Restoring a config backup now applies it to the running server. Before, Cove reported success but kept using the old settings, and the next settings change wrote them back over the restored file.
- Adding or removing a tag on a video, image or other item through tag applications, including tags added by AI extensions, now updates its **Updated** date.
- Submitting a video draft to a stash-box server works when the video's cover is a GIF, BMP, TIFF or animated WebP: Cove sends the first frame as a PNG. Before, stash-box rejected the whole draft.
- Filters with a related-performer condition that selects several genders, or that excludes all of a set of tags or performers, respond in well under a second where they took several seconds to tens of seconds or timed out. The chips of an imported filter now name the tags, performers, galleries, videos, audios and faces it refers to instead of showing **Unavailable item**.
- Refreshing the database upgrade screen, or opening Cove in another tab, while an upgrade runs now shows its progress instead of offering to start the upgrade again.
- Settings → Server → Host & Network has **Database cache (MB)**, the memory Cove's own PostgreSQL uses to cache data, applied after a restart. It stays at 128 MB unless you raise it; a larger cache can speed up large libraries.
- Scanning no longer fails or creates duplicate files in folders whose stored path is spelled differently from the folder on disk, such as Stash-migrated folders stored with a lower-case drive letter or without a backslash after it. The scan now updates the stored path to the folder's real path.
- A batch download that fails no longer leaves behind an empty item holding its URL, which made every later attempt to download that URL fail as a duplicate.
- The **Force rescan (ignore mtime)** description, and the documentation, now say what it does: it re-reads metadata and checks folders a normal scan skips, but does not compare file contents, so a replaced file with the same size and modified time is not treated as changed.
- When Cove deletes a stored cover or entity image, it now also deletes the resized copies it made for display, which were left on disk.
- Behavior change for API callers: video, image, audio and text **list** responses carry each item's tags as `tagIds`, with `tags` empty; `GET /api/tags/lookup` returns every tag you can read with the fields lists show. Detail responses still carry full tags. Tag fields with no value are left out of responses instead of being sent as `null`.
- Behavior change for API callers: playback sessions can be recorded only for videos, images, audios, texts, segments and groups; other host types return 400. Recording playback of a compilation (group) needs `groups.read`, which the built-in viewer roles include. Engagement for an entity type Cove does not recognise is now denied instead of allowed.
- Behavior change for API callers and extensions: an Identify request that does not set `skipMultipleMatches` now skips videos with several candidate matches instead of applying the top one. `PUT /api/system/config` ignores the `updates` section; change update settings through `PUT /api/updates/settings`, which needs `system.update`.

## [1.6.1] - 2026-10-15

- This is the first release with standalone downloads for the 1.6.0 changes. The 1.6.0 build failed on macOS, so 1.6.0 shipped only as Docker images.
- Fixed first-run setup on macOS and Windows failing when Cove downloads its own PostgreSQL: the download server started refusing requests without a User-Agent, so Cove exited during startup. Cove now identifies itself when downloading PostgreSQL, as it already did for FFmpeg.
- Submitting a video draft to a metadata server such as StashDB now works when the video has several files with the same hashes, for example the same file stored at two paths. Before, the draft listed each shared fingerprint once per file, and the metadata server failed when saving the scene from it. Drafts now send each fingerprint once, as Stash does, and **Submit fingerprints** no longer submits a shared fingerprint more than once.

## [1.6.0] - 2026-10-14

- Downloads started without create-missing choices on screen now follow the Scraping → scrape apply defaults from Settings: Settings → Operations → Download From File, downloading a source into an existing video, image, or gallery, and creating an item from a URL. Before, these sent whatever a scrape dialog had last remembered in that browser, or no creation at all, so a URL file could download videos without creating their performers even though Settings said to. Scrape and download dialogs that do show the choices now start from the Settings defaults as well, instead of all-off, the first time they open in a browser.
- Downloads that apply metadata now follow the Scraping → scrape apply defaults from Settings for creating missing tags, performers, and studios and for marking the result organized, unless the request says otherwise. Before, `POST /api/system/downloaders/download` always created all three and `POST /api/system/downloaders/download-batch` never created any unless the caller asked, so a batch sent from the browser extension linked only performers that already existed. The Cove UI's download dialogs are unchanged: they already send what the user chose. Breaking change for extensions: `createMissingTags`, `createMissingPerformers`, `createMissingStudio`, and `markOrganized` on `DownloaderStartRequestDto`, `DownloaderBatchItemDto`, and `DownloaderBatchFollowUpDto` are now `bool?`; an extension that reads or sets them must be rebuilt.
- Settings → Downloaders → Downloader Limits has **Max download resolution**. When a download asks for the best available quality, as the Cove UI, the browser extension, and the API do by default, downloaders that offer several resolutions now fetch the best stream at or below this height instead of the largest one. A download that names a specific resolution keeps it. Off by default. `DownloaderRequest.MaxHeight` carries the limit to downloaders; the batch endpoint applies it when it picks a quality from a match. Downloaders built before this version ignore the limit on single downloads until they are updated.
- Video play dates are now each user's own, like play counts already were. Before, the dates listed under Play History on a video's History tab were one shared list: every user saw the same dates, and **-1** or **Reset** removed them for everyone. Upgrading gives the existing dates, which nearly all come from Stash imports, to the owner, who also holds the play counts imported with them; an install with no owner account drops them. **Wipe automatic engagement data…** in Settings, and `POST /api/engagement/activity/reset-all`, now also remove your play dates; both already reset your play counts to zero. Share links and other callers without a user no longer see any play dates, and `POST /api/videos/{id}/play` records a date only for a signed-in user or a user's API token.
- Behavior change for extensions: `VideoPlayHistory` has a required `UserId`. Saving a play date without one now fails, so set it to the user who played the video. `IUserEngagementService.RecordVideoPlayAsync` records nothing for a principal without a user, such as `CovePrincipal.System()`.
- Bulk edit on the Videos, Images and Galleries pages has a **Favorite** field: choose **Add to favorites** or **Remove from favorites** to change your own favorites for every selected item at once, without changing anyone else's. Leaving the field unticked leaves favorites as they are. The Favorite field in bulk edit for performers, tags and studios now uses the same two buttons instead of True and False. Cards refresh their hearts and ratings right after a bulk edit instead of up to 30 seconds later. `POST /api/videos/bulk`, `/api/images/bulk` and `/api/galleries/bulk` accept an optional `favorite`, applied for the user making the request; like a bulk rating, it needs write permission for the items. Extensions get it as `Favorite` on `BulkVideoUpdateDto`, `BulkImageUpdateDto` and `BulkGalleryUpdateDto`.
- Favorite performers, tags and studios are now each user's own, like favorite videos, images and galleries already were. Before, the favorite filter, search ranking, cards and the favorite fields returned by the API used one shared flag per performer, tag or studio, so a favorite set by a bulk edit, a performer, tag or studio edit, or a Stash import showed for every user, while the heart on the detail page showed the user's own choice. Upgrading moves each shared favorite that no user had as their own favorite to the owner, keeping every favorite users already set. Role content rules that test the `favorite` attribute of performers, tags or studios are converted on upgrade to the performers, tags or studios they matched at that point, so they keep hiding or allowing exactly what they did; afterwards they no longer follow anyone's favorites. A rule that matched non-favorites keeps matching performers, tags or studios added later. New rules on that attribute match nothing. Bulk edit, the edit forms, Stash imports and the Performer Favorite filter on videos, images and galleries now use the user's own favorites. Metadata exports no longer include favorites, and imports ignore them, as with ratings. Repeating a video search as a different user no longer returns the previous user's cached results, which could show their favorites and ratings.
- Behavior change for extensions: `Performer.Favorite`, `Tag.Favorite` and `Studio.Favorite` are obsolete: they are no longer stored, so they load as `false` and setting them changes nothing. Read a user's favorite through `IUserEngagementService.GetSnapshotAsync` instead. Extensions that read or set these properties keep loading but no longer see or change favorites.
- Extensions can offer tag, performer and other entity pickers as filters on the Videos and Tags lists. The filter dialog shows the same picker as Cove's own Tags filter, including excluded entities and **Include sub-tags**, and saved filters and links keep all of it. Extensions declare one with `UIManifestBuilder.AddExtensionEntityListFilter`, and their filter provider receives the picker's choice as `{"value":[ids],"excludes":[ids],"depth":-1}`. Extensions that rely on this must declare this version as their `minCoveVersion`. Saved extension filters that use a modifier such as **Includes All** or **Is Null** now reopen with that modifier instead of an unrecognized one.
- The **Related Performers** filter on the Audios, Images, Galleries and Texts pages now explains what it finds in terms of that page, such as "Find audios by performer", instead of always saying videos. Extensions that define their own related-entity filter can set `relatedResultLabel` to name what their list shows.
- Filters that an extension evaluates, such as Segment Studio's on the Videos page, can no longer be moved into an advanced filter group, including saved ones whose extension is disabled. Cove applies them to the whole list, so inside an OR or NOT group they were silently ignored. Saved filters that already have one inside a group still ignore it there; edit the filter and add it again outside the group.
- The browser tab names the extension page you are on, such as "Segment Studio | Cove", instead of just "Cove", and lists inside an extension page read like "Videos | Segment Studio | Cove". Extensions can name a view more specifically with `useDocumentTitle` from `@cove/runtime/components`: on an extension's own page the title goes in front of the page's label, and elsewhere, such as a tab on a video's page, it replaces the page's title while shown. On older Cove versions it is missing, so look it up on the module namespace rather than importing it by name.
- The **Saved filters** menu can be searched: its first row, which used to only say "Saved filters", is now a search box. Type to narrow the list, use the arrow keys and Enter to apply a match, and press Escape to clear the search. Saved filters named **Group / Name**, with a space on each side of the slash, are listed together under a collapsible group header above the ungrouped filters, and a pencil beside each name renames it in place. Existing names are unchanged, and a slash without spaces, as in `1080p/4K`, does not start a group.
- Saved filters can be moved between Cove instances. **Import / Export** at the bottom of the **Saved filters** menu opens a dialog. **Export** lets you pick one of the list's saved filters, from a searchable list grouped like the menu, and copies it to the clipboard as JSON; the JSON is folded away unless you open it, and display options and sorting with page size are left out unless you tick them. Tags, performers, studios and other linked items are written by name, with their remote IDs and a performer's disambiguation, instead of this library's IDs. Conditions that cannot be written that way, such as an image custom field or an extension filter whose extension is unavailable, are left out and listed. **Import** takes pasted JSON for the same list and shows, for each linked item, whether it was found by remote ID, found by name or alias (ignoring case), or not found. Missing tags, performers and studios are created with the name and remote IDs from the JSON unless you untick them; other missing items are left out, and a condition left with nothing to match is removed rather than kept in a form that would match everything.
- Hovering the seek bar of the player on a video's page shows the sprite-sheet frame and time at that point above the bar. Videos without sprite sheets show no preview.
- Clicking the scrub strip along the bottom of a video card opens the video at the moment the frame shown was captured, keeping the list's play queue. The frame is a sprite-sheet tile: Cove captures tiles in the middle of their interval and Stash at its start, which Cove tells apart by the sprite file name, so the video opens on that frame rather than wherever the pointer was within the tile. Videos without sprite sheets, or whose sprite times do not cover the video, open at the pointer's time. Ctrl- or Cmd-clicks are ignored. On phones and other devices without a mouse or trackpad the strip is no longer shown, so a tap on the card opens the video normally; on iPhone a tap there used to show a still frame and open nothing.
- Settings → Playback & Viewers has **Show scrubber sprites below the player**. It is on by default; turn it off to hide the row of thumbnails under the player on a video's page. Hovering the seek bar with a mouse still previews frames. The setting applies to users who can read system settings; others, such as share-link guests, always see the row.
- Tag groups can be given distinct colors in one step. **Suggest distinct colors** in Tags → Groups previews a new color for every group that has no color, still has the old default green, or shares its color with another group, and leaves groups with a color of their own unchecked. Check or uncheck any group (**Select all groups** checks every group, including those with a color of their own, or unchecks them all when every group is already checked), use **Shuffle** to try another arrangement, and nothing is saved until **Apply**. Suggested colors share one brightness and differ in hue, placed as far as possible from each other and from the groups that keep their colors. If some groups can't be saved they are named, with a **Retry** button, and **Undo** restores the previous colors, except for groups whose color was changed again in the meantime. New groups now start with the hue furthest from the existing groups instead of always the same green, editing a group with no color no longer saves green into it, and emptying the color field when editing now removes the group's color.
- Hovering a tag now names its group. The colored marker's tooltip says, for example, "Tag group: Outdoors" instead of only "Tag group", and when a tag's image or sources popup appears it names the group, with the group's color when it has one.
- Tags in edit forms are colored the way they are on the Details tab: each chip takes its tag's color, or its group's, and shows the group marker. Before, the tag field when editing a video, image, gallery, audio, text, group or studio drew every tag in plain grey, and the performer edit form tinted tags more faintly and spelled out the group name. A tag in a group without a color now shows a neutral marker that names the group, on the Details tab too.
- Video and image phashes now match the ones Stash computes. Before, about a third of the videos Cove hashed came out a few bits away from Stash's value for the same file, and occasionally more: a frame next to a scene cut, or the colours of some 8K VR files, could move a hash by 8 to 14 bits. Cove now samples frames at the same times Stash does, extracts them without JPEG compression, and shrinks images with the same arithmetic. In a sample of 100 library videos every hash matched Stash's, and video hashing takes the same time as before. Images in PNG, GIF, BMP, lossless WebP and most TIFF files match exactly; JPEG and lossy WebP decode slightly differently from Go's decoders, so most match and the rest come out within a few bits; CMYK JPEGs and TIFFs with premultiplied alpha come out close but not always exact. Existing phashes are not recomputed: run Generate with **Video perceptual hashes** or **Image perceptual hashes** and **Overwrite existing** to update them. Until then, a phash from before this change and one from after can differ for copies of the same video, so they may not show as duplicates of each other. Phashes Cove created before 1.5.0 for videos of ten minutes or less benefit most, since that older method could differ from Stash by 20 bits or more.
- **Select all** on the Videos and Images pages with the page size set to Infinite now asks the server for the matching ids in one query instead of fetching every page of full videos or images and keeping only the ids. On a 390,000-video library this took about 30 minutes; it now takes seconds. The visual-search results still select page by page, since the similarity service only answers that way. `POST /api/videos/find-ids` no longer refuses a filter matching more than 50,000 videos; only a filter with extension criteria keeps that bound, since extension filter providers are handed the candidates. `POST /api/images/find-ids` is new and takes the same request as `POST /api/images/find`, returning just the ids of every matching image in the list's order. Extensions get the same through `IImageRepository.FindIdsAsync`.
- Generate no longer offers **Segment thumbnails** or **Animated segment previews**. The animated previews were never shown anywhere: hovering a segment card plays the video itself from the segment's start. Segment cards show the sprite-sheet frame at the segment's start, or a cover set by hand on the segment's page. Selecting either option made Generate list every video in the library, even with **Overwrite** off, and on a large library spend days creating an image for every segment. **Clean generated files** now deletes every animated segment preview, including those of videos that still exist. Segment thumbnails already on disk are still used, and still deleted along with their video.
- Breaking change for extensions: `GenerateOptionsDto.Segments`, `GenerateOptionsDto.SegmentThumbnails`, `GenerateOptionsDto.SegmentPreviews` and `IStreamService.GetSegmentAnimatedPreview` have been removed, along with `GET /api/stream/video/{id}/segment-preview`. Extensions that use them must be rebuilt; extensions that do not are unaffected.
- Fixed videos imported from Stash showing "No video file available": the import left every video without a primary file, so none of them could play. Imports now set it to the file Stash marked as primary. Upgrading repairs videos that were already imported. For videos with several files, the repair should also pick the file Stash marked as primary, because the import has always added that file first, and our tests matched Stash's choice. If a video plays a different file than expected, use **Set as primary** on the video's File Info tab.
- Linking a face to a performer, deleting a face and merging faces stay fast for faces that appear on many images. Linking a face seen on about 800 images takes under a second instead of failing after the 15-second request timeout, and deleting or merging faces on thousands of images takes seconds instead of half a minute or more. Linking the top suggestion for a large selection of faces no longer fails with an "out of shared memory" database error. After a face is unlinked, the performer provenance that face propagation records for each image no longer lists the removed performer.
- Generating scrub-bar sprite sheets is faster and uses far less CPU on high-resolution video. Each tile is now taken from the keyframe at or just before its time instead of decoding up to the exact frame. On 8K VR files this cut sprite generation from about 47 to 30 seconds per video and its CPU use by about 15 times. A tile can be up to one keyframe interval early, usually under a few seconds. Tiles that would repeat another tile's frame are still taken at their exact time. Phashes are unchanged.
- Text search on the Videos page stays usable on very large libraries. On a two-million-video library, searches for rare words now return in well under a second instead of five to ten seconds per query, and the Videos page counts matches once per filter instead of again on every page and infinite-scroll step. `GET /api/videos` and `POST /api/videos/find` accept `skipCount` to return a page without its total: `totalCount` is then `-1`, unless the request still needs a count, such as one with `perPage` set to `0`. Extensions that set `FindFilter.SkipCount` must declare this version as their `minCoveVersion`.
- File size totals of a petabyte or more show their unit instead of "undefined".
- Settings → Interface has **Sort searches by relevance**. It is on by default; turn it off to keep a list's current sort when you type a search instead of switching to relevance order, which is much faster on very large libraries. Relevance stays available in the sort menu. The setting applies to users who can read system settings; other users keep relevance order.
- Sorting videos by date from oldest lists videos without a date last, as sorting from newest already did. This applies to single and multi-column sorts and to the Videos list with compilations. In a multi-column video sort, a title column now places untitled videos where sorting by title alone does: last from A to Z and first from Z to A.
- VR videos can be watched in a headset. A VR video's page has an **Enter VR** button wherever the browser supports WebXR: the Quest Browser, or Chrome and Edge on a PC running SteamVR or the Oculus runtime. Pull the trigger to play or pause, flick the thumbstick to seek, and press B or Y to go back to the page.
- VR videos now record their layout: projection (equirectangular, fisheye or MKX200), field of view, and stereo packing (side by side, top/bottom or mono). The layout is read from studio file-name conventions such as `_180_LR`, `_360_TB`, `_3dh`, `_MKX200` and `_FISHEYE190`, falling back to the frame's proportions. New files with such names are marked as VR when they are scanned. The API returns the layout as `vr` on videos and accepts an explicit one on update. Clear it with `"vr"` in `clearFields` to go back to detection.
- Covers, preview clips and scrub-bar sprite sheets of VR videos now show a flat view of the scene's centre from one eye, instead of both eyes' warped frames side by side. Regenerate them to update existing VR videos.
- Cove can serve HTTPS alongside HTTP, for headsets and other devices that only allow WebXR on a secure origin. Set `Cove:HttpsPort` (for example `Cove__HttpsPort=5443`) to turn it on. Cove creates a local certificate authority in its data folder and issues a certificate for this machine's names and addresses, which it reissues when the machine's address changes. Devices can install the authority from `/api/https/ca.crt`, or accept the browser's warning. In Docker, list the host's LAN names or addresses in `Cove:HttpsHostNames`.
- Holding the stick left or right in the headset speeds the seek up like fast-forward on a remote: 10 s steps, then 30 s after two seconds, then 2 min after five, with the position and rate shown on the timeline as it moves; the video itself is only asked to seek once its previous seek has finished, so a long hold on an 8K file no longer stalls the picture.
- The "View in VR" button on video lists appears only when a headset can be used from the browser.
- In the headset, the stick up and down zooms the picture (remembered per browser), and a timeline with the title, position, zoom and controls appears in front of you while seeking or paused, then stays put in the room rather than following your head. A plays and pauses like the trigger; B or clicking the thumbstick goes back, since some runtimes leave B unmapped.
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
- Fixed the VR wall showing no pointers after pressing back in a video started from its own page: the controllers the session already had were never connected, so pointing and the trigger did nothing until they were switched off and on.
- A video started in the headset from its own page now has the same controls as one opened from the VR wall: zoom, recentre and the timeline work on the Quest too. The Quest now draws it the way the wall does (a WebGL projection layer) instead of handing it to the compositor as a media layer, so the controls match wherever playback started. Extensions can still ask for the media layer with `preferMediaLayer` on `@cove/runtime/webxr` playback.
- 3D films (stereoscopic flat video) play in the headset as a flat screen with depth, each eye seeing its own half of the frame, instead of being wrapped onto a 180° sphere. Mark the video VR and pick **Flat screen (3D film)** as its projection, or let the file name decide: `HSBS`, `FSBS`, `HOU`, `FOU` and similar tags, or `3D` next to `SBS` or `OU`, lay out a video marked VR as a 3D film (they do not mark a file VR on scan). Half-width and half-height packing is recognised by the frame's proportions and restored. Covers, previews and cards show one eye, or both side by side, without reprojection. The API returns and accepts `flat` as the projection.
- Fixed coming back from a video to a gallery that handed its session over: the gallery now lends its renderer to the video page (`presenter` on the handoff), so its own frame loop is never displaced and the wall returns as soon as the video ends.
- Fixed entering VR a second time without reloading the page: sessions now share one WebGL context and a new request waits for the previous session to finish ending.
- Appears With on a performer page now ranks co-stars by the videos they share with the performer in one paged list, with filters and counts for frequent partners (five or more videos together), recurring co-stars and one-time co-stars. Each co-star shows how many videos they have together, the years and studios of those videos, a bar of them across the performer's career and the latest few, with buttons that play those videos in a queue or open exactly them in the performer's Videos tab. **Filters** takes the same video filters as the performer's Videos tab, conditions and combined expressions included, and counts only the shared videos they match, so the tab can show who a performer appears with in videos with a given tag, studio or rating; the links to those videos keep the filter. **Duos only** counts just the videos with the two of them, the list can be narrowed by gender or favorites and sorted by the most recent, first or longest partnership, and ticking several co-stars shows how many videos have all of them or any of them. The tab no longer shows performer cards; to bulk edit or merge co-stars, filter the Performers list by Related Videos with this performer instead. `GET /api/performers/{id}/pairings` returns what the tab uses: each of the performer's videos with another performer and its visible cast, the solo videos per year, and every co-star with their own video count. `POST /api/videos/find-ids` takes the same request as `POST /api/videos/find` and returns just the ids of every matching video, up to 50,000.
- Extensions can render an entity's primary media through `EntityMedia` from `@cove/runtime/components`, the boundary Cove's own cards and heroes use. Media contributed by other extensions, such as animated tag previews, then also appears in extension widgets.
- Cove can store the shot boundaries of a video file: a gapless partition of the file's timeline into shots, with each shot's frames, shot type and the transition it is entered by, and where the set came from (source, AI run, model and version, decode backend). Shots belong to the file that was analysed: they move with it when videos are merged or files are moved, and are deleted with it. Extensions write and edit them through the new `IVideoShotService`, which keeps every set a valid partition, skips a file that already has shots unless the caller asks to replace them, and checks each edit against the set's revision. `GET /api/videos/{id}/shots` returns the set of the video's primary file (or of `fileId`), `GET /api/videos/{id}/shots/sets` lists every file's set, and `DELETE /api/videos/{id}/shots` removes one. Settings → AI Data counts and purges them as "Shot Boundaries".
- A video file's shot boundaries now survive Cove deleting the file in favour of another copy of the same footage: when a conversion deletes the original, when file review or the Files tab deletes a duplicate of the primary file, and when a merge removes a copy, the shots move to the video's primary file (for a merge, the kept video's). They move only when both files have matching stored perceptual hashes, their running times agree to within two frames or 0.1 s, and the shots were measured on the file's current contents; otherwise they are deleted with the file. A primary file keeps shots of its own, unless they are an unedited analysis and the moving shots were edited by hand. Frame numbers are dropped when the frame rates differ. A cut that replaces the original removes the original's shots instead, since they no longer match the cut file's timeline, and the job says to analyse the video again.

## [1.5.1] - 2026-09-25

Fixed extension upgrades and uninstalls that could leave extensions stuck disabled.

- Updating an extension together with a dependency it requires no longer fails partway and leaves the updated extensions disabled. Earlier versions unloaded the dependency first, which disabled its dependents, and then could not unload those dependents because a disabled extension no longer had the services its uninstall step runs with. The same failure affected uninstalling or updating any extension that had been disabled since the server last started, and every later attempt failed the same way until the server restarted. Cove now sets up the services just for the uninstall step, and the extension is unloaded even when that step cannot run. Extensions left disabled by this failure can be updated from Discover after upgrading. Other extensions that depend on an updated one are still disabled during the update and need to be enabled again afterwards.

## [1.5.0] - 2026-09-24

- Videos can now be converted to another codec or container from the video list or a video's own page, using the FFmpeg that Cove already has. Pick a quality, optionally lower the frame rate, and convert a selection in the background; the converted file is attached to the video, and can optionally replace the original once it has been verified to decode cleanly and to show the same footage. Conversion measures quality rather than guessing a bitrate. For each video, Cove encodes a few short samples, scores them against the original, and searches for the smallest file that keeps the chosen quality.
- Parts of a video can now be cut out to save space. Choose **Trim…** on a video's page, drag across the timeline under the player (or mark from the playhead) to mark what to remove, and watch the result with the removed parts skipped before committing. A fast cut copies the kept footage without re-encoding and takes seconds. An exact cut lands precisely on the marks and re-encodes the rest at the chosen quality, which shrinks it further. Before anything changes, the preview shows how much space the cut saves and what happens to each marker, clip and group range. Replacing the original moves them all onto the new timeline: items after a cut move earlier, items spanning a cut are joined across it, and items entirely inside removed footage are deleted. It also deletes the original file and regenerates the video's cover, previews and sprite.
- Extensions can propose cuts. `POST /api/videos/{id}/cut/preview` reports what a set of removals would do without changing anything, `POST /api/videos/cut` cuts any number of videos in one job, and a link such as `/video/123?cut=0-95.5,1800-1932` opens a video's trim editor with the removals marked for the user to review. See `docs/video-cutting.md`.
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
