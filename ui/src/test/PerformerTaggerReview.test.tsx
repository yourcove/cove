import { render, screen } from "@testing-library/react";
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
});
