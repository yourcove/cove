import { describe, expect, it } from "vitest";
import { AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { FACE_SORT_OPTIONS } from "../components/faceSortOptions";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";
import { IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { RAW_SEGMENT_SORT_OPTIONS } from "../components/segmentSortOptions";
import { STUDIO_SORT_OPTIONS } from "../components/studioSortOptions";
import { TEXT_SORT_OPTIONS } from "../components/textSortOptions";

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
});
