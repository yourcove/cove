# Cove Website

Astro + Starlight site for Cove's marketing pages and reader-centered documentation.

## Development

From the Cove repository root:

```bash
npm --prefix docs-site install
npm --prefix docs-site run dev
```

## Build

```bash
npm --prefix docs-site run build
```

Builds default to preview behavior: pages show a preview notice, ask crawlers not
to index them, and do not publish a sitemap. Production deployments must opt in:

```bash
COVE_DOCS_DEPLOYMENT=production npm --prefix docs-site run build
```

## Refresh the theme carousel

The theme carousel is generated from the codebay's demo library. From the repository root, load the codebay environment, install Playwright's Chromium runtime once, and run the capture:

```bash
source gitignored/dev/agent.env
npm --prefix docs-site exec playwright install chromium
npm --prefix docs-site run capture:theme-reel
```

The capture opens the live demo app, selects each carousel theme through Cove's Theme settings, prepares an isolated Videos grid, and replaces both the 1700×1300 PNG sources and 850×650 WebP thumbnails under `public/images/screenshots/`. The original user theme is restored and confirmed by the preferences API before the test exits.

The frontpage images have focused capture tests and can be refreshed together or just for the video detail panel:

```sh
npm --prefix docs-site run capture:frontpage
npm --prefix docs-site run capture:frontpage-video-detail
```

These captures prepare the home dashboard, feed, global search, role permissions, and selected video detail states, then replace each 1700×1300 PNG plus its optimized WebP thumbnail.

The rest of the public Screenshots gallery has its own capture suite:

```sh
npm --prefix docs-site run capture:screenshots-gallery
```

The suite prepares the video filter dialog, occurrence-tag controls, two idempotent raw-segment fixtures, a curated group, the vertical viewer, extension discovery, and an isolated native instance manager. It also refreshes the nine tracked hero portraits as real Cove images on every run, retaining their face records while rebuilding their image and detection records so fingerprints, thumbnails, and facial-recognition grouping always match the tracked files. Other missing media fixtures are created once and reused by later captures; temporary instance-manager data is removed after its capture. Each test replaces the corresponding 1700×1300 PNG and 850×650 WebP thumbnail.

The in-app manual's organization and playback examples can be refreshed separately:

```sh
npm --prefix docs-site run capture:manual-organization-playback
```

This suite regenerates the annotated scraper, tagging, segments, compilation, groups, and player images under `ui/public/manual/screenshots/`. It uses the canonical demo library and idempotently prepares documentation-only occurrence-tag, segment-profile, detection, face, and dynamic-group fixtures so individual captures also work against a fresh sidecar. The capture helpers normalize engagement reads and generated audit timestamps, block engagement writes, never save an edit, and pin every player to a fixed paused timestamp. The illustrative scraper result is intercepted inside Playwright so no scrape attempt is created.

The video-detail, URL creation, metadata-suggestion, and provenance examples have their own capture suite:

```sh
npm --prefix docs-site run capture:manual-video-metadata
```

The vertical-view capture remains part of `capture:manual-search-browsing`. It copies the tracked 9:16 video fixture into the configured demo library, imports it idempotently, and renders the full portrait video without changing Cove's saved preview settings.

## Notes

- The documentation sidebar switches between Get Started, User Guide, Developer, and Reference sections. Tutorial, how-to, explanation, and reference remain internal writing models for individual pages.
- GitHub Pages deployment is managed by the Cove repository workflow.
