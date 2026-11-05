import { describe, expect, it } from "vitest";
import {
  normalizeExtensionFeatureGuide,
  resolveExtensionGuideImage,
  toInAppRoute,
} from "../components/featureGuideContent";
import blockCases from "./fixtures/extension-guide-blocks.json";

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

describe("extension guide blocks shared with the host loader", () => {
  // The host lists a guide only when it has a block this normalizer keeps, so a guide it lists never
  // renders as nothing. ExtensionFeatureGuideTests checks the same cases against the host.
  it.each(blockCases.cases)("$name", ({ block, renderable }) => {
    const normalized = normalizeExtensionFeatureGuide({
      extensionId,
      path: blockCases.guidePath,
      guide: {
        schemaVersion: 1,
        id: "block-case",
        title: "Block case",
        description: "One block.",
        order: 1,
        sections: [{ id: "only", title: "Only section", blocks: [block] }],
      },
    });
    expect(normalized !== null).toBe(renderable);
  });

  it.each(blockCases.sectionIdCases.map(({ id, valid }) => [id, valid] as const))("section id %j", (id, valid) => {
    const normalized = normalizeExtensionFeatureGuide({
      extensionId,
      path: blockCases.guidePath,
      guide: {
        schemaVersion: 1,
        id: "section-case",
        title: "Section case",
        description: "One section.",
        order: 1,
        sections: [{ id, title: "Only section", blocks: [{ type: "paragraph", text: "Body." }] }],
      },
    });
    expect(normalized !== null).toBe(valid);
  });
});

describe("repeated IDs in extension guides", () => {
  it("gives repeated section and recipe IDs a numbered suffix instead of dropping them", () => {
    const paragraph = { type: "paragraph", text: "Body." };
    const normalized = normalizeExtensionFeatureGuide(
      guide([
        { id: "setup", title: "First", blocks: [paragraph] },
        { id: "setup", title: "Second", blocks: [paragraph] },
        { id: "setup-2", title: "Third", blocks: [paragraph] },
        {
          id: "recipes",
          title: "Recipes",
          blocks: [
            {
              type: "recipes",
              items: [
                { id: "tip", title: "Tip one", blocks: [paragraph] },
                { id: "tip", title: "Tip two", blocks: [paragraph] },
              ],
            },
          ],
        },
      ]),
    );

    expect(normalized?.sections.map((section) => `${section.id}:${section.title}`)).toEqual([
      "setup:First",
      "setup-3:Second",
      "setup-2:Third",
      "recipes:Recipes",
    ]);
    const recipes = normalized?.sections[3].blocks[0];
    expect(recipes?.type === "recipes" ? recipes.items.map((recipe) => recipe.id) : []).toEqual(["tip", "tip-2"]);
  });
});
