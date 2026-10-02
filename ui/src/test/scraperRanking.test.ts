import { describe, expect, it } from "vitest";
import type { ScraperSummary } from "../api/types";
import { isCatchAllScraperPattern, sortScrapersForVideo } from "../components/videoScrapeUtils";

function scraper(id: string, urls: string[], preferenceSites?: string[]): ScraperSummary {
  return {
    id,
    name: id,
    entityType: "video",
    supportedScrapes: ["URL"],
    urls,
    sourcePath: "builtin:test",
    preferenceSites,
  };
}

// A yt-dlp-style catch-all that also claims the site, next to a scraper built for it. The catch-all's pattern
// has more literal characters, so ranking by pattern length alone would put it first.
const catchAll = scraper("catch-all", ["https://*/*", "http://*/*"], ["tube.example"]);
const site = scraper("site", ["*.tube.example/*"]);

describe("sortScrapersForVideo", () => {
  it("puts the site scraper ahead of a catch-all scraper that claims the site", () => {
    const sorted = sortScrapersForVideo([catchAll, site], "https://www.tube.example/view_video.php?viewkey=1");

    expect(sorted.map((s) => s.id)).toEqual(["site", "catch-all"]);
  });

  it("honors a preference for a parent site on a subdomain", () => {
    const sorted = sortScrapersForVideo([site, catchAll], "https://de.tube.example/view_video.php?viewkey=1", [
      { entityType: "video", site: "tube.example", scraperId: "catch-all" },
    ]);

    expect(sorted[0].id).toBe("catch-all");
  });
});

describe("isCatchAllScraperPattern", () => {
  it.each([
    ["https://*/*", true],
    ["*", true],
    ["*.tube.example/*", false],
    ["tube.example/view_video.php", false],
  ])("%s -> %s", (pattern, expected) => {
    expect(isCatchAllScraperPattern(pattern)).toBe(expected);
  });
});
