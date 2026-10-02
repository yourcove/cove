import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TagGraphNode } from "../api/types";
import { TagGraphView } from "../components/TagGraphView";

const PREFS_KEY = "cove-tag-graph-prefs";

function node(id: number, name: string): TagGraphNode {
  return {
    id,
    name,
    favorite: false,
    parentIds: [],
    childIds: [],
    totalUsageCount: 1,
    videoCount: 1,
    segmentCount: 0,
    imageCount: 0,
    galleryCount: 0,
    groupCount: 0,
    performerCount: 0,
    studioCount: 0,
  };
}

function renderGraph() {
  return render(
    <TagGraphView nodes={[node(1, "Alpha"), node(2, "Beta")]} links={[]} totalCount={2} onNavigate={vi.fn()} />,
  );
}

describe("TagGraphView layout preferences", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal(
      "ResizeObserver",
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it("restores persisted layout tuning on first render and keeps it persisted", () => {
    localStorage.setItem(PREFS_KEY, JSON.stringify({ layoutSettings: { nodeScale: 0.3 }, showLayoutTuning: true }));

    renderGraph();

    expect(screen.getByText(/Node Size 30%/)).toBeInTheDocument();
    expect(screen.getByText(/Tune the absolute node size range/)).toBeInTheDocument();
    const stored = JSON.parse(localStorage.getItem(PREFS_KEY)!);
    expect(stored.showLayoutTuning).toBe(true);
    expect(stored.layoutSettings.nodeScale).toBe(0.3);
  });

  it("falls back to defaults when the persisted preferences are invalid", () => {
    localStorage.setItem(PREFS_KEY, "{not json");

    renderGraph();

    expect(screen.queryByText(/Tune the absolute node size range/)).not.toBeInTheDocument();
    const stored = JSON.parse(localStorage.getItem(PREFS_KEY)!);
    expect(stored.showLayoutTuning).toBe(false);
  });

  it("resizes visible labels as soon as the label size changes", () => {
    localStorage.setItem(
      PREFS_KEY,
      JSON.stringify({ layoutSettings: { labelDensity: 1, labelSize: 1 }, showLayoutTuning: true }),
    );

    const { container } = renderGraph();
    const labelFontSize = () =>
      Number(
        [...container.querySelectorAll("text")].find((text) => text.textContent === "Alpha")?.getAttribute("font-size"),
      );
    const initialFontSize = labelFontSize();
    expect(initialFontSize).toBeGreaterThan(0);

    const labelSizeInput = screen.getByText("Label Size").closest("label")!.querySelector("input")!;
    fireEvent.change(labelSizeInput, { target: { value: "1.5" } });

    expect(labelFontSize()).toBeCloseTo(initialFontSize * 1.5);
  });
});
