import { describe, expect, it } from "vitest";
import { PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";

describe("PERFORMER_SORT_OPTIONS", () => {
  it("includes the full performer sort set without duplicates", () => {
    const sortByValue = new Map(PERFORMER_SORT_OPTIONS.map((option) => [option.value, option.label]));

    expect(sortByValue.get("career_length")).toBe("Career Length");
    expect(sortByValue.get("last_o_at")).toBe("Last Favorite At");
    expect(sortByValue.get("last_played_at")).toBe("Last Played At");
    expect(sortByValue.get("measurements")).toBe("Measurements");
    expect(sortByValue.get("o_counter")).toBe("Favorites");
    expect(sortByValue.get("play_count")).toBe("Play Count");
    expect(sortByValue.get("random")).toBe("Random");
    expect(sortByValue.size).toBe(PERFORMER_SORT_OPTIONS.length);
  });
});