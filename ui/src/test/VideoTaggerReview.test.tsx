import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { MetadataServerVideoMatch, Video } from "../api/types";
import {
  applyTaggerSelectionChange,
  buildTaggerReview,
  isHandEdited,
  modeForSelection,
  selectorChange,
  type ReviewItem,
  type TaggerRelationshipEdits,
  type TaggerReviewInput,
} from "../components/VideoTaggerReview";
import { MetadataDiff, defaultDiffSelection, scalarStatus, summarizeDiff } from "../components/MetadataDiff";
import { MetadataDiffSummary } from "../components/MetadataDiffSummary";

vi.mock("../api/client", () => ({ videos: { screenshotUrl: (id: number) => `/cover/${id}` } }));
// The library pickers search through the API; here the link panel's picker just offers one tag.
vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: () => null,
  EntityReferenceSelector: ({
    onChange,
  }: {
    onChange: (id: number, option: { id: number; label: string }) => void;
  }) => (
    <button type="button" onClick={() => onChange(7, { id: 7, label: "Edging" })}>
      Pick Edging
    </button>
  ),
}));

const video: Video = {
  id: 5,
  title: "Local title",
  organized: false,
  urls: ["https://kept.example/a"],
  tags: [{ id: 1, name: "Old tag" }],
  performers: [{ id: 9, name: "Known Performer" }],
  files: [],
  galleries: [],
  remoteIds: [],
  groups: [],
  createdAt: "",
  updatedAt: "",
  fieldProvenance: [{ fieldKey: "title", sourceKey: "user", createdAt: "" }],
} as unknown as Video;

const result = {
  id: "r1",
  endpoint: "https://stash.example/graphql",
  serverName: "StashDB",
  title: "Scraped title",
  code: "SC-1",
  details: undefined,
  director: undefined,
  date: "2024-01-02",
  imageUrl: "https://stash.example/cover.jpg",
  urls: ["https://stash.example/scene/1"],
  studioName: "Studio X",
  tagNames: ["Existing tag", "Library tag", "Brand new tag"],
  performerNames: [],
  performerCandidates: [
    { remoteId: "p-known", name: "Known Performer", existsLocally: true, localId: 9 },
    { remoteId: "p-new", name: "Newcomer", existsLocally: false },
  ],
  tagCandidates: [
    { remoteId: "t1", name: "Existing tag", existsLocally: true },
    { remoteId: "t2", name: "Library tag", existsLocally: true },
    { remoteId: "t3", name: "Brand new tag", existsLocally: false },
  ],
  fingerprints: [],
  fingerprintAlgorithms: [],
  matchCount: 0,
} as unknown as MetadataServerVideoMatch;

const input = (overrides: Partial<TaggerReviewInput> = {}): TaggerReviewInput => ({
  video,
  result,
  sourceName: "StashDB",
  fieldStrategies: { title: "ignore", code: "overwrite", date: "overwrite" },
  imageReplace: true,
  collectionModes: { urls: "merge", tags: "merge", performers: "merge", studio: "replace" },
  showStudio: true,
  showTags: true,
  showPerformers: true,
  currentTagNames: ["Old tag"],
  existingTagNames: ["Existing tag", "Library tag"],
  tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "create" },
  performerChoices: [
    { key: "remote-performer:p-known", label: "Known Performer", candidate: result.performerCandidates[0] },
    { key: "remote-performer:p-new", label: "Newcomer", candidate: result.performerCandidates[1] },
  ],
  currentPerformerChoiceKeys: ["remote-performer:p-known"],
  performerActions: { "remote-performer:p-known": "include", "remote-performer:p-new": "create" },
  ...overrides,
});

