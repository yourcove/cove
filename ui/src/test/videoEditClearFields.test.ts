import { describe, expect, it } from "vitest";
import { videoEditClearFields } from "../utils/videoEditClearFields";

describe("videoEditClearFields", () => {
  it("marks cleared date and studio values", () => {
    expect(videoEditClearFields("", undefined)).toEqual(["date", "studioId"]);
  });

  it("does not mark populated values", () => {
    expect(videoEditClearFields("2026-05-01", 9)).toEqual([]);
  });
});
