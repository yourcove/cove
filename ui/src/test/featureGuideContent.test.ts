import { describe, expect, it } from "vitest";
import {
  normalizeExtensionFeatureGuide,
  resolveExtensionGuideImage,
  toInAppRoute,
} from "../components/featureGuideContent";

const extensionId = "com.example.guides";

function guide(sections: unknown, overrides: Record<string, unknown> = {}) {
  return {
    extensionId,
    path: "docs/guides/overview.json",
    guide: {
      schemaVersion: 1,
      id: "example-overview",
      title: "Example overview",
      description: "What the example does.",
      order: 40,
      sections,
      ...overrides,
    },
  };
}

describe("normalizeExtensionFeatureGuide", () => {
  it("keeps every block type the shared schema defines", () => {
    const blocks = [
      { type: "paragraph", text: "Paragraph" },
      { type: "note", text: "Note" },
      { type: "steps", items: ["One", "Two"] },
      { type: "heading", level: 3, text: "Heading" },
      { type: "list", items: ["Item"] },
      {
        type: "links",
        items: [{ label: "Docs", href: "https://example.com/docs", appHref: "/settings", topicId: "extensions" }],
      },
      { type: "image", src: "../images/overview.webp", alt: "Overview", caption: "Caption" },
      { type: "table", caption: "Table", columns: ["A", "B"], rows: [["1", "2"]] },
      { type: "code", language: "json", lines: ["{}"] },
      {
        type: "recipes",
        items: [
          { id: "first-recipe", title: "Recipe", description: "Why", blocks: [{ type: "paragraph", text: "Inside" }] },
        ],
      },
    ];

    const normalized = normalizeExtensionFeatureGuide(guide([{ id: "start", title: "Start", blocks }]));

    expect(normalized?.sections[0].blocks).toEqual([
      ...blocks.slice(0, 6),
      {
        type: "image",
        src: "/api/extensions/assets/com.example.guides/docs/images/overview.webp",
        alt: "Overview",
        caption: "Caption",
      },
      ...blocks.slice(7),
    ]);
    expect(normalized).toMatchObject({ id: "example-overview", title: "Example overview", order: 40 });
  });

  it("drops malformed and unsafe blocks instead of rejecting the guide", () => {
    const normalized = normalizeExtensionFeatureGuide(
      guide([
        {
          id: "start",
          title: "Start",
          blocks: [
            { type: "paragraph", text: "Kept" },
            { type: "steps", items: "not a list" },
            { type: "heading", level: 2, text: "Wrong level" },
            { type: "video", src: "clip.mp4" },
            { type: "links", items: [{ label: "Script", href: "javascript:alert(1)" }] },
            { type: "links", items: [{ label: "Escape", href: "https://example.com", appHref: "//evil.example" }] },
            { type: "image", src: "../../../outside.png", alt: "Outside" },
            { type: "image", src: "http://example.com/plain.png", alt: "Plain HTTP" },
            { type: "table", columns: ["A", "B"], rows: [["only one"]] },
            { type: "recipes", items: [{ id: "nested", title: "Nested", blocks: [{ type: "recipes", items: [] }] }] },
          ],
        },
        { id: "empty", title: "Nothing renderable", blocks: [{ type: "unknown" }] },
      ]),
    );

    expect(normalized?.sections).toEqual([
      {
        id: "start",
        title: "Start",
        blocks: [
          { type: "paragraph", text: "Kept" },
          { type: "links", items: [{ label: "Escape", href: "https://example.com" }] },
        ],
      },
    ]);
  });

  it.each([
    ["a newer schema version", { schemaVersion: 2 }],
    ["a non-slug id", { id: "Not A Slug" }],
    ["a missing title", { title: "" }],
    ["a missing order", { order: undefined }],
  ])("rejects a guide with %s", (_, overrides) => {
    expect(
      normalizeExtensionFeatureGuide(
        guide([{ id: "s", title: "S", blocks: [{ type: "paragraph", text: "x" }] }], overrides),
      ),
    ).toBeNull();
  });

  it("rejects a guide with no renderable sections", () => {
    expect(normalizeExtensionFeatureGuide(guide([{ id: "s", title: "S", blocks: [{ type: "unknown" }] }]))).toBeNull();
  });
});

describe("toInAppRoute", () => {
  it.each([
    ["/settings/extensions", "/settings/extensions"],
    ["/videos?sort=date#top", "/videos?sort=date#top"],
    ["/a/../settings", "/a/../settings"],
  ])("keeps the Cove route %s", (href, expected) => {
    expect(toInAppRoute(href)).toBe(expected);
  });

  it.each([
    "//evil.example",
    "/..//evil.example/x",
    "/.//evil.example",
    "/a/..//evil.example",
    "/%2e%2e//evil.example",
    "/settings/../..//evil.example",
    "/\\evil.example",
    "/\t/evil.example",
    "/\n/evil.example",
    "https://evil.example",
    "settings",
    "",
  ])("rejects %j, which would leave Cove", (href) => {
    expect(toInAppRoute(href)).toBeUndefined();
  });
});

describe("resolveExtensionGuideImage", () => {
  it.each([
    ["overview.png", "/api/extensions/assets/com.example.guides/docs/guides/overview.png"],
    ["./shots/a b.png", "/api/extensions/assets/com.example.guides/docs/guides/shots/a%20b.png"],
    ["../../root.png", "/api/extensions/assets/com.example.guides/root.png"],
    ["https://example.com/a.png", "https://example.com/a.png"],
    ["data:image/png;base64,AAAA", "data:image/png;base64,AAAA"],
    ["../../../outside.png", undefined],
    ["/api/extensions/assets/other/a.png", undefined],
    ["javascript:alert(1)", undefined],
    ["http://example.com/a.png", undefined],
  ])("resolves %s", (source, expected) => {
    expect(resolveExtensionGuideImage(extensionId, "docs/guides/overview.json", source)).toBe(expected);
  });
});
