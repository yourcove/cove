# In-app manual screenshots

The manual uses screenshots with baked-in, color-coded callouts. The matching explanation points are declared in `ui/src/components/TutorialStoryboardDialog.tsx` with prefixes such as `[green]` and `[blue]`; the dialog strips those prefixes from the visible copy and uses them to color the explanation cards.

New screenshots should be generated from the live demo app instead of annotated by hand. The helpers in `manual-capture-helpers.ts` calculate each target's browser bounding box, add a temporary labeled overlay in the matching color, and save a deterministic 1700×2000 PNG under `ui/public/manual/screenshots/`.

The automated batches currently cover 23 of the manual's 43 unique screenshots: Getting Started, List Pages, Search and Browsing, Content Types, and Settings and Access. From the repository root, load the codebay environment and run:

```sh
source gitignored/dev/agent.env
npm --prefix docs-site run capture:manual-getting-started
npm --prefix docs-site run capture:manual-list-pages
npm --prefix docs-site run capture:manual-search-browsing
npm --prefix docs-site run capture:manual-content-types
npm --prefix docs-site run capture:manual-settings-access
```

Every capture test must use role- or label-based locators, assert the intended live state before capture, avoid save/run actions, and block engagement writes when the page would otherwise record a visit. When a public-friendly frame needs to differ from disposable demo configuration, use capture-time visual overrides; these change only DOM properties after all loading waits and are asserted immediately before the screenshot.
