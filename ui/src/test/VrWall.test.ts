import { describe, expect, it } from "vitest";
import {
  claimWallSession,
  dropWallSession,
  hasPendingWallSession,
  lastVrList,
  listKey,
  offerWallSession,
  pageLabel,
  registerVrList,
  vrOnlyFilter,
  type VrListSource,
} from "../vr/vrListRegistry";
import { cardDetails, resolutionLabel, wallPageFor } from "../vr/VrWall";

const source: VrListSource = {
  label: "Videos",
  key: "videos",
  page: 2,
  perPage: 24,
  fetchPage: async () => ({ items: [], totalCount: 0, page: 1, perPage: 24 }),
};

describe("VR list registry", () => {
  it("remembers the list last shown and where it lives", () => {
    window.history.replaceState(null, "", "/videos?page=2");
    registerVrList(source);
    expect(lastVrList()).toEqual({ source, url: "/videos?page=2" });
  });

  it("hands a session only to the page it was left for, once", () => {
    const session = {};
    offerWallSession(session, "/tags/5");
    expect(hasPendingWallSession()).toBe(true);
    expect(claimWallSession("/videos")).toBeNull();
    expect(claimWallSession("/tags/5")).toBe(session);
    expect(claimWallSession("/tags/5")).toBeNull();
    expect(hasPendingWallSession()).toBe(false);
  });

  it("drops an unclaimed session and says whether it was still waiting", () => {
    const session = {};
    offerWallSession(session, "/videos");
    expect(dropWallSession({})).toBe(false);
    expect(dropWallSession(session)).toBe(true);
    expect(dropWallSession(session)).toBe(false);
  });

  it("keys a list by its path and filters, not its page", () => {
    window.history.replaceState(null, "", "/tags/5?page=3");
    const a = listKey({ page: 3, perPage: 24, sort: "date" }, { tagsCriterion: [5] });
    const b = listKey({ page: 4, perPage: 40, sort: "date" }, { tagsCriterion: [5] });
    const c = listKey({ page: 3, perPage: 24, sort: "rating" }, { tagsCriterion: [5] });
    expect(a).toBe(b);
    expect(a).not.toBe(c);
  });

  it("labels the wall after the page", () => {
    document.title = "Brunette | Cove";
    expect(pageLabel()).toBe("Brunette");
    document.title = "";
    expect(pageLabel("Videos")).toBe("Videos");
  });
});

describe("VR wall cards", () => {
  it("summarises a video like the list cards do", () => {
    const details = cardDetails({
      rating: 90,
      studioName: "SLR",
      date: "2024-05-01",
      primaryFileId: 7,
      files: [{ id: 7, basename: "a.mp4", duration: 754, width: 5400, height: 2700 } as never],
    });
    expect(details).toBe("★ 4.5  ·  SLR  ·  2024-05-01  ·  12:34  ·  5K");
  });

  it("starts on the wall page holding the browser page's first item", () => {
    expect(wallPageFor({ page: 1, perPage: 24 }, 18)).toBe(1);
    expect(wallPageFor({ page: 2, perPage: 24 }, 18)).toBe(2); // items 24.. sit on wall page 2 (18..35)
    expect(wallPageFor({ page: 3, perPage: 40 }, 18)).toBe(5); // items 80.. sit on wall page 5 (72..89)
    expect(wallPageFor({ page: 3, perPage: 40 }, 8)).toBe(11); // large cards: 8 a page
  });

  it("restricts to VR videos on top of whatever the list filters by", () => {
    expect(vrOnlyFilter({ tagsCriterion: { value: [1] } })).toEqual({
      tagsCriterion: { value: [1] },
      isVrCriterion: { value: true },
    });
  });

  it("names resolutions by the larger side", () => {
    expect(resolutionLabel(7680, 3840)).toBe("8K");
    expect(resolutionLabel(1920, 1080)).toBe("1080p");
    expect(resolutionLabel(640, 480)).toBe("480p");
  });
});
