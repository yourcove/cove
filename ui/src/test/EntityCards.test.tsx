import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

import { SceneCard, SceneCardPopovers } from "../components/EntityCards";
import { DetailsTab, FileInfoTab } from "../pages/SceneDetailPage";

const sceneFile = {
  id: 10,
  basename: "alpha.mp4",
  path: "C:\\library\\alpha.mp4",
  size: 1_048_576,
  duration: 120,
  width: 1920,
  height: 1080,
  frameRate: 29.97,
  bitRate: 2_400_000,
  videoCodec: "H264",
  audioCodec: "AAC",
  fingerprints: [],
};

const baseScene = {
  id: 42,
  title: "Sample Scene",
  updatedAt: "2024-01-12T00:00:00Z",
  files: [sceneFile],
  performers: [],
  groups: [],
  galleries: [],
  markers: [],
  tags: [],
  studioName: null,
  studioId: null,
  resumeTime: 0,
  rating: null,
  oCounter: 1,
  organized: false,
  details: null,
  date: null,
  playCount: 0,
};

beforeEach(() => {
  vi.restoreAllMocks();
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      observe() {}
      disconnect() {}
      unobserve() {}
    }
  );
  vi.spyOn(window, "open").mockImplementation(() => null);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("SceneCard navigation", () => {
  it("opens the routed scene URL on middle click", () => {
    const onClick = vi.fn();
    const { container } = render(<SceneCard scene={baseScene as any} onClick={onClick} />);

    fireEvent.mouseDown(container.firstElementChild as HTMLElement, { button: 1 });

    expect(window.open).toHaveBeenCalledWith("/scene/42", "_blank", "noopener,noreferrer");
    expect(onClick).not.toHaveBeenCalled();
  });

  it("opens the routed scene URL on ctrl-click instead of navigating in-place", () => {
    const onClick = vi.fn();
    const { container } = render(<SceneCard scene={baseScene as any} onClick={onClick} />);

    fireEvent.click(container.firstElementChild as HTMLElement, { ctrlKey: true });

    expect(window.open).toHaveBeenCalledWith("/scene/42", "_blank", "noopener,noreferrer");
    expect(onClick).not.toHaveBeenCalled();
  });

  it("opens performer badges in a new tab on middle click without triggering the parent scene card", () => {
    const onClick = vi.fn();
    const onNavigate = vi.fn();

    render(
      <SceneCard
        scene={{
          ...baseScene,
          performers: [{ id: 7, name: "Alice Example", imagePath: null }],
        } as any}
        onClick={onClick}
        onNavigate={onNavigate}
      />
    );

    fireEvent.mouseDown(screen.getByRole("button", { name: /Alice Example/i }), { button: 1 });

    expect(window.open).toHaveBeenCalledWith("/performer/7", "_blank", "noopener,noreferrer");
    expect(window.open).not.toHaveBeenCalledWith("/scene/42", "_blank", "noopener,noreferrer");
    expect(onClick).not.toHaveBeenCalled();
    expect(onNavigate).not.toHaveBeenCalled();
  });

  it("opens performer popover items in a new tab on middle click without triggering the scene card", () => {
    vi.useFakeTimers();

    render(
      <SceneCardPopovers
        scene={{
          ...baseScene,
          performers: [{ id: 9, name: "Popover Performer", imagePath: null }],
        } as any}
      />
    );

    fireEvent.mouseEnter(screen.getByTitle("Performers"));
    act(() => {
      vi.advanceTimersByTime(250);
    });

    fireEvent.mouseDown(screen.getByRole("button", { name: /Popover Performer/i }), { button: 1 });

    expect(window.open).toHaveBeenCalledWith("/performer/9", "_blank", "noopener,noreferrer");
    expect(window.open).not.toHaveBeenCalledWith("/scene/42", "_blank", "noopener,noreferrer");
  });

  it("renders a heart-based favorites counter instead of the legacy O badge", () => {
    render(<SceneCard scene={baseScene as any} onClick={vi.fn()} />);

    expect(screen.getByTitle("Favorites: 1")).toBeInTheDocument();
    expect(screen.queryByText(/^O$/)).not.toBeInTheDocument();
  });
});

describe("FileInfoTab", () => {
  it("renders every underlying scene file", () => {
    render(
      <FileInfoTab
        files={[
          sceneFile,
          {
            ...sceneFile,
            id: 11,
            basename: "beta.mp4",
            path: "D:\\archive\\beta.mp4",
          },
        ] as any}
      />
    );

    expect(screen.getByText("C:\\library\\alpha.mp4")).toBeInTheDocument();
    expect(screen.getByText("D:\\archive\\beta.mp4")).toBeInTheDocument();
    expect(screen.getByText("File 1 of 2")).toBeInTheDocument();
    expect(screen.getByText("File 2 of 2")).toBeInTheDocument();
  });
});

describe("DetailsTab performers", () => {
  it("shows performer age at scene date and uses a paired grid for multiple performers", () => {
    const scene = {
      ...baseScene,
      date: "2024-01-12",
      remoteIds: [],
      urls: [],
      customFields: undefined,
      performers: [
        { id: 7, name: "Alice Example", birthdate: "2000-01-10", imagePath: null },
        { id: 8, name: "Beth Example", birthdate: "1998-03-01", imagePath: null },
      ],
    };

    render(<DetailsTab scene={scene as any} onNavigate={vi.fn()} />);

    expect(screen.getByText("24 yrs old")).toBeInTheDocument();

    const performerGrid = screen.getByText("Performers").nextElementSibling as HTMLElement;
    expect(performerGrid.className).toContain("grid");
    expect(performerGrid.className).toContain("grid-cols-2");

    expect(screen.getByRole("button", { name: /Alice Example/i }).className).toContain("w-full");
    expect(screen.getByRole("button", { name: /Beth Example/i }).className).toContain("w-full");
  });
});
