import { describe, expect, it } from "vitest";
import type { PerformerPairingCoStar, PerformerPairings, PerformerPairingVideo } from "../api/types";
import {
  buildPairings,
  facetPairings,
  filterPairings,
  lineupVideos,
  pairingTier,
  parseIdList,
  parsePositiveInt,
  sharedVideosRoute,
  sortPairings,
  summarizePairings,
  UNKNOWN_GENDER,
  videoYear,
} from "../utils/performerPairings";

const FOCAL = 1;

function coStar(id: number, name: string, extra: Partial<PerformerPairingCoStar> = {}): PerformerPairingCoStar {
  return { id, name, favorite: false, videoCount: 0, ...extra };
}

function video(id: number, cast: number[], date: string | null, studio?: [number, string]): PerformerPairingVideo {
  return {
    id,
    title: `Video ${id}`,
    date,
    studioId: studio?.[0] ?? null,
    studioName: studio?.[1] ?? null,
    duration: 600,
    width: 1920,
    height: 1080,
    updatedAt: "2026-01-01T00:00:00.0000000Z",
    performerIds: [FOCAL, ...cast].sort((a, b) => a - b),
  };
}

// Partner (2) shares five duos and one trio with Guest (3); Guest also has an older duo; Newcomer (4)
// and Unknown (5) each share one undated video, and Unknown has no gender set.
const data: PerformerPairings = {
  performerId: FOCAL,
  videoCount: 12,
  soloVideoYears: { "2008": 1, "2016": 1 },
  videos: [
    video(10, [2], "2016-11-04", [7, "Meridian"]),
    video(11, [2], "2016-02-21", [7, "Meridian"]),
    video(12, [2, 3], "2015", [8, "Lumen"]),
    video(13, [2], "2014-03-02", [7, "Meridian"]),
    video(14, [2], "2013-06", [8, "Lumen"]),
    video(15, [2], "2012-01-14", [7, "Meridian"]),
    video(16, [3], "2009-05-01"),
    video(17, [4], null),
    video(18, [5], "0001-01-01"),
  ],
  coStars: [
    coStar(2, "Partner", { gender: "Female", favorite: true, videoCount: 20 }),
    coStar(3, "Guest", { gender: "Male", disambiguation: "the elder" }),
    coStar(4, "Newcomer", { gender: "Female" }),
    coStar(5, "Unknown"),
  ],
};

describe("buildPairings", () => {
  it("counts each co-star's shared videos with their duo split, years and studios", () => {
    const pairings = buildPairings(data, false);
    const partner = pairings.find((pairing) => pairing.coStar.id === 2)!;

    expect(pairings.map((pairing) => pairing.coStar.id).sort()).toEqual([2, 3, 4, 5]);
    expect(partner.count).toBe(6);
    expect(partner.duoCount).toBe(5);
    expect(partner.groupCount).toBe(1);
    expect(partner.firstYear).toBe(2012);
    expect(partner.lastYear).toBe(2016);
    expect(partner.firstDate).toBe("2012-01-14");
    expect(partner.lastDate).toBe("2016-11-04");
    expect([...partner.yearCounts.entries()].sort()).toEqual([
      [2012, 1],
      [2013, 1],
      [2014, 1],
      [2015, 1],
      [2016, 2],
    ]);
    expect(partner.studios).toEqual([
      { id: 7, name: "Meridian", count: 4 },
      { id: 8, name: "Lumen", count: 2 },
    ]);
    expect(partner.videos.map((shared) => shared.id)).toEqual([10, 11, 12, 13, 14, 15]);
  });

  it("drops videos with anyone else in duo mode, and co-stars left with none", () => {
    const pairings = buildPairings(data, true);
    const byId = new Map(pairings.map((pairing) => [pairing.coStar.id, pairing]));

    expect(byId.get(2)?.count).toBe(5);
    expect(byId.get(2)?.groupCount).toBe(0);
    expect(byId.get(3)?.videos.map((shared) => shared.id)).toEqual([16]);
  });

  it("keeps favorites only, and matches a search on the name or disambiguation", () => {
    const pairings = buildPairings(data, false);
    const ids = (filters: { favoritesOnly?: boolean; query?: string }) =>
      filterPairings(pairings, { favoritesOnly: false, query: "", ...filters })
        .map((pairing) => pairing.coStar.id)
        .sort();

    expect(ids({ favoritesOnly: true })).toEqual([2]);
    expect(ids({ query: "  ELDER " })).toEqual([3]);
    expect(filterPairings(pairings, { favoritesOnly: false, query: "e" })[0]).toBe(
      pairings.find((pairing) => pairing.coStar.name.toLowerCase().includes("e")),
    );
  });

  it("treats an implausible date as no date", () => {
    const unknown = buildPairings(data, false).find((pairing) => pairing.coStar.id === 5)!;

    expect(videoYear({ date: "0001-01-01" })).toBeNull();
    expect(videoYear({ date: "1999" })).toBe(1999);
    expect(unknown.firstYear).toBeNull();
    expect(unknown.yearCounts.size).toBe(0);
  });
});

