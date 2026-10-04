import { act, cleanup, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { JobInfo } from "../api/types";
import {
  buildSetupStepList,
  recordProgressSample,
  resolveOwnerBackStep,
  resolveOwnerNextStep,
  resolveStashSetupEntryStep,
  SetupImportProgressCard,
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

describe("setup import progress card", () => {
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  const startedAt = new Date(0).toISOString();
  const runningJob = (progress: number): JobInfo => ({
    id: "job-1",
    type: "stash-import",
    description: "Import from Stash",
    status: "running",
    progress,
    startedAt,
  });

  it("keeps rendering while it samples progress for a running import", () => {
    vi.useFakeTimers({ now: 0 });
    const { rerender } = render(createElement(SetupImportProgressCard, { job: runningJob(0.1) }));

    act(() => {
      vi.advanceTimersByTime(2000);
    });
    rerender(createElement(SetupImportProgressCard, { job: runningJob(0.2) }));
    act(() => {
      vi.advanceTimersByTime(1000);
    });

    expect(screen.getByText("Import from Stash")).toBeInTheDocument();
    expect(screen.getByText("~16s remaining")).toBeInTheDocument();
  });
});
