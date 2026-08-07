import { describe, expect, it } from "vitest";
import { formatHumanDuration } from "../utils/durationFormat";

describe("formatHumanDuration", () => {
  it("preserves duration precision with readable units", () => {
    expect(formatHumanDuration(0)).toBe("0 sec");
    expect(formatHumanDuration(1)).toBe("1 sec");
    expect(formatHumanDuration(90)).toBe("1 min 30 sec");
    expect(formatHumanDuration(3660)).toBe("1 hr 1 min");
    expect(formatHumanDuration(-1)).toBe("-1 sec");
  });
});
