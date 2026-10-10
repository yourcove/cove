import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { MetadataDiff, summarizeDiff } from "../components/MetadataDiff";
import { MetadataDiffSummary } from "../components/MetadataDiffSummary";
import {
  applyPerformerSelectionChange,
  buildPerformerReview,
  type PerformerReviewHandlers,
  type PerformerReviewInput,
} from "../components/PerformerTaggerReview";

const input = (overrides: Partial<PerformerReviewInput> = {}): PerformerReviewInput => ({
  sourceName: "StashDB",
  scalars: [
    { key: "name", label: "Name", current: "Jane Doe", scraped: "Jane Doe" },
    { key: "country", label: "Country", current: null, scraped: "CZ" },
    { key: "gender", label: "Gender", current: "Female", scraped: "Non-binary" },
  ],
  fieldStrategies: { name: "ignore", country: "overwrite", gender: "ignore", image: "ignore" },
  collectionModes: { urls: "merge", aliases: "merge", tags: "merge" },
  currentImageUrl: "/api/performers/1/image",
  incomingImageUrl: "https://cdn.example/b.jpg",
  urls: { current: ["https://a.example"], incoming: ["https://a.example", "https://b.example"] },
  aliases: { current: [], incoming: ["JD"] },
  tags: {
    current: ["Blonde"],
    incoming: ["blonde", "Tattoos", "New Tag"],
    existing: ["Blonde", "Tattoos"],
    actions: { blonde: "include", tattoos: "include", "new tag": "create" },
  },
  ...overrides,
});

const handlers = (): PerformerReviewHandlers => ({
  onFieldStrategyChange: vi.fn(),
  onCollectionModeChange: vi.fn(),
  onTagActionsChange: vi.fn(),
});

