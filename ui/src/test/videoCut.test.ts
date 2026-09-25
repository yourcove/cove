import { describe, expect, it } from "vitest";
import {
  formatCutParam,
  formatTimecode,
  keptSeconds,
  normalizeCutRanges,
  parseCutParam,
  parseTimecode,
} from "../utils/videoCut";
import { buildRouteUrl, parseCurrentRoute } from "../router/location";

describe("normalizeCutRanges", () => {
  it("sorts, clamps and merges overlapping or touching removals", () => {
    expect(
      normalizeCutRanges(
        [
          { start: 50, end: 40 },
          { start: 5, end: 10 },
          { start: -3, end: 2 },
          { start: 10, end: 12 },
          { start: 95, end: 130 },
        ],
        100,
      ),
    ).toEqual([
      { start: 0, end: 2 },
      { start: 5, end: 12 },
      { start: 40, end: 50 },
      { start: 95, end: 100 },
    ]);
  });

  it("drops slivers too short to cut", () => {
    expect(normalizeCutRanges([{ start: 10, end: 10.01 }], 100)).toEqual([]);
  });

  it("counts what is kept", () => {
    expect(
      keptSeconds(
        normalizeCutRanges(
          [
            { start: 0, end: 10 },
            { start: 90, end: 100 },
          ],
          100,
        ),
        100,
      ),
    ).toBe(80);
  });
});

describe("timecodes", () => {
  it("formats with tenths and hours only when needed", () => {
    expect(formatTimecode(0)).toBe("0:00.0");
    expect(formatTimecode(75.26)).toBe("1:15.3");
    expect(formatTimecode(3723.5)).toBe("1:02:03.5");
    expect(formatTimecode(59.96)).toBe("1:00.0");
  });

  it("parses what it formats, and plain seconds", () => {
    expect(parseTimecode("1:02:03.5")).toBe(3723.5);
    expect(parseTimecode("1:15.3")).toBeCloseTo(75.3);
    expect(parseTimecode("90")).toBe(90);
    expect(parseTimecode(" 12.25 ")).toBe(12.25);
    expect(parseTimecode("1:2:3:4")).toBeNull();
    expect(parseTimecode("abc")).toBeNull();
    expect(parseTimecode("")).toBeNull();
  });
});

describe("the cut link parameter", () => {
  it("round-trips removals", () => {
    const ranges = [
      { start: 0, end: 12.5 },
      { start: 300, end: 310.25 },
    ];
    expect(formatCutParam(ranges)).toBe("0-12.5,300-310.25");
    expect(parseCutParam("0-12.5,300-310.25")).toEqual(ranges);
  });

  it("rejects a malformed parameter as a whole rather than guessing", () => {
    expect(parseCutParam("0-12.5,oops")).toBeUndefined();
    expect(parseCutParam("20-10")).toBeUndefined();
    expect(parseCutParam("")).toBeUndefined();
  });

  it("carries removals on a video route", () => {
    const url = buildRouteUrl({ page: "video", id: 42, cut: [{ start: 0, end: 30 }] });
    expect(new URLSearchParams(url.split("?")[1]).get("cut")).toBe("0-30");
    window.history.replaceState(null, "", url);
    expect(parseCurrentRoute()).toMatchObject({ page: "video", id: 42, cut: [{ start: 0, end: 30 }] });
    window.history.replaceState(null, "", "/");
  });
});
