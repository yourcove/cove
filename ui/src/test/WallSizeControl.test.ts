import { describe, expect, it } from "vitest";
import {
  clampWallSizeLevel,
  getWallColumnCountFromSizeLevel,
  getWallSizeLevelFromColumnCount,
} from "../components/WallSizeControl";

describe("wall size conversions", () => {
  it("maps larger size levels to fewer columns", () => {
    expect(getWallColumnCountFromSizeLevel(2)).toBe(8);
    expect(getWallColumnCountFromSizeLevel(5)).toBe(5);
    expect(getWallColumnCountFromSizeLevel(8)).toBe(2);
    expect(getWallSizeLevelFromColumnCount(8)).toBe(2);
    expect(getWallSizeLevelFromColumnCount(2)).toBe(8);
  });

  it("rounds and clamps legacy zoom values", () => {
    expect(clampWallSizeLevel(0)).toBe(2);
    expect(clampWallSizeLevel(5.25)).toBe(5);
    expect(clampWallSizeLevel(20)).toBe(8);
  });
});
