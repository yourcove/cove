import { describe, expect, it } from "vitest";
import { PERFORMER_MULTI_SORT_KEYS, PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";

describe("PERFORMER_SORT_OPTIONS", () => {
  it("includes the full performer sort set without duplicates", () => {
    const sortByValue = new Map(PERFORMER_SORT_OPTIONS.map((option) => [option.value, option.label]));

    expect(sortByValue.get("career_length")).toBe("Career Length");
    expect(sortByValue.get("last_like_at")).toBe("Last Like At");
    expect(sortByValue.get("last_played_at")).toBe("Last Played At");
    expect(sortByValue.get("measurements")).toBe("Measurements");
    expect(sortByValue.get("like_counter")).toBe("Likes");
    expect(sortByValue.get("play_count")).toBe("Play Count");
    expect(sortByValue.get("audio_count")).toBe("Audio Count");
    expect(sortByValue.get("text_count")).toBe("Text Count");
    expect(sortByValue.get("random")).toBe("Random");
    expect(sortByValue.size).toBe(PERFORMER_SORT_OPTIONS.length);
  });

  it("allows personalized scores in compound sorts", () => {
    expect(PERFORMER_MULTI_SORT_KEYS).toEqual(expect.arrayContaining([
      "rating",
      "like_counter",
      "play_count",
      "last_like_at",
      "last_played_at",
      "audio_count",
      "text_count",
    ]));
  });
});
