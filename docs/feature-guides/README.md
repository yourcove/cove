# Shared feature guides

Feature guides in this directory are product documentation sources that can be rendered by both the public documentation site and Cove's in-app User Guide. Keep the task instructions in the guide JSON; consumers should provide only routing and presentation.

Each guide follows `schema.json`. A guide owns its stable ID, title, description, in-app context hints, display order, optional parent topic, and ordered sections. The whole guide is one scrollable page in both consumers. Sections contain an ordered `blocks` list so paragraphs, subheadings, ordered steps, unordered lists, notes, links, tables, code blocks, and any number of images can appear in the right reading order. A link can include a `topicId` and optional `slideId` so the in-app renderer opens another User Guide topic while the website follows its `href`. Until that topic exists in-app, the link safely opens the website page instead. Store image sources below `docs/feature-guides/assets/` and use paths relative to `docs/feature-guides/`. Both builds bundle the same assets.

Use screenshots captured from the running Cove app. Crop them to the controls needed for the nearby instructions, save them as PNG or lossless WebP, and avoid drawn or generated substitutes for product UI. Screenshots contain small text and sharp interface edges, so do not use lossy WebP encoding.

To add a guide:

1. Add a JSON file that validates against `schema.json`.
2. Add a thin MDX route under the website's `docs-site/src/content/docs/docs/` tree that imports the JSON and passes it to `FeatureGuide.astro`.
3. Add the website route to the Starlight sidebar and the relevant landing page. The in-app User Guide discovers valid guide JSON files automatically.

The route wrappers contain only Starlight metadata and the renderer call. Do not copy the guide body into either consumer.
