import { describe, expect, it } from "vitest";
import { formatAggregateDuration } from "../components/MediaAggregateMetadata";

describe("media aggregate formatting", () => {
  it("formats long durations without unbounded hour counts", () => {
    expect(formatAggregateDuration(552 * 86_400 + 21 * 3_600 + 56 * 60)).toBe("552d 21h 56m");
    expect(formatAggregateDuration(49 * 60 + 59)).toBe("49m 59s");
  });
});
