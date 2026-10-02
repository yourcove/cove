import { describe, expect, it } from "vitest";
import {
  buildSetupStepList,
  recordProgressSample,
  resolveOwnerBackStep,
  resolveOwnerNextStep,
  resolveStashSetupEntryStep,
} from "../pages/SetupWizardPage";

describe("Stash setup owner gate", () => {
  it("places owner creation before Stash configuration when an owner is missing", () => {
    expect(buildSetupStepList("stash", true)).toEqual(["welcome", "source", "owner", "stash-config", "theme", "done"]);
  });

  it("opens owner setup before Stash configuration only when needed", () => {
    expect(resolveStashSetupEntryStep(false)).toBe("owner");
    expect(resolveStashSetupEntryStep(true)).toBe("stash-config");
  });

  it("continues from a pre-import owner step into Stash configuration", () => {
    expect(resolveOwnerNextStep("stash", false)).toBe("stash-config");
    expect(resolveOwnerBackStep("stash", false)).toBe("source");
  });

  it("keeps post-content owner navigation unchanged for other setup paths", () => {
    expect(resolveOwnerNextStep("fresh", false)).toBe("theme");
    expect(resolveOwnerBackStep("fresh", false)).toBe("confirm");
    expect(resolveOwnerNextStep("backup", false)).toBe("theme");
    expect(resolveOwnerBackStep("backup", false)).toBe("backup-restore");
    expect(resolveOwnerNextStep("stash", true)).toBe("theme");
    expect(resolveOwnerBackStep("stash", true)).toBe("stash-config");
  });
});

describe("setup import progress samples", () => {
  it("records a sample only when progress has moved", () => {
    const history = recordProgressSample([], 1000, 0.1);
    expect(history).toEqual([{ time: 1000, progress: 0.1 }]);
    expect(recordProgressSample(history, 2000, 0.1)).toBe(history);
    expect(recordProgressSample(history, 3000, 0.2)).toEqual([
      { time: 1000, progress: 0.1 },
      { time: 3000, progress: 0.2 },
    ]);
  });

  it("ignores progress before the job reports any", () => {
    expect(recordProgressSample([], 1000, 0)).toEqual([]);
  });

  it("keeps only the samples from the last 30 seconds", () => {
    const history = [
      { time: 1000, progress: 0.1 },
      { time: 20000, progress: 0.2 },
    ];
    expect(recordProgressSample(history, 40000, 0.3)).toEqual([
      { time: 20000, progress: 0.2 },
      { time: 40000, progress: 0.3 },
    ]);
  });
});
