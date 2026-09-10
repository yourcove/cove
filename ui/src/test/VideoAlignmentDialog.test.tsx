import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { VideoAlignmentDialog } from "../components/VideoAlignmentDialog";

const api = vi.hoisted(() => ({ assess: vi.fn(), analyze: vi.fn(), preview: vi.fn(), apply: vi.fn() }));
vi.mock("../api/client", () => ({
  videoAlignments: api,
  videos: { streamUrl: (id: number, fileId?: number) => `/stream/video/${id}?fileId=${fileId}` },
}));

const assessment = {
  sourceFileId: 10,
  targetFileId: 11,
  sourceDuration: 30,
  targetDuration: 36,
  equivalent: false,
  canAlign: true,
  dependencyCount: 3,
  dependencyCounts: { segment: 1, clip: 2 },
};
const anchors = [
  { sourceSec: 0, targetSec: 6, section: 0 },
  { sourceSec: 20, targetSec: 26, section: 0 },
];

describe("primary-file alignment", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.resetAllMocks();
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue();
    vi.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(() => {});
    vi.spyOn(window, "confirm").mockReturnValue(true);
    api.assess.mockResolvedValue(assessment);
    api.analyze.mockResolvedValue({ anchors, sampleStep: 0.5, message: "matches" });
    api.preview.mockResolvedValue({
      message: "resolve all",
      comparisonCounts: { segment: 1, clip: 2 },
      comparisons: [
        {
          kind: "segment",
          id: 8,
          title: "Opening",
          sourceSec: 4,
          targetSec: 10,
          sourceThumbnail: "/9j/old",
          targetThumbnail: "/9j/new",
        },
        {
          kind: "clip",
          id: 1,
          title: "Mapped",
          sourceSec: 3.5,
          targetSec: 9.5,
          sourceThumbnail: "/9j/old",
          targetThumbnail: "/9j/new",
        },
      ],
      dependencies: [
        { kind: "segment", id: 8, title: "Opening", startSec: 2, endSec: 6, mapped: { startSec: 8, endSec: 12 } },
        { kind: "clip", id: 1, title: "Mapped", startSec: 2, endSec: 5, mapped: { startSec: 8, endSec: 11 } },
        { kind: "clip", id: 2, title: "Missing", startSec: 25, endSec: 29, mapped: null },
      ],
    });
    api.apply.mockResolvedValue({ primaryFileId: 11, mapped: 1, deleted: 1 });
  });

  it("automatically presents paired content previews and requires every unresolved dependency to be removed", async () => {
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} onApplied={vi.fn()} />);
    await screen.findByText(/selected file does not match the current primary file/i);
    expect(screen.queryByText(/video has 1 segment and 2 clip videos/i)).not.toBeInTheDocument();
    const affected = screen.getByRole("list", { name: "Affected timed content" });
    expect(within(affected).getByRole("listitem", { name: "1 segment" })).toBeInTheDocument();
    expect(within(affected).getByRole("listitem", { name: "2 clip videos" })).toBeInTheDocument();
    expect(within(affected).getByTitle("Segment")).toBeInTheDocument();
    expect(within(affected).getByTitle("Clip video")).toBeInTheDocument();
    expect(screen.getByText(/compare samples.*consistent timeline offset.*side-by-side previews/i)).toBeInTheDocument();
    expect(screen.getByText(/permanently remove all affected timed content.*cannot be undone/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Use automated matching" }));
    await screen.findByRole("heading", { name: "Review alignment" });
    expect(api.preview).toHaveBeenCalledWith(42, expect.objectContaining({ anchors }), expect.any(AbortSignal));
    expect(screen.getByTitle("Segment")).toBeInTheDocument();
    expect(screen.getByTitle("Clip video")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Segment: Opening" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Clip: Mapped" })).toBeInTheDocument();
    expect(screen.getByText(/plays both timelines together/i)).toBeInTheDocument();
    await waitFor(() => expect(document.querySelectorAll("video")).toHaveLength(2));
    const streams = [...document.querySelectorAll("video")].map((video) => video.getAttribute("src"));
    expect(streams).toEqual(["/stream/video/42?fileId=10", "/stream/video/42?fileId=11"]);
    const [sourceVideo, targetVideo] = [...document.querySelectorAll("video")];
    Object.defineProperty(sourceVideo, "readyState", { configurable: true, value: 4 });
    Object.defineProperty(targetVideo, "readyState", { configurable: true, value: 4 });
    Object.defineProperty(sourceVideo, "seeking", { configurable: true, value: true });
    Object.defineProperty(targetVideo, "seeking", { configurable: true, value: true });
    const play = vi.mocked(HTMLMediaElement.prototype.play);
    play.mockClear();
    play.mockRejectedValueOnce(new Error("temporary playback failure"));
    fireEvent.canPlay(sourceVideo);
    fireEvent.canPlay(targetVideo);
    expect(play).not.toHaveBeenCalled();
    fireEvent.seeked(sourceVideo);
    expect(play).not.toHaveBeenCalled();
    fireEvent.seeked(targetVideo);
    await waitFor(() => expect(play).toHaveBeenCalledTimes(2));
    await new Promise((resolve) => setTimeout(resolve, 300));
    fireEvent.seeked(sourceVideo);
    fireEvent.seeked(targetVideo);
    await waitFor(() => expect(play).toHaveBeenCalledTimes(4));
    expect(sourceVideo.currentTime).toBe(2);
    expect(targetVideo.currentTime).toBe(8);
    sourceVideo.currentTime = 3;
    targetVideo.currentTime = 8.5;
    fireEvent.timeUpdate(sourceVideo);
    expect(targetVideo.currentTime).toBe(9);
    const clipComparison = screen.getByRole("button", { name: "Play clip Mapped comparison" });
    fireEvent.keyDown(clipComparison, { key: "Enter" });
    expect(clipComparison).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByRole("button", { name: "Add anchor pair" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Preview affected content" })).not.toBeInTheDocument();
    await screen.findByText(/no counterpart found/);
    const apply = screen.getByRole("button", { name: "Approve alignment and set primary" });
    expect(apply).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: "Delete clip video" }));
    expect(apply).toBeEnabled();
    fireEvent.click(apply);
    await waitFor(() =>
      expect(api.apply).toHaveBeenCalledWith(
        42,
        expect.objectContaining({ resolution: "align", expectedPrimaryFileId: 10 }),
        expect.any(AbortSignal),
      ),
    );
    expect(screen.queryByText(/saved alignment/i)).not.toBeInTheDocument();
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining("permanently delete 1 clip video"));
  });

  it("does not apply when permanent clip deletion is not confirmed", async () => {
    vi.mocked(window.confirm).mockReturnValue(false);
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} />);
    await screen.findByText(/selected file does not match the current primary file/i);
    fireEvent.click(screen.getByRole("button", { name: "Use automated matching" }));
    await screen.findByRole("heading", { name: "Review alignment" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Delete clip video" }));
    fireEvent.click(screen.getByRole("button", { name: "Approve alignment and set primary" }));
    await waitFor(() => expect(window.confirm).toHaveBeenCalled());
    expect(api.apply).not.toHaveBeenCalled();
  });

  it("rejects an outdated preview response instead of approving without visual comparisons", async () => {
    api.preview.mockResolvedValue({ message: "old response", dependencies: [] });
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} />);
    await screen.findByText(/selected file does not match the current primary file/i);
    fireEvent.click(screen.getByRole("button", { name: "Use automated matching" }));
    await screen.findByRole("alert");
    expect(screen.getByRole("alert")).toHaveTextContent(/server finishes restarting/);
    expect(screen.queryByRole("button", { name: "Approve alignment and set primary" })).not.toBeInTheDocument();
  });

  it("switches directly when fingerprints and duration match", async () => {
    api.assess.mockResolvedValue({ ...assessment, equivalent: true });
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} />);
    const setPrimary = await screen.findByRole("button", { name: "Set as primary" });
    fireEvent.click(setPrimary);
    await waitFor(() =>
      expect(api.apply).toHaveBeenCalledWith(
        42,
        expect.objectContaining({ resolution: "direct" }),
        expect.any(AbortSignal),
      ),
    );
    expect(api.analyze).not.toHaveBeenCalled();
  });

  it("explains retained timestamps without inventing a missing primary duration", async () => {
    api.assess.mockResolvedValue({ ...assessment, sourceFileId: null, sourceDuration: null, canAlign: false });
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} />);

    await screen.findByText(/no current primary file.*saved timestamps/i);
    expect(screen.getByText(/selected file is 0:36.00 long.*no current primary duration/i)).toBeInTheDocument();
    expect(screen.queryByText(/duration changes from 0:00/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Use automated matching" })).not.toBeInTheDocument();
    expect(screen.getByText(/cannot find matching anchor points/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove all timed content" })).toBeInTheDocument();
  });

  it("describes direct assignment when no primary or timed content exists", async () => {
    api.assess.mockResolvedValue({
      ...assessment,
      sourceFileId: null,
      sourceDuration: null,
      canAlign: false,
      dependencyCount: 0,
      dependencyCounts: {},
    });
    render(<VideoAlignmentDialog videoId={42} targetFileId={11} onClose={vi.fn()} />);

    await screen.findByText(/no current primary file and no timed content to adjust/i);
    expect(screen.queryByText(/does not match the current primary/i)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Set as primary" })).toBeInTheDocument();
  });
});