describe("PerformerTaggerReview", () => {
  it("reads strategies, modes and tag actions as the selection and marks tags the library does not have", () => {
    const review = buildPerformerReview(input());

    expect(review.fields.map((field) => field.key)).toEqual([
      "name",
      "country",
      "gender",
      "image",
      "urls",
      "aliases",
      "tags",
    ]);
    expect(review.source.sentenceLabel).toBe("StashDB");
    expect(review.selection).toMatchObject({ name: "target", country: "source", gender: "target", image: "target" });
    expect(review.selection.urls).toEqual(["https://a.example", "https://b.example"]);
    expect(review.selection.aliases).toEqual(["JD"]);
    expect(review.selection.tags).toEqual(["blonde", "tattoos", "new tag"]);

    const tags = review.fields.find((field) => field.key === "tags")!;
    const incoming = review.source.values.tags as { id: string; isNew: boolean }[];
    expect(incoming.map((tag) => [tag.id, tags.itemIsNew?.(tag)])).toEqual([
      ["blonde", false],
      ["tattoos", false],
      ["new tag", true],
    ]);
  });

  it("leaves out a list the source has nothing for and drops excluded tags from the selection", () => {
    const review = buildPerformerReview(
      input({
        aliases: { current: ["JD"], incoming: [] },
        tags: { ...input().tags, actions: { blonde: "include", tattoos: "exclude", "new tag": "create" } },
      }),
    );

    expect(review.fields.some((field) => field.key === "aliases")).toBe(false);
    expect(review.selection.tags).toEqual(["blonde", "new tag"]);
  });

  it("maps scalar and image choices back to strategies", () => {
    const base = input();
    const review = buildPerformerReview(base);
    const calls = handlers();

    applyPerformerSelectionChange(
      base,
      review.selection,
      { ...review.selection, gender: "source", image: "source" },
      calls,
    );

    expect(calls.onFieldStrategyChange).toHaveBeenCalledWith("gender", "overwrite");
    expect(calls.onFieldStrategyChange).toHaveBeenCalledWith("image", "overwrite");
    expect(calls.onCollectionModeChange).not.toHaveBeenCalled();
  });

  it("derives a collection mode from the presets and reports every toggled tag in one call", () => {
    const base = input();
    const review = buildPerformerReview(base);
    const calls = handlers();

    applyPerformerSelectionChange(base, review.selection, { ...review.selection, urls: ["https://a.example"] }, calls);
    expect(calls.onCollectionModeChange).toHaveBeenCalledWith("urls", "skip");

    applyPerformerSelectionChange(base, review.selection, { ...review.selection, tags: ["blonde", "tattoos"] }, calls);
    expect(calls.onTagActionsChange).toHaveBeenLastCalledWith({ "new tag": "exclude" });

    const excluded = input({
      tags: { ...base.tags, actions: { blonde: "include", tattoos: "exclude", "new tag": "exclude" } },
    });
    const excludedReview = buildPerformerReview(excluded);
    applyPerformerSelectionChange(
      excluded,
      excludedReview.selection,
      { ...excludedReview.selection, tags: ["blonde", "tattoos", "new tag"] },
      calls,
    );
    expect(calls.onTagActionsChange).toHaveBeenLastCalledWith({ tattoos: "include", "new tag": "create" });
  });

  // The performer tagger creates missing tags by default, so a new tag that is not selected was taken
  // out on purpose: it is left out, not merely "not in your library".
  describe("a new tag the person unticked", () => {
    const unticked = () => {
      const review = buildPerformerReview(
        input({ tags: { ...input().tags, actions: { blonde: "include", tattoos: "include", "new tag": "exclude" } } }),
      );
      return { ...review, fields: review.fields.filter((field) => field.key === "tags") };
    };

    it("is summarised as left out", () => {
      const review = unticked();
      const text = summarizeDiff(review.fields, review.source, review.target, review.selection).changes;
      expect(text.map((change) => change.text).join(" | ")).toMatch(/1 tag left out/);
    });

    it("is a left-out chip in the compact summary", () => {
      const review = unticked();
      render(
        <MetadataDiffSummary
          fields={review.fields}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByText("New Tag").closest("[data-state]")).toHaveAttribute("data-state", "left-out");
    });

    it("is only offered when the tagger does not create missing tags", () => {
      const review = buildPerformerReview(
        input({
          createMissingTags: false,
          tags: { ...input().tags, actions: { blonde: "include", tattoos: "include", "new tag": "exclude" } },
        }),
      );
      const fields = review.fields.filter((field) => field.key === "tags");
      const text = summarizeDiff(fields, review.source, review.target, review.selection).changes;
      expect(text.map((change) => change.text).join(" | ")).not.toMatch(/left out/);
      render(
        <MetadataDiffSummary
          fields={fields}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByText("New Tag").closest("[data-state]")).toHaveAttribute("data-state", "not-in-library");
    });

    it("is an excluded chip in the full rows", () => {
      const review = unticked();
      render(
        <MetadataDiff
          fields={review.fields}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByText("New Tag").closest("[data-state]")).toHaveAttribute("data-state", "excluded");
    });
  });

  // With "Create missing tags" off, a new tag starts unchosen; the presets are modes, not a way to
  // create every new tag at once.
  describe("presets with a new tag left out", () => {
    const leftOut = (overrides: Partial<PerformerReviewInput> = {}) =>
      input({
        createMissingTags: false,
        onCollectionModeChange: vi.fn(),
        tags: {
          current: ["Blonde", "Curvy"],
          incoming: ["blonde", "Tattoos", "New Tag"],
          existing: ["Blonde", "Curvy", "Tattoos"],
          actions: { blonde: "include", tattoos: "include", "new tag": "exclude" },
        },
        ...overrides,
      });
    const renderTagsRow = (reviewInput: PerformerReviewInput) => {
      const review = buildPerformerReview(reviewInput);
      const onChange = vi.fn();
      render(
        <MetadataDiff
          fields={review.fields.filter((field) => field.key === "tags")}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={onChange}
        />,
      );
      return { review, onChange };
    };
    const pressed = (name: string) => screen.getByRole("button", { name }).getAttribute("aria-pressed");

    it("reads the default selection as Combine", () => {
      renderTagsRow(leftOut());
      expect(pressed("Use combined Tags")).toBe("true");
      expect(pressed("Use source Tags")).toBe("false");
    });

    it("never selects the unchosen new tag through a preset", async () => {
      const { onChange } = renderTagsRow(leftOut());
      await userEvent.click(screen.getByRole("button", { name: "Use source Tags" }));
      expect([...onChange.mock.calls[0][0].tags].sort()).toEqual(["blonde", "tattoos"]);
    });

    it("maps Only <source> back to its mode without creating the new tag", () => {
      const reviewInput = leftOut();
      const review = buildPerformerReview(reviewInput);
      const calls = handlers();
      applyPerformerSelectionChange(
        reviewInput,
        review.selection,
        { ...review.selection, tags: ["blonde", "tattoos"] },
        calls,
      );
      expect(calls.onCollectionModeChange).toHaveBeenCalledWith("tags", "replace");
      expect(calls.onTagActionsChange).not.toHaveBeenCalled();
    });

    it("highlights the tagger's mode when presets coincide and switches it on click", async () => {
      // The only scraped tag is new and unchosen, so Combine and Only current select the same tags.
      const reviewInput = leftOut({
        tags: { current: ["Blonde"], incoming: ["New Tag"], existing: ["Blonde"], actions: { "new tag": "exclude" } },
      });
      renderTagsRow(reviewInput);
      expect(pressed("Use combined Tags")).toBe("true");
      expect(pressed("Use target Tags")).toBe("false");
      await userEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
      expect(reviewInput.onCollectionModeChange).toHaveBeenCalledWith("tags", "skip");
    });

    // The selection the review is rebuilt from after each change, as the tagger's state would give it.
    const renderLive = (reviewInput: PerformerReviewInput) => {
      const review = buildPerformerReview(reviewInput);
      const calls = { ...handlers(), onCollectionModeChange: reviewInput.onCollectionModeChange! };
      render(
        <MetadataDiff
          fields={review.fields.filter((field) => field.key === "tags")}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={(next) => applyPerformerSelectionChange(reviewInput, review.selection, next, calls)}
        />,
      );
      return calls;
    };

    it("switches to Only current in one click although that reads as unchoosing the one new tag", async () => {
      const reviewInput = leftOut({
        tags: { current: ["Blonde"], incoming: ["New Tag"], existing: ["Blonde"], actions: { "new tag": "create" } },
      });
      renderLive(reviewInput);
      await userEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
      expect(reviewInput.onCollectionModeChange).toHaveBeenLastCalledWith("tags", "skip");
    });

    it("takes the excluded tags back in through the customised mode's button", async () => {
      const reviewInput = leftOut({
        collectionModes: { urls: "merge", aliases: "merge", tags: "replace" },
        tags: { ...leftOut().tags, actions: { blonde: "include", tattoos: "exclude", "new tag": "exclude" } },
      });
      const calls = renderLive(reviewInput);
      await userEvent.click(screen.getByRole("button", { name: "Use source Tags" }));
      expect(calls.onTagActionsChange).toHaveBeenCalledWith({ tattoos: "include" });
      const modes = vi.mocked(reviewInput.onCollectionModeChange!).mock.calls.map(([, mode]) => mode);
      expect(modes.every((mode) => mode === "replace")).toBe(true);
    });

    it("keeps the mode pressed when the selection is customised", () => {
      renderTagsRow(
        leftOut({
          collectionModes: { urls: "merge", aliases: "merge", tags: "replace" },
          tags: { ...leftOut().tags, actions: { blonde: "include", tattoos: "exclude", "new tag": "exclude" } },
        }),
      );
      expect(pressed("Use source Tags")).toBe("true");
      expect(pressed("Use combined Tags")).toBe("false");
    });
  });

  // The server matched the scraped "Big Tits" to the library tag "Big Breasts", by alias.
  describe("a scraped tag the server matched under another name", () => {
    const aliased = (overrides: Partial<PerformerReviewInput["tags"]> = {}) =>
      input({
        tags: {
          current: ["Big Breasts"],
          incoming: ["Big Tits", "Tattoos"],
          existing: ["Big Tits", "Tattoos"],
          actions: { "big tits": "include", tattoos: "include" },
          matches: { "big tits": "Big Breasts" },
          ...overrides,
        },
      });

    it("lands on the library tag, so a tag the performer has is not shown as added", () => {
      const review = buildPerformerReview(aliased());
      const incoming = review.source.values.tags as { id: string; label: string; isNew: boolean }[];
      expect(incoming.map((tag) => [tag.id, tag.label, tag.isNew])).toEqual([
        ["big breasts", "Big Breasts", false],
        ["tattoos", "Tattoos", false],
      ]);
      const text = summarizeDiff(review.fields, review.source, review.target, review.selection).changes;
      expect(text.map((change) => change.text)).toContain("1 tag added");
    });

    it("keeps the scraped spelling on hover", () => {
      const review = buildPerformerReview(aliased({ current: [] }));
      render(
        <MetadataDiff
          fields={review.fields.filter((field) => field.key === "tags")}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByTitle("Scraped as “Big Tits”")).toHaveTextContent("Big Breasts");
    });

    it("says the other spelling came as well when the scrape also returned the library name", () => {
      const review = buildPerformerReview(
        aliased({
          current: [],
          incoming: ["Big Tits", "Big Breasts"],
          existing: ["Big Tits", "Big Breasts"],
          actions: { "big tits": "include", "big breasts": "include" },
        }),
      );
      render(
        <MetadataDiff
          fields={review.fields.filter((field) => field.key === "tags")}
          source={review.source}
          target={review.target}
          value={review.selection}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByTitle("Also scraped as “Big Tits”")).toHaveTextContent("Big Breasts");
    });

    it("toggles the scraped name the apply sends when the library tag's chip is toggled", () => {
      const base = aliased({ current: [] });
      const review = buildPerformerReview(base);
      const calls = handlers();
      applyPerformerSelectionChange(base, review.selection, { ...review.selection, tags: ["tattoos"] }, calls);
      expect(calls.onTagActionsChange).toHaveBeenLastCalledWith({ "big tits": "exclude" });
    });
  });
});
