import { describe, expect, it } from "vitest";
import { getPerformerAge } from "../utils/performerAge";

describe("getPerformerAge", () => {
  const today = "2026-08-30";

  it("calculates completed years for living performers", () => {
    expect(getPerformerAge("2000-08-30", undefined, today)).toBe(26);
    expect(getPerformerAge("2000-08-31", undefined, today)).toBe(25);
  });

  it("stops age at a past death date", () => {
    expect(getPerformerAge("1994-08-23", "2017-12-05", today)).toBe(23);
  });

  it("preserves possible age ranges for partial birth and death dates", () => {
    expect(getPerformerAge("1994", "2017", today)).toBe("22–23");
    expect(getPerformerAge("1994-08", "2017-12", today)).toBe(23);
  });

  it("caps a future death date at today", () => {
    expect(getPerformerAge("2000-08-30", "2030-01-01", today)).toBe(26);
  });

  it("supports partial birth dates and rejects malformed or reversed dates", () => {
    expect(getPerformerAge("2000", undefined, today)).toBe("25–26");
    expect(getPerformerAge("2000-02-30", undefined, today)).toBeNull();
    expect(getPerformerAge("2000-01-01", "1999-12-31", today)).toBeNull();
  });
});
