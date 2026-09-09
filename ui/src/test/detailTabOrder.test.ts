import { describe, expect, it } from "vitest";
import { orderDetailTabsByMenuItems } from "../utils/detailTabOrder";

describe("orderDetailTabsByMenuItems", () => {
  it("orders shared detail tabs by the configured main menu and keeps detail-only tabs afterward", () => {
    const tabs = [
      { key: "videos", label: "Videos" },
      { key: "galleries", label: "Galleries" },
      { key: "images", label: "Images" },
      { key: "audios", label: "Audios" },
      { key: "texts", label: "Texts" },
      { key: "groups", label: "Groups" },
      { key: "faces", label: "Faces" },
      { key: "appearsWith", label: "Appears With" },
      { key: "similar", label: "Similar" },
    ];

    const ordered = orderDetailTabsByMenuItems(tabs, [
      "videos",
      "images",
      "audios",
      "texts",
      "galleries",
      "segments",
      "performers",
      "tags",
      "groups",
      "studios",
      "faces",
    ]);

    expect(ordered.map((tab) => tab.key)).toEqual([
      "videos",
      "images",
      "audios",
      "texts",
      "galleries",
      "groups",
      "faces",
      "appearsWith",
      "similar",
    ]);
  });

  it("preserves the fallback order when no menu order is configured", () => {
    const tabs = [{ key: "images" }, { key: "videos" }, { key: "fileinfo" }];

    expect(orderDetailTabsByMenuItems(tabs, [])).toBe(tabs);
  });

  it("keeps tabs omitted from the configured menu in their existing relative order", () => {
    const tabs = [{ key: "videos" }, { key: "galleries" }, { key: "appearsWith" }, { key: "similar" }];

    expect(orderDetailTabsByMenuItems(tabs, ["galleries", "videos"]).map((tab) => tab.key)).toEqual([
      "galleries",
      "videos",
      "appearsWith",
      "similar",
    ]);
  });
});
