import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { MetadataDiff } from "../components/MetadataDiff";
import {
  applyStudioSelectionChange,
  buildStudioReview,
  type StudioReviewHandlers,
  type StudioReviewInput,
} from "../components/StudioTaggerReview";

const input = (overrides: Partial<StudioReviewInput> = {}): StudioReviewInput => ({
  sourceName: "StashDB",
  scalars: [
    { key: "name", label: "Name", current: "Palladium", scraped: "Palladium" },
    { key: "parent", label: "Parent", current: null, scraped: "Palladium Group" },
  ],
  fieldStrategies: { name: "ignore", parent: "overwrite", image: "ignore" },
  collectionModes: { urls: "merge", aliases: "merge" },
  currentImageUrl: "/api/studios/1/image",
  incomingImageUrl: "https://cdn.example/logo.jpg",
  urls: { current: ["https://a.example"], incoming: ["https://b.example"] },
  aliases: { current: [], incoming: ["Palladium Video"] },
  ...overrides,
});

const handlers = (): StudioReviewHandlers => ({
  onFieldStrategyChange: vi.fn(),
  onCollectionModeChange: vi.fn(),
});

describe("StudioTaggerReview", () => {
  it("reads strategies and collection modes as the selection", () => {
    const review = buildStudioReview(input());

    expect(review.fields.map((field) => field.key)).toEqual(["name", "parent", "image", "urls", "aliases"]);
    expect(review.source.sentenceLabel).toBe("StashDB");
    expect(review.selection).toMatchObject({ name: "target", parent: "source", image: "target" });
    expect(review.selection.urls).toEqual(["https://a.example", "https://b.example"]);
    expect(review.selection.aliases).toEqual(["Palladium Video"]);
  });

  it("offers the presets only, since a studio list has no per-item action", () => {
    const review = buildStudioReview(input());

    for (const key of ["urls", "aliases"]) {
      const field = review.fields.find((entry) => entry.key === key)!;
      expect(field.modesOnly).toBe(true);
      expect(field.lockKeptItems).toBe(true);
    }
  });

  it("leaves out a list the source has nothing for and the logo the source does not have", () => {
    const review = buildStudioReview(
      input({ aliases: { current: ["Palladium Video"], incoming: [] }, incomingImageUrl: undefined }),
    );

    expect(review.fields.map((field) => field.key)).toEqual(["name", "parent", "urls"]);
    expect(review.selection.aliases).toBeUndefined();
    expect(review.selection.image).toBeUndefined();
  });

  it("takes the incoming side alone when a list is set to replace", () => {
    const review = buildStudioReview(input({ collectionModes: { urls: "replace", aliases: "merge" } }));

    expect(review.selection.urls).toEqual(["https://b.example"]);
  });

  it("trims and dedupes list values, and counts a value both sides have only once", () => {
    const review = buildStudioReview(
      input({
        urls: { current: [" https://a.example ", "https://a.example", ""], incoming: ["https://a.example"] },
      }),
    );

    expect(review.target.values.urls).toEqual([{ id: "https://a.example", label: "https://a.example" }]);
    expect(review.selection.urls).toEqual(["https://a.example"]);
  });

  it("builds an empty review when the source offers nothing", () => {
    const review = buildStudioReview(
      input({
        scalars: [],
        incomingImageUrl: undefined,
        urls: { current: ["https://a.example"], incoming: [] },
        aliases: { current: [], incoming: [] },
      }),
    );

    expect(review.fields).toEqual([]);
    expect(review.selection).toEqual({});
  });

  it("keeps only the current values when a list is switched off", () => {
    const review = buildStudioReview(input({ collectionModes: { urls: "skip", aliases: "merge" } }));

    expect(review.selection.urls).toEqual(["https://a.example"]);
  });

  it("flips a field strategy when a scalar or the logo changes side", () => {
    const review = buildStudioReview(input());
    const calls = handlers();

    applyStudioSelectionChange(
      input(),
      review.selection,
      { ...review.selection, name: "source", image: "source" },
      calls,
    );

    expect(calls.onFieldStrategyChange).toHaveBeenCalledWith("name", "overwrite");
    expect(calls.onFieldStrategyChange).toHaveBeenCalledWith("image", "overwrite");
    expect(calls.onFieldStrategyChange).toHaveBeenCalledTimes(2);
    expect(calls.onCollectionModeChange).not.toHaveBeenCalled();
  });

  it("maps a changed list selection back to the collection mode it matches", () => {
    const review = buildStudioReview(input());
    const calls = handlers();

    applyStudioSelectionChange(
      input(),
      review.selection,
      { ...review.selection, urls: ["https://b.example"], aliases: [] },
      calls,
    );

    expect(calls.onCollectionModeChange).toHaveBeenCalledWith("urls", "replace");
    expect(calls.onCollectionModeChange).toHaveBeenCalledWith("aliases", "skip");
    expect(calls.onFieldStrategyChange).not.toHaveBeenCalled();
  });

  it("reports nothing for a selection that did not change", () => {
    const review = buildStudioReview(input());
    const calls = handlers();

    applyStudioSelectionChange(input(), review.selection, { ...review.selection }, calls);

    expect(calls.onFieldStrategyChange).not.toHaveBeenCalled();
    expect(calls.onCollectionModeChange).not.toHaveBeenCalled();
  });

  it("highlights the tagger's mode when presets coincide and switches it on click", async () => {
    // The source has only a URL the studio already has, so Combine and Only current select the same URLs.
    const reviewInput = input({
      collectionModes: { urls: "skip", aliases: "merge" },
      onCollectionModeChange: vi.fn(),
      urls: { current: ["https://a.example", "https://c.example"], incoming: ["https://a.example"] },
    });
    const review = buildStudioReview(reviewInput);
    render(
      <MetadataDiff
        fields={review.fields.filter((field) => field.key === "urls")}
        source={review.source}
        target={review.target}
        value={review.selection}
        onChange={vi.fn()}
      />,
    );
    const pressed = (name: string) => screen.getByRole("button", { name }).getAttribute("aria-pressed");
    expect(pressed("Use target URLs")).toBe("true");
    expect(pressed("Use combined URLs")).toBe("false");
    await userEvent.click(screen.getByRole("button", { name: "Use combined URLs" }));
    expect(reviewInput.onCollectionModeChange).toHaveBeenCalledWith("urls", "merge");
  });
});
