import { describe, expect, it } from "vitest";
import { GALLERY_MULTI_SORT_KEYS, GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";

describe("gallery sort options", () => {
  it("exposes first-class gallery metadata and relationship sorts", () => {
    expect(GALLERY_SORT_OPTIONS.map((option) => option.value)).toEqual([
      "updated_at",
      "created_at",
      "date",
      "studio",
      "file_mod_time",
      "file_count",
      "path",
      "title",
      "code",
      "photographer",
      "organized",
      "rating",
      "image_count",
      "video_count",
      "performer_count",
      "tag_count",
      "typical_resolution",
      "random",
    ]);
  });

  it("allows Rating in compound sorts", () => {
    expect(GALLERY_MULTI_SORT_KEYS).toContain("rating");
  });
});