describe("facetPairings", () => {
  const pairings = buildPairings(data, false);

  it("puts co-stars in tiers by videos together", () => {
    expect(pairings.map((pairing) => [pairing.coStar.name, pairingTier(pairing)]).sort()).toEqual([
      ["Guest", "recurring"],
      ["Newcomer", "once"],
      ["Partner", "frequent"],
      ["Unknown", "once"],
    ]);
  });

  it("counts each chip row with the other row's choice applied", () => {
    const female = facetPairings(pairings, { gender: "Female", tier: null });
    const once = facetPairings(pairings, { gender: null, tier: "once" });

    expect(female.visible.map((pairing) => pairing.coStar.name).sort()).toEqual(["Newcomer", "Partner"]);
    expect(female.tierCounts).toEqual({ frequent: 1, recurring: 0, once: 1 });
    expect(female.genderCounts).toEqual([
      { gender: "Female", count: 2 },
      { gender: "Male", count: 1 },
      { gender: UNKNOWN_GENDER, count: 1 },
    ]);
    expect(once.visible.map((pairing) => pairing.coStar.name).sort()).toEqual(["Newcomer", "Unknown"]);
    expect(once.tierCounts).toEqual({ frequent: 1, recurring: 1, once: 2 });
    expect(once.genderCounts).toEqual([
      { gender: "Female", count: 1 },
      { gender: UNKNOWN_GENDER, count: 1 },
    ]);
  });
});

describe("sortPairings", () => {
  const pairings = buildPairings(data, false);
  const order = (sort: Parameters<typeof sortPairings>[1]) =>
    sortPairings(pairings, sort).map((pairing) => pairing.coStar.name);

  it("orders by videos together, then by the latest video together", () => {
    expect(order("together")).toEqual(["Partner", "Guest", "Newcomer", "Unknown"]);
  });

  it("puts co-stars without dates last whichever way dates run", () => {
    expect(order("recent")).toEqual(["Partner", "Guest", "Newcomer", "Unknown"]);
    expect(order("first")).toEqual(["Guest", "Partner", "Newcomer", "Unknown"]);
  });

  it("orders by years together and by name", () => {
    expect(order("span")).toEqual(["Guest", "Partner", "Newcomer", "Unknown"]);
    expect(order("name")).toEqual(["Guest", "Newcomer", "Partner", "Unknown"]);
  });
});

describe("summarizePairings", () => {
  it("spans the performer's dated videos, solo ones included, ignoring implausible dates", () => {
    expect(summarizePairings(data)).toEqual({ videoCount: 12, sharedVideoCount: 9, firstYear: 2008, lastYear: 2016 });
  });
});

describe("lineupVideos", () => {
  it("finds the videos with every chosen co-star and those with any of them", () => {
    const lineup = lineupVideos(data, [2, 3]);

    expect(lineup.all.map((shared) => shared.id)).toEqual([12]);
    expect(lineup.any.map((shared) => shared.id)).toEqual([10, 11, 12, 13, 14, 15, 16]);
    expect(lineupVideos(data, [])).toEqual({ all: [], any: [] });
  });

  it("counts only duos in duo mode", () => {
    expect(lineupVideos(data, [2], true).all.map((shared) => shared.id)).toEqual([10, 11, 13, 14, 15]);
    expect(lineupVideos(data, [2, 3], true).all).toEqual([]);
    expect(lineupVideos(data, [2, 3], true).any.map((shared) => shared.id)).toEqual([10, 11, 13, 14, 15, 16]);
  });
});

describe("parseIdList", () => {
  it("reads ids in order, skipping repeats and anything that is not a positive whole number", () => {
    expect(parseIdList("3,1,3,x,-2,0,1.5,7")).toEqual([3, 1, 7]);
    expect(parseIdList(null)).toEqual([]);
  });
});

describe("paging", () => {
  it("reads a page or page size only when it is a positive whole number", () => {
    expect(parsePositiveInt("3")).toBe(3);
    for (const value of [null, undefined, "", "0", "-2", "1.5", "x"]) expect(parsePositiveInt(value)).toBeNull();
  });
});

describe("sharedVideosRoute", () => {
  it("opens the performer's Videos tab filtered to the co-stars", () => {
    expect(sharedVideosRoute(FOCAL, [2])).toEqual({
      page: "performer",
      id: FOCAL,
      detailTab: "videos",
      listFilter: { q: "", page: 1 },
      listObjectFilter: { performersCriterion: { value: [2], modifier: "INCLUDES" } },
    });
    expect(sharedVideosRoute(FOCAL, [2, 3]).listObjectFilter).toEqual({
      performersCriterion: { value: [2, 3], modifier: "INCLUDES_ALL" },
    });
    expect(sharedVideosRoute(FOCAL, [2, 3], { match: "any" }).listObjectFilter).toEqual({
      performersCriterion: { value: [2, 3], modifier: "INCLUDES" },
    });
  });

  it("limits the list to duos when duo mode is on", () => {
    expect(sharedVideosRoute(FOCAL, [2], { duoOnly: true }).listObjectFilter).toEqual({
      performersCriterion: { value: [2], modifier: "INCLUDES" },
      performerCountCriterion: { value: 2, modifier: "EQUALS" },
    });
  });
});
