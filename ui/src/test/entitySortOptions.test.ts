import { describe, expect, it } from "vitest";
import { AUDIO_MULTI_SORT_KEYS, AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { FACE_SORT_OPTIONS } from "../components/faceSortOptions";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";
import { IMAGE_MULTI_SORT_KEYS, IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { RAW_SEGMENT_SORT_OPTIONS } from "../components/segmentSortOptions";
import { STUDIO_MULTI_SORT_KEYS, STUDIO_SORT_OPTIONS } from "../components/studioSortOptions";
import { TEXT_MULTI_SORT_KEYS, TEXT_SORT_OPTIONS } from "../components/textSortOptions";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { TAG_MULTI_SORT_KEYS, TAG_SORT_OPTIONS } from "../components/tagSortOptions";
import {
  AUDIO_MULTI_SORT_KEYS as CONTRACT_AUDIO_KEYS,
  GALLERY_MULTI_SORT_KEYS,
  IMAGE_MULTI_SORT_KEYS as CONTRACT_IMAGE_KEYS,
  PERFORMER_MULTI_SORT_KEYS,
  STUDIO_MULTI_SORT_KEYS as CONTRACT_STUDIO_KEYS,
  TAG_MULTI_SORT_KEYS as CONTRACT_TAG_KEYS,
  TEXT_MULTI_SORT_KEYS as CONTRACT_TEXT_KEYS,
  VIDEO_MULTI_SORT_KEYS,
} from "../components/entityMultiSortKeys";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";

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

  it.each([
    ["videos", VIDEO_MULTI_SORT_KEYS, VIDEO_SORT_OPTIONS],
    ["images", CONTRACT_IMAGE_KEYS, IMAGE_SORT_OPTIONS],
    ["audios", CONTRACT_AUDIO_KEYS, AUDIO_SORT_OPTIONS],
    ["texts", CONTRACT_TEXT_KEYS, TEXT_SORT_OPTIONS],
    ["galleries", GALLERY_MULTI_SORT_KEYS, GALLERY_SORT_OPTIONS],
    ["performers", PERFORMER_MULTI_SORT_KEYS, PERFORMER_SORT_OPTIONS],
    ["studios", CONTRACT_STUDIO_KEYS, STUDIO_SORT_OPTIONS],
    ["tags", CONTRACT_TAG_KEYS, TAG_SORT_OPTIONS],
  ])("keeps every %s compound key unique and visible in its sort options", (_entity, keys, options) => {
    expect(new Set(keys).size).toBe(keys.length);
    expect(options.map((option) => option.value)).toEqual(expect.arrayContaining(keys));
  });

  it.each([
    ["videos", VIDEO_MULTI_SORT_KEYS, VIDEO_SORT_OPTIONS, ["random", "phash", "performer_age"]],
    ["images", CONTRACT_IMAGE_KEYS, IMAGE_SORT_OPTIONS, ["random"]],
    ["audios", CONTRACT_AUDIO_KEYS, AUDIO_SORT_OPTIONS, ["random"]],
    ["texts", CONTRACT_TEXT_KEYS, TEXT_SORT_OPTIONS, ["random"]],
    ["galleries", GALLERY_MULTI_SORT_KEYS, GALLERY_SORT_OPTIONS, ["random", "typical_resolution"]],
    ["performers", PERFORMER_MULTI_SORT_KEYS, PERFORMER_SORT_OPTIONS, ["random", "career_length", "measurements"]],
    ["studios", CONTRACT_STUDIO_KEYS, STUDIO_SORT_OPTIONS, ["random"]],
    ["tags", CONTRACT_TAG_KEYS, TAG_SORT_OPTIONS, ["random", "tag_group"]],
  ])("lists every compound-capable %s option in the shared contract", (_entity, keys, options, singleSortOnlyKeys) => {
    const eligibleOptionKeys = options
      .map((option) => option.value)
      .filter((key) => !singleSortOnlyKeys.includes(key));

    expect(new Set(keys)).toEqual(new Set(eligibleOptionKeys));
  });

  it("re-exports tag compound keys from the tag catalog", () => {
    expect(TAG_MULTI_SORT_KEYS).toBe(CONTRACT_TAG_KEYS);
  });
});
