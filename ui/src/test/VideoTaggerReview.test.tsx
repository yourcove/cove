import { describe, expect, it, vi } from "vitest";
import { render } from "@testing-library/react";
import type { MetadataServerVideoMatch, Video } from "../api/types";
import {
  applyTaggerSelectionChange,
  buildTaggerReview,
  isHandEdited,
  selectorChange,
  type ReviewItem,
  type TaggerReviewInput,
} from "../components/VideoTaggerReview";
import { defaultDiffSelection, scalarStatus, summarizeDiff } from "../components/MetadataDiff";

vi.mock("../api/client", () => ({ videos: { screenshotUrl: (id: number) => `/cover/${id}` } }));

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
    expect(summary.changes.map((change) => change.text)).toContain("2 tags added");
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
    expect(summary.changes.map((change) => change.text)).toContain("1 tag added");
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