describe("VideoTaggerReview", () => {
  it("builds review rows from the scraped fields and reads the tagger's strategies as the selection", () => {
    const review = buildTaggerReview(input());
    expect(review.fields.map((field) => field.key)).toEqual([
      "title",
      "code",
      "date",
      "image",
      "studio",
      "urls",
      "performers",
      "tags",
    ]);
    expect(review.source.label).toBe("From StashDB");
    expect(review.target.provenance?.title).toBe("Edited by you");
    expect(review.selection).toMatchObject({
      title: "target",
      code: "source",
      date: "source",
      image: "source",
      studio: "source",
    });
    expect(review.selection.urls).toEqual(["https://kept.example/a", "https://stash.example/scene/1"]);
    expect([...(review.selection.tags as string[])].sort()).toEqual([
      "brand new tag",
      "existing tag",
      "library tag",
      "old tag",
    ]);
    expect([...(review.selection.performers as string[])].sort()).toEqual([
      "remote-performer:p-known",
      "remote-performer:p-new",
    ]);
    const tags = review.fields.find((field) => field.key === "tags")!;
    const newTag = (review.source.values.tags as unknown[]).find((tag) => tags.itemLabel!(tag) === "Brand new tag");
    expect(tags.itemIsNew!(newTag)).toBe(true);
    const summary = summarizeDiff(review.fields, review.source, review.target, review.selection);
    expect(summary.changes.map((change) => change.text).join(" | ")).toMatch(/Code, Date, Studio filled from StashDB/);
    expect(summary.changes.map((change) => change.text).join(" | ")).toMatch(/Title kept from current \(conflict\)/);
  });

  it("keeps a hand-edited field by default and lets the generic default agree", () => {
    expect(isHandEdited(video, "title")).toBe(true);
    expect(isHandEdited(video, "code")).toBe(false);
    const review = buildTaggerReview(input({ fieldStrategies: {} }));
    const generic = defaultDiffSelection(review.fields, review.source, review.target);
    expect(generic.title).toBe("target");
    expect(generic.code).toBe("source");
  });

  it("maps scalar and studio choices back to strategies and modes", () => {
    const handlers = { onFieldStrategyChange: vi.fn(), onCollectionModeChange: vi.fn() };
    const review = buildTaggerReview(input());
    applyTaggerSelectionChange(
      input(),
      review.selection,
      { ...review.selection, title: "source", image: "target", studio: "target" },
      handlers,
    );
    expect(handlers.onFieldStrategyChange).toHaveBeenCalledWith("title", "overwrite");
    expect(handlers.onFieldStrategyChange).toHaveBeenCalledWith("image", "ignore");
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("studio", "skip");
  });

  it("derives a collection mode from the presets and toggles single incoming items", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onTogglePerformer: vi.fn() };
    const review = buildTaggerReview(input());
    // "Only current" for URLs: nothing incoming selected.
    applyTaggerSelectionChange(
      input(),
      review.selection,
      { ...review.selection, urls: ["https://kept.example/a"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("urls", "skip");
    // "Only incoming" for tags: current items dropped, mode becomes replace, no item toggles.
    applyTaggerSelectionChange(
      input(),
      review.selection,
      { ...review.selection, tags: ["existing tag", "library tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("tags", "replace");
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
    // Dropping one incoming tag toggles just that tag by name.
    applyTaggerSelectionChange(
      input(),
      review.selection,
      { ...review.selection, tags: ["old tag", "existing tag", "library tag"] },
      handlers,
    );
    expect(handlers.onToggleTag).toHaveBeenCalledTimes(1);
    expect(handlers.onToggleTag).toHaveBeenCalledWith(["Brand new tag"]);
    // Performers toggle by their choice key.
    applyTaggerSelectionChange(
      input(),
      review.selection,
      { ...review.selection, performers: ["remote-performer:p-known"] },
      handlers,
    );
    expect(handlers.onTogglePerformer).toHaveBeenCalledWith(["remote-performer:p-new"]);
  });

  it("recognises the presets even when a scraped tag is already current", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn() };
    // "Existing tag" is on the video and in the result: one id on both sides.
    const overlapping = input({ currentTagNames: ["Old tag", "Existing tag"] });
    const review = buildTaggerReview(overlapping);
    expect([...(review.selection.tags as string[])].sort()).toEqual([
      "brand new tag",
      "existing tag",
      "library tag",
      "old tag",
    ]);
    // Only StashDB: the current-only tag goes, the shared one stays; that is replace, with no toggles.
    applyTaggerSelectionChange(
      overlapping,
      review.selection,
      { ...review.selection, tags: ["existing tag", "library tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenLastCalledWith("tags", "replace");
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
    // Only current: the shared tag remains selected yet the incoming side is off.
    applyTaggerSelectionChange(
      overlapping,
      review.selection,
      { ...review.selection, tags: ["old tag", "existing tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenLastCalledWith("tags", "skip");
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
  });

  it("toggles every changed item of one action in a single call", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn() };
    const excluded = input({
      tagActions: { "existing tag": "include", "library tag": "exclude", "brand new tag": "exclude" },
    });
    const review = buildTaggerReview(excluded);
    expect(review.selection.tags).toEqual(["old tag", "existing tag"]);
    // Combine brings both excluded tags back at once.
    applyTaggerSelectionChange(
      excluded,
      review.selection,
      { ...review.selection, tags: ["old tag", "existing tag", "library tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
    expect(handlers.onToggleTag).toHaveBeenCalledTimes(1);
    expect(handlers.onToggleTag.mock.calls[0][0].sort()).toEqual(["Brand new tag", "Library tag"]);
  });

  it("lists a performer matched by name once and hides chip toggles while a collection is skipped", () => {
    const byName = input({
      performerChoices: [
        {
          key: "remote-performer:Known Performer",
          label: "Known Performer",
          candidate: { remoteId: "Known Performer", name: "Known Performer", existsLocally: true },
        },
      ],
      currentPerformerChoiceKeys: ["remote-performer:Known Performer"],
      performerActions: { "remote-performer:known performer": "include" },
      collectionModes: { tags: "skip", performers: "merge" },
    });
    const review = buildTaggerReview(byName);
    expect((review.target.values.performers as unknown[]).length).toBe(1);
    expect(review.selection.performers).toEqual(["remote-performer:known performer"]);
    expect(review.fields.find((field) => field.key === "tags")?.modesOnly).toBe(true);
    expect(review.fields.find((field) => field.key === "performers")?.modesOnly).toBeFalsy();
  });

  it("shows only the current side when a collection is skipped and re-enables it from a preset", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn() };
    const skipped = input({ collectionModes: { tags: "skip" } });
    const review = buildTaggerReview(skipped);
    expect(review.selection.tags).toEqual(["old tag"]);
    applyTaggerSelectionChange(
      skipped,
      review.selection,
      { ...review.selection, tags: ["old tag", "existing tag", "library tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("tags", "merge");
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
  });
});

describe("VideoTaggerReview alias matches", () => {
  // A scraper returned "Tit Tease", which the library knows as an alias of "Tit Worship", and also
  // "Tit Worship" itself: both names land on one library tag.
  const aliased = (overrides: Partial<TaggerReviewInput> = {}) =>
    input({
      result: {
        ...result,
        tagNames: ["Tit Tease", "Tit Worship", "Brand new tag"],
        tagCandidates: [
          { remoteId: "Tit Tease", name: "Tit Tease", existsLocally: true },
          { remoteId: "Tit Worship", name: "Tit Worship", existsLocally: true },
          { remoteId: "Brand new tag", name: "Brand new tag", existsLocally: false },
        ],
      } as MetadataServerVideoMatch,
      existingTagNames: ["Tit Tease", "Tit Worship"],
      tagMatchInfo: { "tit tease": "Tit Worship", "tit worship": "Tit Worship" },
      tagActions: { "tit tease": "include", "tit worship": "include", "brand new tag": "create" },
      ...overrides,
    });
  const incomingTags = (review: ReturnType<typeof buildTaggerReview>) => review.source.values.tags as ReviewItem[];

  it("labels an alias match with the library tag and merges the names that land on it", () => {
    const review = buildTaggerReview(aliased());
    expect(incomingTags(review).map((tag) => [tag.label, tag.scrapedAs ?? []])).toEqual([
      ["Brand new tag", []],
      ["Tit Worship", ["Tit Tease"]],
    ]);
    const summary = summarizeDiff(review.fields, review.source, review.target, review.selection);
    expect(summary.changes.map((change) => change.text)).toContain("2 tags added (1 new)");
  });

  it("lines an alias match up with the library tag the video already has", () => {
    const review = buildTaggerReview(
      aliased({
        video: { ...video, tags: [...video.tags, { id: 2, name: "Tit Worship" }] } as Video,
        currentTagNames: ["Old tag", "Tit Worship"],
        tagActions: { "tit tease": "include", "tit worship": "include", "brand new tag": "create" },
      }),
    );
    expect([...(review.selection.tags as string[])].sort()).toEqual(["brand new tag", "old tag", "tit worship"]);
    expect((review.target.values.tags as ReviewItem[]).find((tag) => tag.id === "tit worship")?.localId).toBe(2);
    const summary = summarizeDiff(review.fields, review.source, review.target, review.selection);
    expect(summary.changes.map((change) => change.text)).toContain("1 tag added (1 new)");
  });

  it("ignores an exclusion on a scraped name that lines up with a tag the video has", () => {
    // "Tit Tease" was taken out while it was still a new name of its own; it now lands on "Tit Worship".
    const review = buildTaggerReview(
      aliased({
        video: { ...video, tags: [...video.tags, { id: 2, name: "Tit Worship" }] } as Video,
        currentTagNames: ["Old tag", "Tit Worship"],
        result: { ...result, tagNames: ["Tit Tease"], tagCandidates: [] } as MetadataServerVideoMatch,
        tagMatchInfo: { "tit tease": "Tit Worship" },
        tagActions: { "tit tease": "exclude" },
        collectionModes: { urls: "merge", tags: "replace", performers: "merge", studio: "replace" },
      }),
    );
    expect(review.selection.tags).toEqual(["tit worship"]);
  });

  it("shows the scraped spelling only on hover, and not for a match that differs only in case", () => {
    const review = buildTaggerReview(
      aliased({
        result: {
          ...result,
          tagNames: ["Tit Tease", "tit worship", "TIT WORSHIP"],
          tagCandidates: [],
        } as MetadataServerVideoMatch,
      }),
    );
    const tags = review.fields.find((field) => field.key === "tags")!;
    const [merged] = incomingTags(review);
    expect(incomingTags(review)).toHaveLength(1);
    expect(merged.scrapedAs).toEqual(["Tit Tease"]);
    const { container } = render(<>{tags.renderItem!(merged)}</>);
    expect(container.textContent).toContain("Tit Worship");
    expect(container.querySelector("[title]")?.getAttribute("title")).toBe("Scraped as “Tit Tease”");
    const caseOnly = { ...merged, scrapedAs: undefined };
    expect(render(<>{tags.renderItem!(caseOnly)}</>).container.querySelector("[title]")).toBeNull();
  });

  it("toggles two spellings of one scraped name once", () => {
    const handlers = { onToggleTag: vi.fn() };
    const twice = input({
      result: { ...result, tagNames: ["Foo", "foo"], tagCandidates: [] } as MetadataServerVideoMatch,
      tagActions: { foo: "exclude" },
    });
    const review = buildTaggerReview(twice);
    applyTaggerSelectionChange(
      twice,
      review.selection,
      { ...review.selection, tags: [...(review.selection.tags as string[]), "foo"] },
      handlers,
    );
    expect(handlers.onToggleTag).toHaveBeenCalledWith(["Foo"]);
  });

  it("toggles every scraped name behind a merged chip, only the ones whose state changes", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn() };
    const both = aliased();
    const review = buildTaggerReview(both);
    applyTaggerSelectionChange(
      both,
      review.selection,
      { ...review.selection, tags: ["old tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onToggleTag.mock.calls[0][0].sort()).toEqual(["Tit Tease", "Tit Worship"]);

    const partly = aliased({
      tagActions: { "tit tease": "exclude", "tit worship": "include", "brand new tag": "create" },
    });
    const partlyReview = buildTaggerReview(partly);
    expect(partlyReview.selection.tags).toContain("tit worship");
    applyTaggerSelectionChange(
      partly,
      partlyReview.selection,
      { ...partlyReview.selection, tags: ["old tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onToggleTag).toHaveBeenLastCalledWith(["Tit Worship"]);

    const none = aliased({
      tagActions: { "tit tease": "exclude", "tit worship": "exclude", "brand new tag": "create" },
    });
    const noneReview = buildTaggerReview(none);
    applyTaggerSelectionChange(
      none,
      noneReview.selection,
      { ...noneReview.selection, tags: [...(noneReview.selection.tags as string[]), "tit worship"] },
      handlers,
    );
    expect(handlers.onToggleTag.mock.lastCall![0].sort()).toEqual(["Tit Tease", "Tit Worship"]);
  });
});

describe("VideoTaggerReview hand edits", () => {
  it("keeps every tag and performer in library order and shows the rows even without scraped items", () => {
    const review = buildTaggerReview(
      input({
        result: { ...result, tagNames: [], tagCandidates: [], performerCandidates: [] } as MetadataServerVideoMatch,
        performerChoices: [],
      }),
    );
    expect(review.fields.map((field) => field.key)).toContain("tags");
    expect(review.fields.map((field) => field.key)).toContain("performers");
    const sorted = buildTaggerReview(input({ currentTagNames: ["Zeta", "Alpha", "Mid"] }));
    expect((sorted.target.values.tags as { label: string }[]).map((tag) => tag.label)).toEqual([
      "Alpha",
      "Mid",
      "Zeta",
    ]);
    expect((sorted.source.values.tags as { label: string }[]).map((tag) => tag.label)).toEqual([
      "Brand new tag",
      "Existing tag",
      "Library tag",
    ]);
  });

  it("reads hand edits into the selection: library additions count as added, removals drop the current item", () => {
    const review = buildTaggerReview(input({ tagEdits: { added: [42], removed: [1] } }));
    expect(review.selection.tags).toContain("library:42");
    expect(review.selection.tags).not.toContain("old tag");
    expect(review.fields.find((field) => field.key === "tags")?.lockKeptItems).toBe(false);
    const summary = summarizeDiff(review.fields, review.source, review.target, review.selection);
    const text = summary.changes.map((change) => change.text).join(" | ");
    expect(text).toMatch(/4 tags added/);
    expect(text).toMatch(/1 tag removed/);
  });

  it("reports library additions and current removals as edits without touching the mode or the scraped items", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    // Two current-only tags, so taking one off cannot read as the "Only incoming" preset.
    const twoCurrent = input({
      video: { ...video, tags: [...video.tags, { id: 2, name: "Other tag" }] } as Video,
      currentTagNames: ["Old tag", "Other tag"],
    });
    const review = buildTaggerReview(twoCurrent);
    // A tag picked through search.
    applyTaggerSelectionChange(
      twoCurrent,
      review.selection,
      { ...review.selection, tags: [...(review.selection.tags as string[]), "library:42"] },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).toHaveBeenLastCalledWith("tags", { added: [42], removed: [] });
    // The current tag taken off with its x button.
    applyTaggerSelectionChange(
      twoCurrent,
      review.selection,
      { ...review.selection, tags: (review.selection.tags as string[]).filter((id) => id !== "old tag") },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).toHaveBeenLastCalledWith("tags", { added: [], removed: [1] });
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
    // A current performer known only locally is removed by its id.
    // A current performer taken off beside an excluded scraped one: a removal, not the "Only incoming" preset.
    applyTaggerSelectionChange(twoCurrent, review.selection, { ...review.selection, performers: [] }, handlers);
    expect(handlers.onRelationshipEditsChange).toHaveBeenLastCalledWith("performers", { added: [], removed: [9] });
  });

  it("lets the presets clear a removal and does not repeat unchanged edits", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    const edited = input({ tagEdits: { added: [], removed: [1] } });
    const review = buildTaggerReview(edited);
    // Combine puts the current tag back: that is the preset, not a per-item change.
    applyTaggerSelectionChange(
      edited,
      review.selection,
      { ...review.selection, tags: ["old tag", "existing tag", "library tag", "brand new tag"] },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).toHaveBeenLastCalledWith("tags", { added: [], removed: [] });
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
    handlers.onRelationshipEditsChange.mockClear();
    // Dropping one scraped tag while the removal stands toggles that tag only.
    applyTaggerSelectionChange(
      edited,
      review.selection,
      { ...review.selection, tags: (review.selection.tags as string[]).filter((id) => id !== "brand new tag") },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).not.toHaveBeenCalled();
    expect(handlers.onToggleTag).toHaveBeenCalledWith(["Brand new tag"]);
  });
});

describe("VideoTaggerReview selector mapping", () => {
  const items: ReviewItem[] = [
    { id: "old tag", label: "Old tag", isNew: false, localId: 1, inTarget: true },
    { id: "existing tag", label: "Existing tag", isNew: false, localId: 7 },
    { id: "brand new tag", label: "Brand new tag", isNew: true },
  ];

  it("selects the scraped item when a searched id is one the scrape brought, and a library id otherwise", () => {
    expect(selectorChange(items, ["old tag", "brand new tag"], [1, 7], false).sort()).toEqual([
      "brand new tag",
      "existing tag",
      "old tag",
    ]);
    expect(selectorChange(items, ["old tag", "brand new tag"], [1, 42], false).sort()).toEqual([
      "brand new tag",
      "library:42",
      "old tag",
    ]);
  });

  it("treats every searched id as a library addition while the scraped side is switched off, so the mode stays", () => {
    const next = selectorChange(items, ["old tag"], [1, 7], true);
    expect(next.sort()).toEqual(["library:7", "old tag"]);
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    const skipped = input({
      collectionModes: { tags: "skip" },
      result: {
        ...result,
        tagCandidates: [{ remoteId: "t1", name: "Existing tag", existsLocally: true, localId: 7 }],
      } as MetadataServerVideoMatch,
    });
    const review = buildTaggerReview(skipped);
    applyTaggerSelectionChange(skipped, review.selection, { ...review.selection, tags: next }, handlers);
    expect(handlers.onRelationshipEditsChange).toHaveBeenLastCalledWith("tags", { added: [7], removed: [] });
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
  });

  it("removes a current item through the selector and keeps scraped chips as they were", () => {
    expect(selectorChange(items, ["old tag", "existing tag"], [], false).sort()).toEqual(["existing tag"]);
  });

  it("marks derived and extension-managed tags as locked and ignores a malformed library id", () => {
    const locked = input({ video: { ...video, tags: [{ id: 1, name: "Old tag", canRemove: false }] } as Video });
    const review = buildTaggerReview(locked);
    expect((review.target.values.tags as ReviewItem[])[0].locked).toBe(true);
    const handlers = { onRelationshipEditsChange: vi.fn() };
    applyTaggerSelectionChange(
      locked,
      review.selection,
      { ...review.selection, tags: [...(review.selection.tags as string[]), "library:foo"] },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).not.toHaveBeenCalled();
  });
});

describe("VideoTaggerReview cover comparison", () => {
  const coverField = (review: ReturnType<typeof buildTaggerReview>) =>
    review.fields.find((field) => field.key === "image")!;

  it("keeps the cover a choice while the comparison has not answered", () => {
    const review = buildTaggerReview(input());
    expect(scalarStatus(coverField(review), review.source, review.target)).toBe("conflict");
    expect(coverField(review).alwaysVisible).toBe(true);
  });

  it("reads the same cover as unchanged rather than as two differing URLs", () => {
    const review = buildTaggerReview(input({ coverComparison: { verdict: "same" }, imageReplace: false }));
    expect(scalarStatus(coverField(review), review.source, review.target)).toBe("identical");
    expect(coverField(review).alwaysVisible).toBe(false);
    expect(summarizeDiff(review.fields, review.source, review.target, review.selection).identical).toContain("Cover");
    // Nothing is taken from the source on the strength of the verdict alone.
    expect(review.selection.image).toBe("target");
  });

  it("keeps the cover a choice once the person has asked for the incoming one", () => {
    // The verdict only lands after a download, so the choice can be made while both are still shown.
    // Folding the row away then would claim "unchanged" about a write and leave no way back.
    const review = buildTaggerReview(input({ coverComparison: { verdict: "same" }, imageReplace: true }));
    expect(scalarStatus(coverField(review), review.source, review.target)).toBe("conflict");
    expect(review.selection.image).toBe("source");
  });

  it("leaves the same cover at a higher resolution as a choice to offer", () => {
    const review = buildTaggerReview(
      input({
        coverComparison: {
          verdict: "upgrade",
          distance: 2,
          current: { width: 1280, height: 720, byteSize: 240_000 },
          candidate: { width: 1920, height: 1080, byteSize: 520_000 },
        },
        imageReplace: false,
      }),
    );
    expect(scalarStatus(coverField(review), review.source, review.target)).toBe("conflict");
    // Suggested, never taken: the upgrade is still one deliberate click away.
    expect(review.selection.image).toBe("target");
  });

  it("keeps two genuinely different covers a choice", () => {
    const review = buildTaggerReview(input({ coverComparison: { verdict: "differs", distance: 27 } }));
    expect(scalarStatus(coverField(review), review.source, review.target)).toBe("conflict");
  });
});

describe("VideoTaggerReview items the library does not have", () => {
  // "Brand new tag" is not in the library and the tagger does not create missing tags by default.
  const leftOut = (overrides: Partial<TaggerReviewInput> = {}) =>
    input({
      tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "exclude" },
      onLinkTag: vi.fn(async () => {}),
      ...overrides,
    });
  const renderRow = (reviewInput: TaggerReviewInput, key: "tags" | "performers" = "tags") => {
    const review = buildTaggerReview(reviewInput);
    const onChange = vi.fn();
    const selected = review.selection[key] as string[];
    render(<>{review.fields.find((field) => field.key === key)!.renderList!(selected, onChange, false)}</>);
    return { onChange, selected };
  };

  it("keeps new items apart from matched ones, each with a way to add or link it", () => {
    renderRow(leftOut());
    const strip = screen.getByText("Not in your library").parentElement!;
    expect(strip).toHaveTextContent("Brand new tag");
    expect(strip).not.toHaveTextContent("Existing tag");
    expect(screen.getByText("Brand new tag").closest("[data-state]")).toHaveAttribute("data-state", "available");
    expect(screen.getByRole("button", { name: "Create and add Tags: Brand new tag" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Link Tags: Brand new tag to a library tag" })).toBeInTheDocument();
  });

  it("marks a new item that will be created when the tagger creates missing items", () => {
    renderRow(
      leftOut({ tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "create" } }),
    );
    expect(screen.getByText("Brand new tag").closest("[data-state]")).toHaveAttribute("data-state", "new");
    expect(screen.getByRole("button", { name: "Remove Tags: Brand new tag" })).toBeInTheDocument();
  });

  it("remembers the scraped name as an alias of the linked tag", async () => {
    const reviewInput = leftOut();
    const { onChange } = renderRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Link Tags: Brand new tag to a library tag" }));
    await userEvent.click(screen.getByRole("button", { name: "Pick Edging" }));
    expect(screen.getByLabelText("Remember “Brand new tag” as an alias of Edging")).toBeChecked();
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(reviewInput.onLinkTag).toHaveBeenCalledWith("Brand new tag", { id: 7, label: "Edging" });
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Link" })).not.toBeInTheDocument();
  });

  it("adds the linked tag to this video alone when the alias is not remembered", async () => {
    const reviewInput = leftOut({
      tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "create" },
    });
    const { onChange, selected } = renderRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Link Tags: Brand new tag to a library tag" }));
    await userEvent.click(screen.getByRole("button", { name: "Pick Edging" }));
    await userEvent.click(screen.getByLabelText(/Remember “Brand new tag” as an alias/));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(reviewInput.onLinkTag).not.toHaveBeenCalled();
    expect([...onChange.mock.calls[0][0]].sort()).toEqual(
      [...selected.filter((id) => id !== "brand new tag"), "library:7"].sort(),
    );
  });

  it("keeps the panel open with the reason when the alias cannot be saved", async () => {
    const reviewInput = leftOut({ onLinkTag: vi.fn(async () => Promise.reject(new Error("Alias taken"))) });
    renderRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Link Tags: Brand new tag to a library tag" }));
    await userEvent.click(screen.getByRole("button", { name: "Pick Edging" }));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Alias taken");
    expect(screen.getByRole("button", { name: "Link" })).toBeEnabled();
  });

  it("closes the link panel on Escape and hands focus back to the link button", async () => {
    renderRow(leftOut());
    const linkButton = screen.getByRole("button", { name: "Link Tags: Brand new tag to a library tag" });
    await userEvent.click(linkButton);
    await userEvent.click(screen.getByLabelText(/Remember “Brand new tag” as an alias/));
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("group", { name: "Link “Brand new tag”" })).not.toBeInTheDocument();
    await waitFor(() => expect(linkButton).toHaveFocus());
  });

  it("offers no link for performers, which never match by alias", () => {
    renderRow(
      leftOut({ performerActions: { "remote-performer:p-known": "include", "remote-performer:p-new": "exclude" } }),
      "performers",
    );
    expect(screen.getByText("Not in your library").parentElement!).toHaveTextContent("Newcomer");
    expect(screen.getByRole("button", { name: "Create and add Performers: Newcomer" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Link / })).not.toBeInTheDocument();
  });
});

describe("VideoTaggerReview presets with new items left out", () => {
  // "Brand new tag" is not in the library and nobody chose it; the other two scraped tags exist.
  const leftOut = (overrides: Partial<TaggerReviewInput> = {}) =>
    input({
      tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "exclude" },
      onCollectionModeChange: vi.fn(),
      ...overrides,
    });
  const allNew = (overrides: Partial<TaggerReviewInput> = {}) =>
    leftOut({
      result: {
        ...result,
        tagNames: ["Brand new tag"],
        tagCandidates: [{ remoteId: "t3", name: "Brand new tag", existsLocally: false }],
      } as MetadataServerVideoMatch,
      existingTagNames: [],
      tagActions: { "brand new tag": "exclude" },
      ...overrides,
    });
  const renderTagsRow = (reviewInput: TaggerReviewInput) => {
    const review = buildTaggerReview(reviewInput);
    const tagsOnly = review.fields.filter((field) => field.key === "tags");
    const onChange = vi.fn();
    render(
      <MetadataDiff
        fields={tagsOnly}
        source={review.source}
        target={review.target}
        value={review.selection}
        onChange={onChange}
      />,
    );
    return { review, onChange };
  };
  const pressed = (name: string) => screen.getByRole("button", { name }).getAttribute("aria-pressed");

  it("reads the default selection as Combine although a new item is left out", () => {
    renderTagsRow(leftOut());
    expect(pressed("Use combined Tags")).toBe("true");
    expect(pressed("Use target Tags")).toBe("false");
    expect(pressed("Use source Tags")).toBe("false");
  });

  it("never selects an unchosen new item through a preset", async () => {
    const reviewInput = leftOut({
      tagActions: { "existing tag": "exclude", "library tag": "include", "brand new tag": "exclude" },
    });
    const { onChange } = renderTagsRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect([...onChange.mock.calls[0][0].tags].sort()).toEqual(["existing tag", "library tag", "old tag"]);
    await userEvent.click(screen.getByRole("button", { name: "Use source Tags" }));
    expect([...onChange.mock.calls[1][0].tags].sort()).toEqual(["existing tag", "library tag"]);
  });

  it("maps a preset click back to its mode without toggling an unchosen new item", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn() };
    const reviewInput = leftOut();
    const review = buildTaggerReview(reviewInput);
    applyTaggerSelectionChange(
      reviewInput,
      review.selection,
      { ...review.selection, tags: ["existing tag", "library tag"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("tags", "replace");
    expect(handlers.onToggleTag).not.toHaveBeenCalled();
  });

  it("keeps a chosen new item in the presets", () => {
    renderTagsRow(
      leftOut({ tagActions: { "existing tag": "include", "library tag": "include", "brand new tag": "create" } }),
    );
    expect(pressed("Use combined Tags")).toBe("true");
  });

  it("highlights the tagger's mode when presets coincide and switches it on click", async () => {
    // Every scraped tag is new and unchosen, so Combine and Only current select the same items.
    const reviewInput = allNew();
    renderTagsRow(reviewInput);
    expect(pressed("Use combined Tags")).toBe("true");
    expect(pressed("Use target Tags")).toBe("false");
    await userEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
    expect(reviewInput.onCollectionModeChange).toHaveBeenCalledWith("tags", "skip");
  });

  it("highlights Only current while the tagger skips the collection", () => {
    renderTagsRow(allNew({ collectionModes: { tags: "skip" } }));
    expect(pressed("Use target Tags")).toBe("true");
    expect(pressed("Use combined Tags")).toBe("false");
  });

  it("leaves the mode alone when the active preset is clicked again", async () => {
    const reviewInput = leftOut();
    const { onChange } = renderTagsRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect(reviewInput.onCollectionModeChange).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("switches to Only current in one click when it shares its items with Combine", async () => {
    // Every scraped tag is new and unchosen, and the current tag was taken off by hand.
    const reviewInput = allNew({ tagEdits: { added: [], removed: [1] } });
    const { onChange } = renderTagsRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
    expect(onChange.mock.calls[0][0].tags).toEqual(["old tag"]);
    expect(reviewInput.onCollectionModeChange).toHaveBeenLastCalledWith("tags", "skip");
  });

  it("adds the last new item as a choice, not as a preset", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    for (const mode of ["merge", "replace"] as const) {
      const reviewInput = leftOut({ collectionModes: { tags: mode } });
      const review = buildTaggerReview(reviewInput);
      applyTaggerSelectionChange(
        reviewInput,
        review.selection,
        { ...review.selection, tags: [...(review.selection.tags as string[]), "brand new tag"] },
        handlers,
      );
      expect(handlers.onToggleTag).toHaveBeenLastCalledWith(["Brand new tag"]);
    }
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
    expect(handlers.onRelationshipEditsChange).not.toHaveBeenCalled();
  });

  it("counts a performer already on the video as current, not as new, in the presets", () => {
    const handlers = {
      onCollectionModeChange: vi.fn(),
      onTogglePerformer: vi.fn(),
      onRelationshipEditsChange: vi.fn(),
    };
    // "Known Performer" is on the video and scraped, though the library lookup has not yet said it
    // exists; "Local Only" is on the video alone. The known one was taken off by hand.
    const reviewInput = leftOut({
      video: { ...video, performers: [...video.performers, { id: 10, name: "Local Only" }] } as Video,
      performerChoices: [
        {
          key: "remote-performer:p-known",
          label: "Known Performer",
          candidate: { remoteId: "p-known", name: "Known Performer", existsLocally: false },
        },
      ],
      currentPerformerChoiceKeys: ["remote-performer:p-known"],
      performerActions: { "remote-performer:p-known": "include" },
      performerEdits: { added: [], removed: [9] },
    });
    const review = buildTaggerReview(reviewInput);
    expect(review.selection.performers).toEqual(["local:10"]);
    // Only StashDB: the scraped performer alone.
    applyTaggerSelectionChange(
      reviewInput,
      review.selection,
      { ...review.selection, performers: ["remote-performer:p-known"] },
      handlers,
    );
    expect(handlers.onCollectionModeChange).toHaveBeenCalledWith("performers", "replace");
    expect(handlers.onRelationshipEditsChange).toHaveBeenCalledWith("performers", { added: [], removed: [] });
  });
  it("stays in Only current when that preset is clicked after a tag was taken off by hand", () => {
    // Every scraped tag is new and unchosen, so Combine and Only current select the same items. The row
    // skips tags and the current tag was taken off by hand; "Only current" puts it back.
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    const reviewInput = allNew({ collectionModes: { tags: "skip" }, tagEdits: { added: [], removed: [1] } });
    const review = buildTaggerReview(reviewInput);
    expect(review.selection.tags).toEqual([]);
    applyTaggerSelectionChange(reviewInput, review.selection, { ...review.selection, tags: ["old tag"] }, handlers);
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalledWith("tags", "merge");
    expect(handlers.onRelationshipEditsChange).toHaveBeenCalledWith("tags", { added: [], removed: [] });
  });

  it("keeps the current mode when the selection matches several presets", () => {
    // Combine and Only current select the same ids when nothing incoming is eligible.
    expect(modeForSelection(["a"], ["a"], [], "skip")).toBe("skip");
    expect(modeForSelection(["a"], ["a"], [], "merge")).toBe("merge");
    expect(modeForSelection(["a"], ["a"], [], "replace")).toBe("merge");
    expect(modeForSelection(["a", "b"], ["a"], ["b"], "skip")).toBe("merge");
  });

  it("switches to Combine on a real click after a hand removal while the presets coincide", async () => {
    // The click runs the selection change and then the explicit preset, as the row does.
    const onCollectionModeChange = vi.fn();
    const reviewInput = allNew({
      collectionModes: { tags: "skip" },
      tagEdits: { added: [], removed: [1] },
      onCollectionModeChange,
    });
    const review = buildTaggerReview(reviewInput);
    render(
      <MetadataDiff
        fields={review.fields.filter((field) => field.key === "tags")}
        source={review.source}
        target={review.target}
        value={review.selection}
        onChange={(next) =>
          applyTaggerSelectionChange(reviewInput, review.selection, next, {
            onCollectionModeChange,
            onToggleTag: vi.fn(),
            onRelationshipEditsChange: vi.fn(),
          })
        }
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect(onCollectionModeChange).toHaveBeenLastCalledWith("tags", "merge");
  });

  it("calls an unchosen new tag left out when the tagger creates missing tags by default", () => {
    const review = buildTaggerReview(leftOut({ createMissingTags: true }));
    const tags = review.fields.filter((field) => field.key === "tags");
    const text = summarizeDiff(tags, review.source, review.target, review.selection).changes.map((c) => c.text);
    expect(text.join(" | ")).toMatch(/1 tag left out/);
    const offered = buildTaggerReview(leftOut());
    const offeredText = summarizeDiff(
      offered.fields.filter((field) => field.key === "tags"),
      offered.source,
      offered.target,
      offered.selection,
    ).changes.map((c) => c.text);
    expect(offeredText.join(" | ")).not.toMatch(/left out/);
  });

  it("still switches Only current to Combine when Combine is clicked and the presets coincide", async () => {
    const reviewInput = allNew({ collectionModes: { tags: "skip" } });
    renderTagsRow(reviewInput);
    await userEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect(reviewInput.onCollectionModeChange).toHaveBeenCalledWith("tags", "merge");
  });
});

describe("VideoTaggerReview Only <source> with a current tag that was also scraped", () => {
  // "Old tag" is on the video and the scrape returns it too, beside one other tag the library has.
  const scrapedToo = (edits?: TaggerRelationshipEdits) =>
    input({
      result: {
        ...result,
        tagNames: ["Old tag", "Existing tag"],
        tagCandidates: [
          { remoteId: "t0", name: "Old tag", existsLocally: true, localId: 1 },
          { remoteId: "t1", name: "Existing tag", existsLocally: true },
        ],
      } as MetadataServerVideoMatch,
      existingTagNames: ["Old tag", "Existing tag"],
      tagActions: { "old tag": "include", "existing tag": "include" },
      collectionModes: { urls: "merge", tags: "replace", performers: "merge", studio: "replace" },
      tagEdits: edits,
    });

  it("takes the tag off when its ✕ is clicked", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    const reviewInput = scrapedToo();
    const review = buildTaggerReview(reviewInput);
    expect([...(review.selection.tags as string[])].sort()).toEqual(["existing tag", "old tag"]);
    applyTaggerSelectionChange(
      reviewInput,
      review.selection,
      { ...review.selection, tags: ["existing tag"] },
      handlers,
    );
    expect(handlers.onRelationshipEditsChange).toHaveBeenCalledWith("tags", { added: [], removed: [1] });
    expect(handlers.onCollectionModeChange).not.toHaveBeenCalled();
    // Once recorded, the review shows it off.
    expect(buildTaggerReview(scrapedToo({ added: [], removed: [1] })).selection.tags).toEqual(["existing tag"]);
  });

  it("does not record a current-only tag as removed, since Only <source> drops it anyway", () => {
    const handlers = { onCollectionModeChange: vi.fn(), onToggleTag: vi.fn(), onRelationshipEditsChange: vi.fn() };
    // "Kept only" is on the video but not scraped; flipping one scraped chip must not mark it removed.
    const base = scrapedToo();
    const reviewInput = {
      ...base,
      video: { ...base.video, tags: [...base.video.tags, { id: 2, name: "Kept only" }] } as Video,
      currentTagNames: ["Old tag", "Kept only"],
    };
    const review = buildTaggerReview(reviewInput);
    expect([...(review.selection.tags as string[])].sort()).toEqual(["existing tag", "old tag"]);
    applyTaggerSelectionChange(reviewInput, review.selection, { ...review.selection, tags: ["old tag"] }, handlers);
    expect(handlers.onToggleTag).toHaveBeenCalledWith(["Existing tag"]);
    expect(handlers.onRelationshipEditsChange).not.toHaveBeenCalled();
  });
});

describe("VideoTaggerReview studio", () => {
  const renderStudio = (reviewInput: TaggerReviewInput, full = false) => {
    const review = buildTaggerReview(reviewInput);
    const props = {
      fields: review.fields.filter((field) => field.key === "studio"),
      source: review.source,
      target: review.target,
      value: review.selection,
      onChange: vi.fn(),
    };
    render(full ? <MetadataDiff {...props} /> : <MetadataDiffSummary {...props} />);
    return review;
  };

  it("is not offered as a fill, and is created only through its Create action", async () => {
    const onCreateStudio = vi.fn();
    const review = renderStudio(input({ studioIsNew: true, createStudio: false, onCreateStudio }));
    // The mode still says "replace", but nothing would be set: the review must not claim otherwise.
    expect(review.selection.studio).toBe("target");
    expect(screen.getByText("not in your library")).toBeInTheDocument();
    expect(screen.queryByRole("radio", { name: "Use StashDB" })).not.toBeInTheDocument();
    expect(screen.queryByText(/fills empty/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Studio: create “Studio X” and use it" }));
    expect(onCreateStudio).toHaveBeenCalledOnce();
  });

  it("cannot be picked as the incoming side in the full rows either", async () => {
    const onCreateStudio = vi.fn();
    renderStudio(input({ studioIsNew: true, createStudio: false, onCreateStudio }), true);
    expect(screen.getByRole("radio", { name: "Studio from source" })).toBeDisabled();
    expect(screen.queryByText("Filled from StashDB")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Studio: create “Studio X” and use it" }));
    expect(onCreateStudio).toHaveBeenCalledOnce();
  });

  it("says it will be created once it is chosen", () => {
    const review = renderStudio(input({ studioIsNew: true, createStudio: true }));
    expect(review.selection.studio).toBe("source");
    expect(screen.getByText("fills empty · new, will be created")).toBeInTheDocument();
  });

  it("leaves a studio the library has as a plain fill", () => {
    const review = renderStudio(input({ studioIsNew: false, createStudio: false }));
    expect(review.selection.studio).toBe("source");
    expect(screen.getByText("fills empty")).toBeInTheDocument();
  });

  it("shows a studio matched under another name by its library name, with the scraped one on hover", () => {
    const review = buildTaggerReview(input({ studioMatchName: "Studio X Productions" }));
    const studio = review.fields.find((field) => field.key === "studio")!;
    expect(review.source.values.studio).toBe("Studio X Productions");
    const { container } = render(<>{studio.render!(review.source.values.studio)}</>);
    expect(container).toHaveTextContent("Studio X Productions");
    expect(container.querySelector("[title]")).toHaveAttribute("title", "Scraped as “Studio X”");
  });
});
