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

## Notes

- The documentation sidebar switches between Get Started, User Guide, Developer, and Reference sections. Tutorial, how-to, explanation, and reference remain internal writing models for individual pages.
- GitHub Pages deployment is managed by the Cove repository workflow.
