import { describe, expect, it, vi } from "vitest";

// Distinct values, so a URL wired to the wrong family fails; 0 checks that an unbumped family adds nothing.
vi.mock("../api/mediaCacheRevisions.json", () => ({
  default: { libraryImages: 11, entityImages: 12, segmentCovers: 13, videoScreenshots: 14, detectionCrops: 0 },
}));

import { entityImages, faces, galleries, images, videos } from "../api/client";
import { mediaCacheRevision } from "../api/mediaCacheRevisions";

const revisionOf = (url: string) => new URL(url, "http://cove.test").searchParams.get("r");

describe("media cache revisions", () => {
  it("gives each media URL its own family's revision", () => {
    const cases: [string, string | null][] = [
      [images.imageUrl(1), "11"],
      [images.thumbnailUrl(1, 640), "11"],
      [entityImages.tagImageUrl(1, "v1"), "12"],
      [entityImages.videoCoverUrl(1, "v1"), "12"],
      [entityImages.groupBackImageUrl(1, "v1"), "12"],
      [galleries.coverUrl(1, "v1"), "12"],
      [galleries.getCoverImageUrl(1, "v1"), "12"],
      [entityImages.segmentCoverUrl(1, "v1"), "13"],
      [videos.screenshotUrl(1, "v1", 2), "14"],
      [faces.detectionCropUrl(1, 640), null],
    ];

    for (const [url, revision] of cases) {
      expect(revisionOf(url), url).toBe(revision);
    }
  });

  it("leaves URLs outside the families unchanged", () => {
    expect(revisionOf(videos.streamUrl(1))).toBeNull();
    expect(revisionOf(videos.previewUrl(1))).toBeNull();
  });

  it("adds the parameter only for a positive revision", () => {
    expect(mediaCacheRevision("libraryImages")).toEqual({ r: 11 });
    expect(mediaCacheRevision("detectionCrops")).toEqual({});
  });
});
