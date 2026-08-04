import { describe, expect, it } from "vitest";
import { AUDIO_MULTI_SORT_KEYS, AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { FACE_SORT_OPTIONS } from "../components/faceSortOptions";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";
import { IMAGE_MULTI_SORT_KEYS, IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { RAW_SEGMENT_SORT_OPTIONS } from "../components/segmentSortOptions";
import { STUDIO_MULTI_SORT_KEYS, STUDIO_SORT_OPTIONS } from "../components/studioSortOptions";
import { TEXT_MULTI_SORT_KEYS, TEXT_SORT_OPTIONS } from "../components/textSortOptions";

describe("canonical entity sort options", () => {
  it.each([
    ["audios", AUDIO_SORT_OPTIONS],
    ["texts", TEXT_SORT_OPTIONS],
    ["images", IMAGE_SORT_OPTIONS],
    ["studios", STUDIO_SORT_OPTIONS],
    ["groups", GROUP_SORT_OPTIONS],
    ["segments", RAW_SEGMENT_SORT_OPTIONS],
    ["faces", FACE_SORT_OPTIONS],
  ])("includes one Random option for %s", (_entity, options) => {
    expect(options.filter((option) => option.value === "random")).toEqual([
      { value: "random", label: "Random" },
    ]);
  });

  it("keeps Visual Match out of the context-safe image catalog", () => {
    expect(IMAGE_SORT_OPTIONS.some((option) => option.value === "visual_match")).toBe(false);
  });

  it.each([
    ["audios", AUDIO_MULTI_SORT_KEYS],
    ["texts", TEXT_MULTI_SORT_KEYS],
    ["images", IMAGE_MULTI_SORT_KEYS],
    ["studios", STUDIO_MULTI_SORT_KEYS],
  ])("allows Rating in compound sorts for %s", (_entity, keys) => {
    expect(keys).toContain("rating");
  });

  it.each([
    ["audios", AUDIO_MULTI_SORT_KEYS, ["play_count", "like_counter", "play_duration", "last_played_at"]],
    ["texts", TEXT_MULTI_SORT_KEYS, ["read_count", "like_counter", "read_duration", "last_read_at"]],
    ["images", IMAGE_MULTI_SORT_KEYS, ["like_counter"]],
  ])("allows engagement fields in compound sorts for %s", (_entity, keys, expectedKeys) => {
    expect(keys).toEqual(expect.arrayContaining(expectedKeys));
  });
});
