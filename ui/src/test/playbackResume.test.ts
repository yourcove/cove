import { describe, expect, it } from "vitest";
import { normalizeStoredResumeTime } from "../utils/playbackResume";

describe("stored playback resume time", () => {
  it("restarts completed media from the beginning", () => {
    expect(normalizeStoredResumeTime(120, 120)).toBeUndefined();
    expect(normalizeStoredResumeTime(119.98, 120)).toBeUndefined();
  });

  it("preserves partial playback positions", () => {
    expect(normalizeStoredResumeTime(42, 120)).toBe(42);
    expect(normalizeStoredResumeTime(119.75, 120)).toBe(119.75);
    expect(normalizeStoredResumeTime(119, 120)).toBe(119);
  });

  it("ignores missing and non-positive positions", () => {
    expect(normalizeStoredResumeTime(undefined, 120)).toBeUndefined();
    expect(normalizeStoredResumeTime(0, 120)).toBeUndefined();
  });
});
