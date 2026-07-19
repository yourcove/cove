import { describe, expect, it } from "vitest";
import { orderManifestItems } from "../pages/CompilationPlayerPage";

describe("orderManifestItems", () => {
  const manifest = [
    { groupItemId: -1, hostType: "Segment", hostId: 101 },
    { groupItemId: -2, hostType: "Segment", hostId: 102 },
    { groupItemId: -3, hostType: "Segment", hostId: 103 },
  ];

  it("orders dynamic items by stable host identity when synthetic IDs differ", () => {
    const items = orderManifestItems(manifest, ["segment:103", "segment:101", "segment:102"]);

    expect(items.map((item) => item.hostId)).toEqual([103, 101, 102]);
  });

  it("orders static items by their group item identity", () => {
    const items = orderManifestItems(manifest, ["item:-2", "item:-3", "item:-1"]);

    expect(items.map((item) => item.groupItemId)).toEqual([-2, -3, -1]);
  });

  it("excludes manifest items outside the visible ordered subset", () => {
    const items = orderManifestItems(manifest, ["segment:103", "segment:101"]);

    expect(items.map((item) => item.hostId)).toEqual([103, 101]);
  });

  it("returns no manifest items for an explicitly empty visible order", () => {
    expect(orderManifestItems(manifest, [])).toEqual([]);
  });
});
