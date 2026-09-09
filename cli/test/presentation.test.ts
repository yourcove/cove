import { expect, test } from "bun:test";
import {
  renderAudio,
  renderAudios,
  renderAuthSummary,
  renderCatalogDetail,
  renderGalleries,
  renderGlobalSearch,
  renderGroups,
  renderImages,
  renderPerformer,
  renderPerformers,
  renderProfiles,
  renderSavedFilters,
  renderSegments,
  renderStudios,
  renderTags,
  renderTexts,
  renderVideo,
  renderVideoResults,
} from "../src/output";
import type { RenderContext } from "../src/output";
import { stripTerminalSequences } from "../src/ui";

const video = {
  id: 42,
  title: "A descriptive Unicode 🎬 video title",
  date: "2026-08-20",
  studioName: "Example Studio",
  performers: [{ id: 7, name: "Example Performer" }],
  tags: [{ id: 8, name: "Featured", tagGroupColor: "#3bbd83" }],
  groups: [{ id: 9, name: "Example Group", videoIndex: 2 }],
  galleries: [{ id: 10, title: "Example Gallery" }],
  remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-42" }],
  urls: ["https://outside.example/videos/42"],
  details: "A useful description with a [related page](/related/42).",
  files: [{ basename: "example-video.mp4", duration: 392, width: 1920, height: 1080 }],
};

const renderLists: Array<(context: RenderContext) => string> = [
  context => renderVideoResults("Videos", [video], { ...context, totalCount: 1_234_567, listPosition: { offset: 25, page: 2, perPage: 25 } }),
  context => renderGlobalSearch({ groups: [{ type: "video", items: [{ id: 42, title: video.title, subtitle: "A descriptive result context" }] }], failedTypes: ["a-descriptive-failed-type-name"] }, context),
  context => renderAudios([{ id: 1, title: "A descriptive audio title", date: "2026-08-20", studioName: "Example Studio", performers: [], tracks: [], files: [{ duration: 65, format: "flac" }] }], context),
  context => renderImages([{ id: 2, title: "A descriptive image title", date: "2026-08-20", studioName: "Example Studio", performers: [], files: [{ width: 2048, height: 1365 }] }], context),
  context => renderGalleries([{ id: 3, title: "A descriptive gallery title", date: "2026-08-20", studioName: "Example Studio", imageCount: 24, performers: [], files: [] }], context),
  context => renderTags([{ id: 4, name: "A descriptive tag name", aliases: [], tagGroupName: "Example Group", tagGroupColor: "#3bbd83", videoCount: 12, imageCount: 8, performerCount: 3 }], context),
  context => renderPerformers([{ id: 5, name: "A descriptive performer name", aliases: [], videoCount: 12, imageCount: 8, galleryCount: 3 }], context),
  context => renderStudios([{ id: 6, name: "A descriptive studio name", parentName: "Parent Studio", aliases: [], videoCount: 12, imageCount: 8 }], context),
  context => renderGroups([{ id: 7, name: "A descriptive group name", itemCount: 12, tags: [] }], context),
  context => renderTexts([{ id: 8, title: "A descriptive text title", maxWordCount: 1200, performers: [], tags: [], groups: [], files: [] }], context),
  context => renderSegments([{ id: 9, title: "A descriptive segment title", hostType: "video", hostId: 42, startSec: 10, endSec: 30, sourceKey: "user" }], context),
  context => renderSavedFilters([{ id: 10, mode: "videos", name: "A descriptive saved filter", findFilter: JSON.stringify({ q: "Unicode 🎬 query", sort: "date", direction: "desc" }), objectFilter: JSON.stringify({ tags: [8] }) }], context),
  context => renderProfiles([{ name: "personal", server: "https://cove.example/a/descriptive/path", default: true, authentication: "session" }], context),
];

const renderDetails: Array<(context: RenderContext) => string> = [
  context => renderVideo(video, context),
  context => renderAudio({ id: 11, title: "Example Audio", performers: [{ id: 7, name: "Example Performer" }], tags: [{ id: 8, name: "Featured", tagGroupColor: "#3bbd83" }], groups: [{ id: 9, name: "Example Group" }], tracks: [], files: [] }, context),
  context => renderPerformer({ id: 7, name: "Example Performer", aliases: ["Example Alias"], tags: [{ id: 8, name: "Featured", tagGroupColor: "#3bbd83" }], remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-7" }] }, context),
  context => renderCatalogDetail({ id: 12, title: "Example Image", performers: [{ id: 7, name: "Example Performer" }], tags: [{ id: 8, name: "Featured", tagGroupColor: "#3bbd83" }], groups: [{ id: 9, name: "Example Group" }], galleries: [{ id: 10, title: "Example Gallery" }], files: [] }, "image", context),
  context => renderAuthSummary("https://cove.example/a/descriptive/path", "personal", "1.2.3", "authenticated as example-user", true, context),
];

test("presentation stays bounded across terminal widths, colors, and hyperlink policies", () => {
  for (const terminalWidth of [30, 80, 140]) {
    for (const color of [false, "#3bbd83"] as const) {
      for (const hyperlinks of [false, true]) {
        const context: RenderContext = {
          color,
          hyperlinks,
          terminalWidth,
          server: "https://cove.example/base",
          metadataServers: [{ endpoint: "https://metadata.example/graphql", name: "Friendly Catalog" }],
        };
        for (const render of [...renderLists, ...renderDetails]) {
          const rendered = render(context);
          for (const line of stripTerminalSequences(rendered).split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(terminalWidth);
        }
      }
    }
  }
});

test("detail views consistently honor the shared color and hyperlink policy", () => {
  const linkedContext: RenderContext = { color: "#3bbd83", hyperlinks: true, terminalWidth: 100, server: "https://cove.example" };
  for (const render of renderDetails.slice(0, 4)) {
    const rendered = render(linkedContext);
    expect(rendered).toContain("\u001b[38;2;59;189;131m");
    expect(rendered).toContain("\u001b]8;;https://cove.example/");
  }

  const plainContext: RenderContext = { color: false, hyperlinks: false, terminalWidth: 100, server: "https://cove.example" };
  for (const render of renderDetails) {
    const rendered = render(plainContext);
    expect(rendered).not.toContain("\u001b[");
    expect(rendered).not.toContain("\u001b]8;;");
  }
});
