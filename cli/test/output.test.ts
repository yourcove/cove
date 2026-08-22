import { expect, test } from "bun:test";
import { renderAudio, renderAudios, renderCatalogDetail, renderGalleries, renderGlobalSearch, renderGroups, renderImages, renderPerformer, renderPerformers, renderProfiles, renderSavedFilters, renderSegments, renderSimilarImages, renderSimilarVideos, renderStudios, renderTags, renderTexts, renderVideo, renderVideoResults, renderVideos } from "../src/output";
import { stripTerminalSequences } from "../src/ui";

test("renders a dependency-free, bounded Unicode video list", () => {
  const rendered = renderVideos(
    { id: 1, name: "Example", aliases: [] },
    [{ id: 9, title: "A very long 🎬 title ".repeat(12), date: "2026-01-01", studioName: "Studio", performers: [], files: [{ basename: "fallback.mp4", duration: 65, width: 1920, height: 1080 }] }],
    { color: false },
  );
  expect(rendered).toContain("Example · 1 match");
  expect(rendered).toContain("1:05");
  expect(rendered).toContain("…");
  expect(rendered).not.toMatch(/[┌┬┐├┼┤└┴┘│]/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(100);
});

test("video lists mirror Cove's dense two-line card hierarchy", () => {
  const video = {
    id: 42,
    title: "Example Video",
    date: "2026-08-18",
    studioId: 11,
    studioName: "Example Studio",
    details: "A useful description that remains readable while the result stays compact across the terminal.",
    performers: [
      { id: 1, name: "Alpha Performer" },
      { id: 2, name: "Beta Performer" },
      { id: 3, name: "Gamma Performer" },
      { id: 4, name: "Delta Performer" },
      { id: 5, name: "Epsilon Performer" },
      { id: 6, name: "Zeta Performer" },
    ],
    tags: Array.from({ length: 3 }, (_, index) => ({ id: index + 1, name: `Tag ${index + 1}` })),
    files: [{ duration: 392 }],
  };
  const plain = renderVideoResults("Videos", [video], { color: false, hyperlinks: false, terminalWidth: 80 });
  const lines = plain.split("\n");

  expect(lines[2]).toStartWith("Example Video");
  expect(lines[2]).toEndWith("Example Studio · 6:32 · 2026-08-18");
  expect(Bun.stringWidth(lines[2]!)).toBe(80);
  expect(lines[3]).toStartWith("Alpha Performer, Beta Performer, Gamma Performer, Delta Performer +2");
  expect(lines[3]).toEndWith("  42");
  expect(lines[3]).not.toStartWith(" ");
  expect(Bun.stringWidth(lines[3]!)).toBe(80);
  expect(plain).not.toContain("A useful description");
  expect(plain).not.toContain("👤");
  expect(plain).not.toContain("🏷");
  expect(plain).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  expect(plain).not.toContain("#42");

  const colored = renderVideoResults("Videos", [video], { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 80 });
  expect(stripTerminalSequences(colored)).toBe(plain);
  expect(colored).toContain("\u001b[38;2;59;189;131m\u001b]8;;https://cove.example/base/video/42\u0007Example Video\u001b]8;;\u0007\u001b[39m");
  expect(colored).toContain("\u001b]8;;https://cove.example/base/studio/11\u0007Example Studio\u001b]8;;\u0007");
  expect(colored).toContain("\u001b]8;;https://cove.example/base/performer/1\u0007Alpha Performer\u001b]8;;\u0007");
  expect(colored).toContain("\u001b]8;;https://cove.example/base/performer/4\u0007Delta Performer\u001b]8;;\u0007 +2");
  expect(colored.split("\n")[3]).toEndWith("  \u001b[2m42\u001b[22m");
  expect(colored).not.toContain("\u001b[90m");
  expect(colored).not.toContain("\uFE0E");
  expect(colored.split("\u001b[38;2;59;189;131m")).toHaveLength(2);

  const rightAlignedIds = renderVideoResults("Videos", [
    { ...video, id: 7, title: "First" },
    { ...video, id: 1_234, title: "Second" },
  ], { color: false, terminalWidth: 80 });
  expect(rightAlignedIds.split("\n")[3]).toEndWith("  7");
  expect(rightAlignedIds.split("\n")[5]).toEndWith("  1234");
  expect(Bun.stringWidth(rightAlignedIds.split("\n")[3]!)).toBe(80);
  expect(Bun.stringWidth(rightAlignedIds.split("\n")[5]!)).toBe(80);
});

test("similarity cards show ranked match context without losing media links or alignment", () => {
  const context = { color: true, hyperlinks: true, server: "https://cove.example", terminalWidth: 80 };
  const video = renderSimilarVideos([{ video: { id: 42, title: "Video match", performers: [{ id: 7, name: "Example Performer" }], files: [] }, distance: 0.12, sectionIndex: 1, startSec: 65, endSec: 80 }], "visual", context);
  const plainVideo = stripTerminalSequences(video);
  expect(plainVideo.split("\n")[0]).toBe("Visually similar videos · 1 match");
  expect(plainVideo.split("\n")[3]).toEndWith("88% match · 1:05–1:20 · 42");
  expect(Bun.stringWidth(plainVideo.split("\n")[3]!)).toBe(80);
  expect(video).toContain("\u001b]8;;https://cove.example/video/42\u0007Video match\u001b]8;;\u0007");
  expect(video.split("\n")[3]).toEndWith("\u001b[2m88% match · 1:05–1:20 · 42\u001b[22m");

  const image = renderSimilarImages([{ image: { id: 9, title: "Image match", performers: [], files: [] }, distance: 0.04 }], { color: false, terminalWidth: 50 });
  expect(image.split("\n")[0]).toBe("Visually similar images · 1 match");
  expect(image.split("\n")[3]).toEndWith("96% match · 9");

  const wholeVideo = renderSimilarVideos([{ video: { id: 8, title: "Whole video", performers: [], files: [] }, distance: 0.2, sectionIndex: 0, startSec: 65, endSec: 80 }], "visual", { color: false, terminalWidth: 50 });
  expect(wholeVideo.split("\n")[3]).toEndWith("80% match · 8");
  expect(wholeVideo).not.toContain("1:05–1:20");

  const narrow = renderSimilarVideos([{ video: { id: 2_147_483_647, title: "Narrow match", performers: [], files: [] }, distance: 0, sectionIndex: 1, startSec: 3_600, endSec: 7_200 }], "audio", { color: false, terminalWidth: 30 });
  expect(narrow.split("\n")[3]).toEndWith("100% match · 2147483647");
  expect(Bun.stringWidth(narrow.split("\n")[3]!)).toBe(30);
});

test("video performer overflow remains bounded for unusually large lists", () => {
  const video = {
    id: 2_147_483_647,
    title: "Large cast",
    performers: Array.from({ length: 5_000 }, (_, index) => ({ id: index + 1, name: `Performer ${index + 1}` })),
    tags: Array.from({ length: 5_000 }, (_, index) => ({ id: index + 1, name: `Tag ${index + 1}` })),
    files: [],
  };
  const rendered = renderVideoResults("Videos", [video], { color: false, terminalWidth: 80 });
  const performerLine = rendered.split("\n")[3]!;

  expect(performerLine).toStartWith("Performer 1");
  expect(performerLine).toMatch(/ \+[\d,]+\s+2147483647$/);
  expect(Bun.stringWidth(performerLine)).toBe(80);

  const narrowLine = renderVideoResults("Videos", [video], { color: false, terminalWidth: 30 }).split("\n")[3]!;
  expect(narrowLine).toStartWith("Performer");
  expect(narrowLine).toEndWith("  2147483647");
  expect(narrowLine).not.toContain("👤");
  expect(narrowLine).not.toContain("🏷");
  expect(Bun.stringWidth(narrowLine)).toBeLessThanOrEqual(30);
});

test("video card truncation preserves complete grapheme clusters", () => {
  const flag = "🏳️‍🌈";
  const rendered = renderVideoResults("Videos", [{ id: 42, title: flag.repeat(40), performers: [], tags: [], files: [] }], { color: false, terminalWidth: 30 });
  const title = rendered.split("\n")[2]!;
  const visibleFlags = Math.floor(29 / Bun.stringWidth(flag));

  expect(title).toBe(`${flag.repeat(visibleFlags)}…`);
  expect(Bun.stringWidth(title)).toBeLessThanOrEqual(30);
});

test("paged lists show Cove-style position summaries in the heading and below the results", () => {
  const videos = Array.from({ length: 25 }, (_, index) => ({ id: index + 26, title: `Example Video ${index + 26}`, performers: [], files: [] }));
  const rendered = renderVideoResults("Videos", videos, {
    color: false,
    terminalWidth: 100,
    totalCount: 1_378,
    listPosition: { offset: 25, page: 2, perPage: 25 },
  });
  const summary = "26-50 of 1,378 · Page 2/56";
  const lines = rendered.split("\n");

  expect(lines.slice(0, 3)).toEqual(["Videos", "", summary]);
  expect(lines[4]).toStartWith("Example Video 26");
  expect(rendered).not.toContain("Videos · 1,378 matches");
  expect(lines.at(-1)).toBe(summary);
  expect(rendered.split(summary)).toHaveLength(3);

  const narrow = renderVideoResults("Videos", videos.slice(0, 1), { color: false, terminalWidth: 30, totalCount: 1_378, listPosition: { offset: 25, page: 2, perPage: 25 } });
  expect(narrow.split("\n").slice(0, 4)).toEqual([
    "Videos",
    "",
    "26-26 of 1,378 · Page 2/56",
    "",
  ]);
  const narrowColored = renderVideoResults("Videos", videos.slice(0, 1), { color: true, terminalWidth: 30, totalCount: 1_378, listPosition: { offset: 25, page: 2, perPage: 25 } });
  expect(narrowColored.split("\n")[0]).toBe("\u001b[1mVideos\u001b[22m");
  expect(narrowColored.split("\n")[2]).toBe("\u001b[2m26-26 of 1,378 · Page 2/56\u001b[22m");

  const bounded = renderVideoResults("Videos", videos.slice(0, 3), { color: false, totalCount: 10, listPosition: { offset: 0 } });
  expect(bounded.split("1-3 of 10")).toHaveLength(3);
  expect(bounded).not.toContain("Page 1");

  const finalPage = renderVideoResults("Videos", videos.slice(0, 9), { color: false, totalCount: 49, listPosition: { offset: 40, page: 2, perPage: 40 } });
  expect(finalPage.split("41-49 of 49 · Page 2/2")).toHaveLength(3);

  const emptyPage = renderVideoResults("Videos", [], { color: false, totalCount: 49, listPosition: { offset: 80, page: 3, perPage: 40 } });
  expect(emptyPage).toContain("No matches found.");
  expect(emptyPage.split("0 of 49 · Page 3 requested · 2 pages available")).toHaveLength(3);
});

test("renders grouped global search results and partial failures", () => {
  const rendered = renderGlobalSearch({ groups: [{ type: "video\nINJECTED", items: [{ id: 4, title: "Example\nVideo", subtitle: "Studio" }] }], failedTypes: ["\u001b[2Jtext"] }, { color: false });
  expect(rendered).toBe("Video INJECTED · 1 match\n\nTITLE          CONTEXT  ID\nExample Video  Studio   4\n\nSome searches failed: text");
  expect(rendered).not.toContain("\t");
});

test("global search uses available terminal width before truncating text columns", () => {
  const title = "A descriptive result title that fits comfortably";
  const result = { groups: [{ type: "video", items: [{ id: 4, title, subtitle: "Short context" }] }], failedTypes: [] };

  const wide = renderGlobalSearch(result, { color: false, terminalWidth: 80 });
  expect(wide).toContain(title);
  for (const line of wide.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(80);

  const narrow = renderGlobalSearch(result, { color: false, terminalWidth: 30 });
  expect(narrow).toContain("…");
  for (const line of narrow.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(30);

  const unicodeTitle = "Distinctive Unicode 🎬 title fits here";
  const skewed = renderGlobalSearch({ groups: [{ type: "video", items: [{ id: 4, title: unicodeTitle, subtitle: "Long context ".repeat(100) }] }], failedTypes: [] }, { color: false, terminalWidth: 80 });
  expect(skewed).toContain(unicodeTitle);
  for (const line of skewed.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(80);

  const empty = renderGlobalSearch({ groups: [{ type: "video", items: [] }], failedTypes: [] }, { color: false, terminalWidth: 80 });
  expect(empty).toBe("Video · 0 matches\n\nNo matches found.\nTry changing the filters.");
});

test("wide list tables use spare width for every content-bearing column", () => {
  const id = 123456789;
  const title = "A descriptive text title that should fit";
  const rendered = renderTexts([{
    id,
    title,
    performers: [],
    tags: [],
    groups: [],
    files: [],
  }], { color: false, totalCount: 1, terminalWidth: 120 });

  expect(rendered).toContain(String(id));
  expect(rendered).toContain(title);
  expect(rendered).not.toContain("…");
  for (const line of rendered.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(120);
});

test("linked entity lists lead with meaningful labels and omit redundant IDs", () => {
  const context = { color: false, hyperlinks: true, server: "https://cove.example/base", terminalWidth: 100 } as const;
  const cases: Array<[rendered: string, route: string, id: number, label: string]> = [
    [renderVideoResults("Videos", [{ id: 1, title: "Example Video", performers: [], files: [] }], context), "video", 1, "Example Video"],
    [renderAudios([{ id: 2, title: "Example Audio", performers: [], tracks: [], files: [] }], context), "audio", 2, "Example Audio"],
    [renderImages([{ id: 3, title: "Example Image", performers: [], files: [] }], context), "image", 3, "Example Image"],
    [renderGalleries([{ id: 4, title: "Example Gallery", performers: [], files: [] }], context), "gallery", 4, "Example Gallery"],
    [renderTags([{ id: 5, name: "Example Tag", aliases: [] }], context), "tag", 5, "Example Tag"],
    [renderPerformers([{ id: 6, name: "Example Performer", aliases: [] }], context), "performer", 6, "Example Performer"],
    [renderStudios([{ id: 7, name: "Example Studio", aliases: [] }], context), "studio", 7, "Example Studio"],
    [renderGroups([{ id: 8, name: "Example Group", tags: [] }], context), "group", 8, "Example Group"],
    [renderTexts([{ id: 9, title: "Example Text", performers: [], tags: [], groups: [], files: [] }], context), "text", 9, "Example Text"],
    [renderSegments([{ id: 10, title: "Example Segment", hostType: "video", hostId: 1, startSec: 0, sourceKey: "user" }], context), "segment", 10, "Example Segment"],
  ];

  for (const [rendered, route, id, label] of cases) {
    const firstBodyLine = rendered.split("\n")[2]!;
    if (route === "video" || route === "audio" || route === "image" || route === "gallery" || route === "tag" || route === "performer" || route === "studio") {
      expect(firstBodyLine).toContain(label);
    } else {
      expect(firstBodyLine).toMatch(/^(TITLE|NAME)/);
      expect(firstBodyLine).not.toMatch(/(^|\s)ID(\s|$)/);
    }
    expect(rendered).toContain(`\u001b]8;;https://cove.example/base/${route}/${id}\u0007${label}`);
  }

  const search = renderGlobalSearch({ groups: [{ type: "video", items: [{ id: 11, title: "Search Result", subtitle: "Context" }] }], failedTypes: [] }, context);
  expect(search.split("\n")[2]).toMatch(/^TITLE/);
  expect(search.split("\n")[2]).not.toContain("ID");
  expect(search).toContain("\u001b]8;;https://cove.example/base/video/11\u0007Search Result");
});

test("plain tables retain fallback IDs while rich media lists prioritize content", () => {
  const table = renderGroups([{ id: 42, name: "Example Group", tags: [] }], { color: false, hyperlinks: false, server: "https://cove.example", terminalWidth: 80 });
  expect(table.split("\n")[2]).toMatch(/^NAME.*\bID$/);
  expect(table).toContain("42");

  const rendered = renderVideoResults("Videos", [{ id: 42, title: "Example Video", performers: [], files: [] }], { color: false, hyperlinks: false, server: "https://cove.example", terminalWidth: 80 });
  expect(rendered.split("\n")[2]).toBe("Example Video");
  expect(rendered.split("\n")[3]).toEndWith("  42");
  expect(Bun.stringWidth(rendered.split("\n")[3]!)).toBe(80);
  expect(rendered).not.toContain("#42");
  expect(rendered).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  expect(rendered).not.toContain("\u001b]8;;");

  const unsafeServer = renderVideoResults("Videos", [{ id: 42, title: "Example Video", performers: [], files: [] }], { color: false, hyperlinks: true, server: "https://user:secret@cove.example", terminalWidth: 80 });
  expect(unsafeServer.split("\n")[2]).toBe("Example Video");
  expect(unsafeServer.split("\n")[3]).toEndWith("  42");
  expect(Bun.stringWidth(unsafeServer.split("\n")[3]!)).toBe(80);
  expect(unsafeServer).not.toContain("\u001b]8;;");
  expect(unsafeServer).not.toContain("secret");
});

test("remaining list views use the shared compact borderless hierarchy", () => {
  const renderings = [
    renderGroups([{ id: 1, name: "Group", tags: [] }], { color: false, totalCount: 1 }),
    renderTexts([{ id: 1, title: "Text", performers: [], tags: [], groups: [], files: [] }], { color: false, totalCount: 1 }),
    renderSegments([{ id: 1, hostType: "video", hostId: 2, startSec: 1, sourceKey: "user" }], { color: false, totalCount: 1 }),
    renderSavedFilters([{ id: 1, mode: "videos", name: "Filter" }]),
  ];
  for (const rendered of renderings) {
    expect(rendered).toMatch(/\n\n(?:TITLE|NAME)/);
    expect(rendered).not.toMatch(/[┌┬┐├┼┤└┴┘│]/);
  }
  const profiles = renderProfiles([{ name: "personal", server: "https://cove.example", default: true, authentication: "session" }], { color: false });
  expect(profiles).toContain("\n\nPROFILE");
  expect(profiles).not.toMatch(/[┌┬┐├┼┤└┴┘│]/);
});

test("tag lists use group and usage-count cards", () => {
  const tag = {
    id: 9,
    name: "Example Tag",
    aliases: [],
    color: "#123456",
    tagGroupId: 8,
    tagGroupName: "Category",
    tagGroupColor: "#654321",
    videoCount: 120,
    imageCount: 34,
    galleryCount: 5,
    performerCount: 6,
    studioCount: 7,
  };
  const plain = renderTags([tag], { color: false, hyperlinks: false, terminalWidth: 100 });
  const lines = plain.split("\n");
  expect(lines[0]).toBe("Tags · 1 tag");
  expect(lines[2]).toStartWith("Example Tag");
  expect(lines[2]).toEndWith("Category");
  expect(lines[3]).toStartWith("120 videos · 34 images · 5 galleries · 6 performers · 7 studios");
  expect(lines[3]).toEndWith("  9");
  expect(plain).not.toMatch(/\n\nNAME\b/);
  for (const line of lines) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(100);

  const rich = renderTags([tag], { color: true, hyperlinks: true, server: "https://cove.example/base", terminalWidth: 100 });
  expect(rich).toContain("\u001b]8;;https://cove.example/base/tag/9\u0007Example Tag");
  expect(rich).toContain("\u001b[38;2;18;52;86m");
  expect(rich).toContain("\u001b[38;2;101;67;33mCategory");

  const accentFallback = renderTags([{ id: 3, name: "Plain Tag", aliases: [] }], { color: true, hyperlinks: false, terminalWidth: 80 });
  expect(accentFallback).toContain("\u001b[38;2;79;143;247mPlain Tag");

  const narrow = renderTags([tag], { color: false, hyperlinks: false, terminalWidth: 40 });
  expect(narrow).toContain("120 videos · 34 images · 5 galleries");
  expect(narrow).not.toContain("6 performers");
  for (const line of narrow.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(40);

  const defensive = renderTags([{ id: 2, name: "", aliases: [], color: 123 as unknown as string, tagGroupName: " \n ", tagGroupColor: {} as unknown as string, videoCount: -1, imageCount: Number.NaN, galleryCount: 1 }], { color: false, hyperlinks: false, terminalWidth: 80 });
  expect(defensive).toContain("Tag 2");
  expect(defensive).toContain("0 videos · 0 images · 1 gallery");
  expect(defensive).not.toContain("NaN");
});

test("tag and tag-group colors use Cove's server-provided hex values", () => {
  const rendered = renderTags([{ id: 1, name: "Color tag", aliases: [], color: null, tagGroupName: "Mood", tagGroupColor: "#12ab34" }], { color: true, totalCount: 1 });
  expect(rendered).toContain("\u001b[38;2;18;171;52m");
  expect(rendered).toContain("Color tag");
  expect(rendered).toContain("Mood");

  const detail = renderVideo({ id: 2, title: "Video", performers: [], files: [], tags: [{ id: 1, name: "Own color", color: "#ff8800", tagGroupColor: "#12ab34" }] }, { color: true, terminalWidth: 80 });
  expect(detail).toContain("\u001b[38;2;255;136;0m");
  expect(renderVideo({ id: 2, title: "Video", performers: [], files: [], tags: [{ id: 1, name: "Plain", tagGroupColor: "#12ab34" }] }, { color: false, terminalWidth: 80 })).not.toContain("\u001b[");
});

test("remaining detail views use sectioned summaries", () => {
  const audio = renderAudio({ id: 5, title: "Audio", date: "2026-01-01", studioName: "Studio", performers: [], tracks: [], files: [] }, { color: false, terminalWidth: 80 });
  const performer = renderPerformer({ id: 7, name: "Performer", aliases: [], videoCount: 2, tags: [] }, { color: false, terminalWidth: 80 });
  const catalog = renderCatalogDetail({ id: 9, name: "Group", tags: [], itemCount: 3 }, "group", { color: false, terminalWidth: 80 });
  for (const rendered of [audio, performer, catalog]) {
    expect(rendered).toContain("\n\nOverview\n");
    expect(rendered).toContain("\n\nLibrary\n");
  }
});

test("renders and sanitizes catalog detail fields", () => {
  const rendered = renderCatalogDetail({ id: 4, name: "Group\nName", tags: [{ id: 2, name: "Tag\u001b[2J" }], aliases: "Alias", description: "Description\nline", itemCount: 3 }, "group", { color: false, terminalWidth: 50 });
  expect(rendered).toContain("Group Name");
  expect(rendered).toContain("Description");
  expect(rendered).toContain("line");
  expect(rendered).toContain("Tag");
  expect(rendered).not.toContain("\u001b");
  for (const line of rendered.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);
});

test("renders the permission-protected image stream URL without dropping the server path", () => {
  const rendered = renderCatalogDetail({ id: 4, title: "Image", performers: [], files: [] }, "image", { color: false, terminalWidth: 100, server: "https://cove.example/base" });
  expect(rendered).toContain("Image URL");
  expect(rendered).toContain("https://cove.example/base/api/stream/image/4");
});

test("relative detail links never expose server URL credentials", () => {
  const context = { color: false, hyperlinks: true, server: "https://review-user:review-secret@cove.example/base", terminalWidth: 100 } as const;
  const video = renderVideo({ id: 4, title: "Video", performers: [], files: [], urls: ["related"], details: "See [related item](/related)." }, context);
  const image = renderCatalogDetail({ id: 5, title: "Image", performers: [], files: [] }, "image", context);

  for (const rendered of [video, image]) {
    expect(rendered).not.toContain("review-user");
    expect(rendered).not.toContain("review-secret");
    expect(rendered).not.toContain("\u001b]8;;");
  }
  expect(video).toContain("https://cove.example/base/related");
  expect(video).toContain("https://cove.example/related");
  expect(image).toContain("https://cove.example/base/api/stream/image/5");
});

test("audio lists mirror the video two-line card hierarchy", () => {
  const rendered = renderAudios(
    [{ id: 9, title: "A very long audio title ".repeat(12), date: "2026-01-01", studioId: 8, studioName: "Studio", performers: [{ id: 7, name: "Example Performer" }], tracks: [], files: [{ basename: "fallback.flac", duration: 65, format: "flac" }] }],
    { color: false, terminalWidth: 80 },
  );
  const lines = rendered.split("\n");
  expect(lines[0]).toBe("Audios · 1 audio");
  expect(lines[2]).toEndWith("Studio · 1:05 · 2026-01-01");
  expect(lines[3]).toStartWith("Example Performer");
  expect(lines[3]).toEndWith("  9");
  expect(rendered).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(80);

  const linked = renderAudios(
    [{ id: 9, title: "Title", studioId: 8, studioName: "Studio", performers: [{ id: 7, name: "Example Performer" }], tracks: [], files: [] }],
    { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 80 },
  );
  expect(linked).toContain("\u001b]8;;https://cove.example/base/audio/9\u0007Title\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/studio/8\u0007Studio\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/performer/7\u0007Example Performer\u001b]8;;\u0007");

  const narrow = renderAudios([{ id: 9, title: "A long title that must remain bounded", date: "2026-01-01", studioName: "A long studio name", performers: [{ id: 7, name: "A long performer name that must remain bounded" }], tracks: [], files: [{ duration: 65 }] }], { color: false, terminalWidth: 50 });
  for (const line of narrow.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);
});

test("image lists mirror the media two-line card hierarchy", () => {
  const rendered = renderImages(
    [{ id: 9, title: "A very long image title ".repeat(12), date: "2026-01-01", studioId: 8, studioName: "Studio", performers: [{ id: 7, name: "Example Performer" }], files: [{ basename: "fallback.webp", width: 2048, height: 1365 }] }],
    { color: false, terminalWidth: 80 },
  );
  const lines = rendered.split("\n");
  expect(lines[0]).toBe("Images · 1 image");
  expect(lines[2]).toEndWith("Studio · 2048×1365 · 2026-01-01");
  expect(lines[3]).toStartWith("Example Performer");
  expect(lines[3]).toEndWith("  9");
  expect(rendered).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(80);

  const linked = renderImages(
    [{ id: 9, title: "Title", studioId: 8, studioName: "Studio", performers: [{ id: 7, name: "Example Performer" }], files: [] }],
    { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 80 },
  );
  expect(linked).toContain("\u001b]8;;https://cove.example/base/image/9\u0007Title\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/studio/8\u0007Studio\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/performer/7\u0007Example Performer\u001b]8;;\u0007");

  const narrow = renderImages([{ id: 9, title: "A long title that must remain bounded", date: "2026-01-01", studioName: "A long studio name", performers: [{ id: 7, name: "A long performer name that must remain bounded" }], files: [{ width: 2048, height: 1365 }] }], { color: false, terminalWidth: 50 });
  expect(narrow.split("\n")[2]).toEndWith("2048×1365 · 2026-01-01");
  expect(narrow.split("\n")[2]).not.toContain("studio");
  for (const line of narrow.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);

  const fallbacks = renderImages([
    { id: 10, title: null, performers: [], files: [{ basename: "fallback.webp", width: Number.NaN, height: 1365 }] },
    { id: 11, title: null, performers: [], files: [] },
  ], { color: false, terminalWidth: 30 });
  expect(fallbacks.split("\n")[2]).toBe("fallback.webp");
  expect(fallbacks.split("\n")[4]).toBe("Image 11");
  expect(fallbacks).not.toContain("NaN×1365");
});

test("gallery lists mirror the media two-line card hierarchy", () => {
  const rendered = renderGalleries(
    [{ id: 9, title: "A very long gallery title ".repeat(12), date: "2026-01-01", studioId: 8, studioName: "Studio", imageCount: 24, performers: [{ id: 7, name: "Example Performer" }], files: [] }],
    { color: false, terminalWidth: 80 },
  );
  const lines = rendered.split("\n");
  expect(lines[0]).toBe("Galleries · 1 gallery");
  expect(lines[2]).toEndWith("Studio · 24 · 2026-01-01");
  expect(lines[3]).toStartWith("Example Performer");
  expect(lines[3]).toEndWith("  9");
  expect(rendered).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(80);

  const linked = renderGalleries(
    [{ id: 9, title: "Title", studioId: 8, studioName: "Studio", imageCount: 1, performers: [{ id: 7, name: "Example Performer" }], files: [] }],
    { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 80 },
  );
  expect(linked).toContain("\u001b]8;;https://cove.example/base/gallery/9\u0007Title\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/studio/8\u0007Studio\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/performer/7\u0007Example Performer\u001b]8;;\u0007");
  expect(stripTerminalSequences(linked).split("\n")[2]).toEndWith("Studio · 1");

  const narrow = renderGalleries([{ id: 9, title: "A long title that must remain bounded", date: "2026-01-01", studioName: "A long studio name", imageCount: 24, performers: [{ id: 7, name: "A long performer name that must remain bounded" }], files: [] }], { color: false, terminalWidth: 50 });
  expect(narrow.split("\n")[2]).toEndWith("24 · 2026-01-01");
  expect(narrow.split("\n")[2]).not.toContain("studio");
  for (const line of narrow.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);

  const fallbacks = renderGalleries([
    { id: 10, title: null, displayName: "fallback.zip", imageCount: -1, performers: [], files: [] },
    { id: 11, title: null, imageCount: 1.5, performers: [], files: [] },
  ], { color: false, terminalWidth: 30 });
  expect(fallbacks.split("\n")[2]).toBe("fallback.zip");
  expect(fallbacks.split("\n")[4]).toBe("Gallery 11");
  expect(fallbacks).not.toContain("-1");
  expect(fallbacks).not.toContain("1.5");
});

test("performer lists use demographic and library-count cards", () => {
  const performer = { id: 9, name: "Example Performer", aliases: [], country: "Finland", birthdate: "1990-01-02", tags: [{ id: 1, name: "First" }, { id: 2, name: "Second" }], videoCount: 120, imageCount: 34, galleryCount: 5, likeCount: 6 };
  const rendered = renderPerformers([performer], { color: false, hyperlinks: false, terminalWidth: 100 });
  const lines = rendered.split("\n");
  expect(lines[0]).toBe("Performers · 1 performer");
  expect(lines[2]).toStartWith("Example Performer");
  expect(lines[2]).toEndWith("Finland · 1990-01-02");
  expect(lines[3]).toStartWith("2 tags · 120 videos · 34 images · 5 galleries · 6 likes");
  expect(lines[3]).toEndWith("  9");
  expect(rendered).not.toMatch(/\n\n(?:ID|NAME)\b/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(100);

  const linked = renderPerformers([performer], { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 100 });
  expect(linked).toContain("\u001b]8;;https://cove.example/base/performer/9\u0007Example Performer\u001b]8;;\u0007");
  expect(stripTerminalSequences(linked).split("\n")[3]).toBe("2 tags · 120 videos · 34 images · 5 galleries · 6 likes");

  const narrow = renderPerformers([performer], { color: false, hyperlinks: false, terminalWidth: 40 });
  expect(narrow.split("\n")[2]).toEndWith("1990-01-02");
  expect(narrow.split("\n")[2]).not.toContain("Finland");
  expect(narrow.split("\n")[3]).toStartWith("2 tags · 120 videos");
  expect(narrow.split("\n")[3]).not.toContain("galleries");
  for (const line of narrow.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(40);

  const defensive = renderPerformers([
    { ...performer, id: 10, disambiguation: " \n ", tags: [{ id: 1, name: "Only" }], videoCount: 1, imageCount: 0, galleryCount: -1, likeCount: 1.5 },
    { ...performer, id: 11, disambiguation: { malformed: true } as unknown as string },
  ], { color: false, hyperlinks: true, server: "https://cove.example", terminalWidth: 100 });
  const defensiveLines = stripTerminalSequences(defensive).split("\n");
  expect(defensiveLines[2]).toStartWith("Example Performer");
  expect(defensiveLines[2]).not.toContain("()");
  expect(defensiveLines[3]).toBe("1 tag · 1 video · 0 images · 0 galleries · 0 likes");
  expect(defensiveLines[4]).toStartWith("Example Performer");
  expect(defensiveLines[4]).not.toContain("(");
});

test("studio lists use parent and library-count cards", () => {
  const studio = { id: 9, name: "Example Studio", parentId: 8, parentName: "Parent Studio", aliases: [], tags: [{ id: 1, name: "First" }, { id: 2, name: "Second" }], videoCount: 120, imageCount: 34, galleryCount: 5, childStudioCount: 6 };
  const rendered = renderStudios([studio], { color: false, hyperlinks: false, terminalWidth: 100 });
  const lines = rendered.split("\n");
  expect(lines[0]).toBe("Studios · 1 studio");
  expect(lines[2]).toStartWith("Example Studio");
  expect(lines[2]).toEndWith("Parent Studio");
  expect(lines[3]).toStartWith("2 tags · 120 videos · 34 images · 5 galleries · 6 children");
  expect(lines[3]).toEndWith("  9");
  expect(rendered).not.toMatch(/\n\n(?:ID|NAME)\b/);
  for (const line of rendered.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(100);

  const linked = renderStudios([studio], { color: "#3bbd83", hyperlinks: true, server: "https://cove.example/base", terminalWidth: 100 });
  expect(linked).toContain("\u001b]8;;https://cove.example/base/studio/9\u0007Example Studio\u001b]8;;\u0007");
  expect(linked).toContain("\u001b]8;;https://cove.example/base/studio/8\u0007Parent Studio\u001b]8;;\u0007");
  expect(stripTerminalSequences(linked).split("\n")[3]).toBe("2 tags · 120 videos · 34 images · 5 galleries · 6 children");

  const narrow = renderStudios([studio], { color: false, hyperlinks: false, terminalWidth: 40 });
  expect(narrow.split("\n")[2]).toEndWith("Parent Studio");
  expect(narrow.split("\n")[3]).toStartWith("2 tags · 120 videos");
  expect(narrow.split("\n")[3]).not.toContain("galleries");
  for (const line of narrow.split("\n").slice(1)) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(40);

  const defensive = renderStudios([{ ...studio, parentId: -1, childStudioCount: 1 }], { color: false, hyperlinks: true, server: "https://cove.example", terminalWidth: 100 });
  expect(defensive).toContain("Parent Studio");
  expect(defensive).not.toContain("/studio/-1");
  expect(stripTerminalSequences(defensive).split("\n")[3]).toEndWith("1 child");
});

test("renders performer details and sanitizes free text", () => {
  const rendered = renderPerformer({
    id: 7, name: "Example", disambiguation: "Variant", aliases: ["Alias"], videoCount: 4, imageCount: 3,
    tags: [{ id: 2, name: "Featured" }], details: `Line one\n${"Line two ".repeat(12)}\u0000`, urls: ["https://example.test/profile"],
    deathDate: "2026-01-01", ethnicity: "Example", eyeColor: "Blue", hairColor: "Black", heightCm: 170, weight: 60,
    measurements: "Example", careerStart: "2020", careerEnd: "2025", tattoos: "Example", piercings: "Example",
    groupCount: 2, audioCount: 1, textCount: 5, faceCount: 6, likeCount: 7, remoteIds: [{ endpoint: "example", remoteId: "abc" }],
    customFields: { example: true }, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
  }, { color: false, terminalWidth: 50 });
  expect(rendered).toContain("Example (Variant)");
  expect(rendered).toContain("Featured");
  expect(rendered).toContain("Line one\n");
  expect(rendered).toContain("Line two Line two");
  expect(rendered).not.toContain("\u0000");
  expect(rendered).toContain("Death date");
  expect(rendered).toContain("Custom fields");
  for (const line of rendered.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);
});

test("performer remote IDs use friendly metadata labels and entity links", () => {
  const rendered = renderPerformer({
    id: 7,
    name: "Example Performer",
    aliases: [],
    tags: [{ id: 2, name: "Featured" }],
    urls: ["https://outside.example/profile"],
    remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-123" }],
  }, { color: false, terminalWidth: 100, server: "https://cove.example", hyperlinks: true, metadataServers: [{ endpoint: "https://metadata.example/graphql/", name: "Friendly Catalog" }] });

  expect(rendered).toContain("\u001b]8;;https://cove.example/performer/7\u0007Example Performer");
  expect(rendered).toContain("\u001b]8;;https://cove.example/tag/2\u0007Featured");
  expect(rendered).toContain("\u001b]8;;https://outside.example/profile\u0007");
  expect(rendered).toContain("Friendly Catalog · remote-123");
  expect(rendered).toContain("\u001b]8;;https://metadata.example/performers/remote-123\u0007");
  expect(rendered).not.toContain('"endpoint"');
  expect(rendered).not.toContain('"remoteId"');
});

test("entity detail headings link to their Cove pages", () => {
  const audio = renderAudio({ id: 5, title: "Example Audio", performers: [], tracks: [], files: [] }, { color: false, terminalWidth: 80, server: "https://cove.example", hyperlinks: true });
  const group = renderCatalogDetail({ id: 9, name: "Example Group", tags: [] }, "group", { color: false, terminalWidth: 80, server: "https://cove.example", hyperlinks: true });

  expect(audio).toContain("\u001b]8;;https://cove.example/audio/5\u0007Example Audio");
  expect(group).toContain("\u001b]8;;https://cove.example/group/9\u0007Example Group");
});

test("renders bounded video and audio detail views", () => {
  const video = renderVideo({ id: 4, title: "Video", details: "First paragraph\n\nSee [related post](/posts/8).", captions: "Caption", tags: [{ id: 2, name: "Featured" }], performers: [{ id: 7, name: "Example" }], groups: [{ id: 8, name: "A readable group", videoIndex: 2 }], galleries: [{ id: 9, title: "Gallery" }], remoteIds: [{ endpoint: "example", remoteId: "v4" }], parentVideoId: 1, parentVideoTitle: "Parent", clipStartSec: 2, clipEndSec: 8, childVideoCount: 3, urls: ["/videos/4", "https://outside.example/item"], files: [{ basename: "video.mp4", path: "/example/video.mp4", duration: 65, width: 1920, height: 1080, videoCodec: "h264", audioCodec: "aac", frameRate: 30, bitRate: 1000, size: 2000, fingerprints: [] }] }, { color: false, terminalWidth: 50, server: "https://cove.example" });
  const audio = renderAudio({ id: 5, title: "Audio", details: "Line one\nLine two", tags: [{ id: 3, name: "Favorite" }], performers: [{ id: 7, name: "Example" }], groups: [{ id: 8, name: "Group" }], fileCount: 1, maxDuration: 90, hasVideoFiles: false, tracks: [{ id: 1, orderIndex: 0, title: "Track", startSec: 0, endSec: 90 }], urls: ["/audios/5"], files: [{ basename: "audio.flac", path: "/example/audio.flac", duration: 90, format: "flac", audioCodec: "flac", bitRate: 1000, sampleRate: 48000, channels: 2, size: 2000, hasVideoTrack: false }] }, { color: false, terminalWidth: 50, server: "https://cove.example" });
  expect(video).toContain("Featured");
  expect(video).toContain("video.mp4");
  expect(audio).toContain("Favorite");
  expect(audio).toContain("audio.flac");
  expect(video).toContain("Parent video");
  expect(audio).toContain("Max duration");
  expect(video).toMatch(/Tags\s+Featured/);
  expect(video).not.toContain("Featured (#2)");
  expect(video).toMatch(/Groups\s+A readable group \(index 2\)/);
  expect(video).not.toContain("\"videoIndex\"");
  expect(video).toMatch(/Files\s+\/example\/video\.mp4/);
  expect(video).not.toContain("\"audioCodec\"");
  expect(video).toContain("https://cove.example/videos/4");
  expect(video).toContain("outside.example/item");
  expect(audio).toContain("https://cove.example/audios/5");
  expect(video).toContain("First paragraph");
  expect(video).toContain("See related post\n");
  expect(video).toContain("(https://cove.example/posts/8).");
  expect(audio).toMatch(/Line one\n\s+Line two/);
  for (const rendered of [video, audio]) for (const line of rendered.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(50);

  const linked = renderVideo({ id: 4, title: "Video", details: "See [related post](/posts/8).", performers: [], files: [] }, { color: false, terminalWidth: 50, server: "https://cove.example", hyperlinks: true });
  expect(linked).toContain("\u001b]8;;https://cove.example/posts/8\u0007related post\u001b]8;;\u0007");
  expect(linked).not.toContain("related post\u001b]8;;\u0007 .");

  const linkedEntities = renderVideo({
    id: 4,
    title: "Linked video",
    performers: [{ id: 7, name: "Example performer" }],
    tags: [{ id: 2, name: "Featured", tagGroupColor: "#3bbd83" }],
    galleries: [{ id: 9, title: "Example gallery" }],
    remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-123" }],
    files: [],
  }, { color: false, terminalWidth: 80, server: "https://cove.example/base", hyperlinks: true, metadataServers: [
    { endpoint: "https://metadata.example/graphql/", name: "Friendly Catalog" },
  ] });
  expect(linkedEntities).toContain("\u001b]8;;https://cove.example/base/video/4\u0007Linked video\u001b]8;;\u0007");
  expect(linkedEntities).toContain("\u001b]8;;https://cove.example/base/performer/7\u0007Example performer\u001b]8;;\u0007");
  expect(linkedEntities).toContain("\u001b]8;;https://cove.example/base/tag/2\u0007Featured\u001b]8;;\u0007");
  expect(linkedEntities).toContain("\u001b]8;;https://cove.example/base/gallery/9\u0007Example gallery\u001b]8;;\u0007");
  expect(linkedEntities).toContain("\u001b]8;;https://metadata.example/scenes/remote-123\u0007Friendly Catalog · remote-123\u001b]8;;\u0007");
  expect(linkedEntities).not.toContain('"endpoint"');

  const plainEntities = renderVideo({
    id: 4,
    title: "Plain video",
    performers: [],
    remoteIds: [{ endpoint: "https://www.metadata.example/graphql", remoteId: "remote-123" }],
    files: [],
  }, { color: false, terminalWidth: 80, server: "https://cove.example", hyperlinks: false });
  expect(plainEntities).toContain("metadata.example · remote-123");
  expect(plainEntities).not.toContain("\u001b]8;;");
  expect(plainEntities).not.toContain('"remoteId"');

  const credentialEndpoint = renderVideo({
    id: 4,
    title: "Safe video",
    performers: [],
    remoteIds: [{ endpoint: "https://review-user:review-secret@metadata.example/graphql?token=hidden", remoteId: "remote-123" }],
    files: [],
  }, { color: false, terminalWidth: 80, server: "https://cove.example", hyperlinks: true });
  expect(credentialEndpoint).toContain("metadata.example · remote-123");
  expect(credentialEndpoint).not.toContain("review-user");
  expect(credentialEndpoint).not.toContain("review-secret");
  expect(credentialEndpoint).not.toContain("token=hidden");

  const malformedEndpoint = renderVideo({
    id: 4,
    title: "Safe video",
    performers: [],
    remoteIds: [{ endpoint: "metadata.example/graphql?api_key=review-secret", remoteId: "remote-123" }],
    files: [],
  }, { color: false, terminalWidth: 80, server: "https://cove.example", hyperlinks: true });
  expect(malformedEndpoint).toContain("metadata server · remote-123");
  expect(malformedEndpoint).not.toContain("api_key");
  expect(malformedEndpoint).not.toContain("review-secret");

  const hardened = renderVideo({ id: 4, title: "Video", details: "[safe\u001b[2Jspoof](child/with/a/very/long/path) [blocked](javascript:alert(1))", performers: [], files: [] }, { color: false, terminalWidth: 30, server: "https://cove.example/subpath", hyperlinks: true });
  expect(hardened).toContain("\u001b]8;;https://cove.example/subpath/child/with/a/very/long/path\u0007safespoof\u001b]8;;\u0007");
  expect(hardened).not.toContain("\u001b[2J");
  expect(hardened).not.toContain("\u001b]8;;javascript:");
  expect(hardened).toMatch(/blocked\s+\(javascript:alert\(\s*1\)\)/);
  for (const line of hardened.split("\n")) expect(Bun.stringWidth(line.replace(/\u001b]8;;[^\u0007]*\u0007|\u001b]8;;\u0007/g, ""))).toBeLessThanOrEqual(30);

  const injected = renderVideo({ id: 4, title: "Video", details: "before \u001b]8;;file:///etc/passwd\u0007open local file\u001b]8;;\u0007 after", performers: [], files: [] }, { color: false, terminalWidth: 50, server: "https://cove.example", hyperlinks: true });
  expect(injected).not.toContain("file:///etc/passwd");
  expect(injected).toContain("before open local file after");

  const injectedFields = renderVideo({ id: 4, title: "Title \u001b]8;;file:///etc/passwd\u0007open\u001b]8;;\u0007", captions: "Caption \u001b]8;;file:///tmp/x\u0007local\u001b]8;;\u0007", tags: [{ id: 1, name: "Tag \u001b]8;;file:///tmp/y\u0007local\u001b]8;;\u0007" }], performers: [], files: [] }, { color: false, terminalWidth: 50, server: "https://cove.example", hyperlinks: true });
  expect(injectedFields).not.toContain("file:///etc/passwd");
  expect(injectedFields).not.toContain("file:///tmp/");
  expect(injectedFields).toContain("Title open");
  expect(injectedFields).toContain("Caption local");
  expect(injectedFields).toContain("Tag local");
});

test("saved-filter tables tolerate invalid JSON shapes and wide Unicode", () => {
  const rendered = renderSavedFilters([
    { id: 1, mode: "videos", name: "Wide 🎬 名称", findFilter: "null", objectFilter: "42" },
    { id: 2, mode: "videos", name: "Malformed", findFilter: "{", objectFilter: "{}" },
  ]);
  expect(rendered).toContain("Wide 🎬 名称");
  expect(rendered).toContain("invalid JSON");
  expect(renderSavedFilters([{ id: 3, mode: "performers", name: "Recent" }], { terminalWidth: 100 }, "Saved performer filters")).toContain("latest_video_date desc");
  for (const line of rendered.split("\n")) expect(Bun.stringWidth(line)).toBeLessThanOrEqual(100);
});

test("list headings disclose an automatically applied default filter", () => {
  const filterSummary = 'Default filter · sort=random desc · favoriteCriterion={"value":true}';
  const rendered = renderPerformers([{ id: 1, name: "Example", aliases: [], tags: [] }], { totalCount: 124, listPosition: { offset: 0 }, defaultFilterApplied: true, appliedFilterSummary: filterSummary, color: false });
  expect(rendered).toStartWith(`Performers\n${filterSummary}\n\n1-1 of 124`);

  const paged = renderPerformers([{ id: 1, name: "Example", aliases: [], tags: [] }], { totalCount: 124, listPosition: { offset: 25, page: 2, perPage: 25 }, defaultFilterApplied: true, appliedFilterSummary: filterSummary, color: false });
  expect(paged).toStartWith(`Performers\n${filterSummary}\n\n26-26 of 124 · Page 2/5`);
  expect(paged).toEndWith("26-26 of 124 · Page 2/5");

  const empty = renderPerformers([], { totalCount: 0, listPosition: { offset: 0 }, defaultFilterApplied: true, appliedFilterSummary: filterSummary, color: false });
  expect(empty).toStartWith(`Performers\n${filterSummary}\n\n0 performers`);

  const emptyVideos = renderVideoResults("Videos", [], { totalCount: 0, listPosition: { offset: 0 }, defaultFilterApplied: true, appliedFilterSummary: filterSummary, color: false });
  expect(emptyVideos).toStartWith(`Videos\n${filterSummary}\n\n0 matches`);
});
