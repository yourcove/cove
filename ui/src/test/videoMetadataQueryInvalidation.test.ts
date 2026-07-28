import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { invalidateVideoMetadataQueries } from "../components/videoMetadataQueryInvalidation";

describe("invalidateVideoMetadataQueries", () => {
  it("invalidates cached relationship views immediately after a video metadata import", async () => {
    const queryClient = new QueryClient();
    const affectedQueryKeys = [
      ["video", 42],
      ["videos", { page: 1 }],
      ["videos-popover", [42]],
      ["performer-videos", 7, {}, "page", { page: 1 }],
      ["performer-appears-with", 7, { page: 1 }],
      ["performers-popover", [7]],
      ["studio-videos", 8, false, {}],
      ["studio-performers", 8, false, {}],
      ["studios-popover", [8]],
      ["tag-videos", 9, {}, false],
      ["performer", 7],
      ["performers", { page: 1 }],
      ["studio", 8],
      ["studios", { page: 1 }],
      ["tag", 9],
      ["tags", { page: 1 }],
    ] as const;

    for (const queryKey of affectedQueryKeys) {
      queryClient.setQueryData(queryKey, { cached: true });
    }
    queryClient.setQueryData(["images"], { cached: true });

    await invalidateVideoMetadataQueries(queryClient, 42);

    for (const queryKey of affectedQueryKeys) {
      expect(queryClient.getQueryState(queryKey)?.isInvalidated, JSON.stringify(queryKey)).toBe(true);
    }
    expect(queryClient.getQueryState(["images"])?.isInvalidated).toBe(false);
  });
});
