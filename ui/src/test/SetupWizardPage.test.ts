import { describe, expect, it } from "vitest";
import {
  buildSetupStepList,
  resolveOwnerBackStep,
  resolveOwnerNextStep,
  resolveStashSetupEntryStep,
} from "../pages/SetupWizardPage";

describe("Stash setup owner gate", () => {
  it("places owner creation before Stash configuration when an owner is missing", () => {
    expect(buildSetupStepList("stash", true)).toEqual([
      "welcome",
      "source",
      "owner",
      "stash-config",
      "theme",
      "done",
    ]);
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
