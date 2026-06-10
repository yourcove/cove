# Changelog

All notable changes to Cove are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
semantic versioning (see the release lifecycle docs for how versions and tags work).

The About page in the app shows the most recent entries by parsing this file directly
(via `ui/src/data/changelog.ts`), so this is the single source of truth — edit only
here. Keep the `## [version] - date` heading format below so the parser can read it.

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
