# Changelog

All notable changes to Cove are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
semantic versioning (see the release lifecycle docs for how versions and tags work).

The About page in the app shows the most recent entries by parsing this file directly
(via `ui/src/data/changelog.ts`), so this is the single source of truth — edit only
here. Keep the `## [version] - date` heading format below so the parser can read it.

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
