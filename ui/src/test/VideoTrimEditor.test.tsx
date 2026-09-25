import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { VideoTrimEditor } from "../components/VideoTrimEditor";
import type { VideoCutPreview } from "../api/client";

const mocks = vi.hoisted(() => ({
  encoders: vi.fn(),
  preview: vi.fn(),
  start: vi.fn(),
}));

vi.mock("../api/client", () => ({
  videoConversion: { encoders: mocks.encoders, start: vi.fn() },
  videoCuts: { preview: mocks.preview, start: mocks.start },
}));

function renderEditor(props: Partial<Parameters<typeof VideoTrimEditor>[0]> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onSeek = vi.fn();
  const view = render(
    <QueryClientProvider client={queryClient}>
      <VideoTrimEditor
        videoId={5}
        fileId={11}
        duration={100}
        currentTime={30}
        canReplaceOriginal
        onSeek={onSeek}
        onClose={vi.fn()}
        {...props}
      />
    </QueryClientProvider>,
  );
  return { ...view, onSeek };
}

function stubStorage() {
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, value),
    removeItem: (key: string) => void values.delete(key),
    clear: () => values.clear(),
  };
  vi.stubGlobal("localStorage", storage);
  Object.defineProperty(window, "localStorage", { configurable: true, value: storage });
}

const PREVIEW: VideoCutPreview = {
  fileId: 11,
  sourceDuration: 100,
  exact: false,
  kept: [{ start: 18, end: 100 }],
  outputDuration: 82,
  removedSeconds: 18,
  sourceBytes: 1000,
  estimatedBytes: 820,
  items: [
    { kind: "segment", id: 1, title: "Intro", start: 5, end: 15, outcome: "removed", newStart: null, newEnd: null },
    { kind: "segment", id: 2, title: "Scene", start: 40, end: 60, outcome: "moved", newStart: 22, newEnd: 42 },
  ],
};

describe("VideoTrimEditor", () => {
  beforeEach(() => {
    stubStorage();
    mocks.encoders.mockReset();
    mocks.preview.mockReset();
    mocks.start.mockReset();
    mocks.encoders.mockResolvedValue([{ codec: "hevc", encoder: "hevc_nvenc", hardware: true }]);
    mocks.preview.mockResolvedValue(PREVIEW);
  });

  it("opens with removals from a link and previews what the cut would do", async () => {
    renderEditor({ initialRemove: [{ start: 0, end: 20 }] });

    expect(screen.getByLabelText("Removal 1 start")).toHaveProperty("value", "0:00.0");
    expect(screen.getByLabelText("Removal 1 end")).toHaveProperty("value", "0:20.0");
    await waitFor(() =>
      expect(mocks.preview).toHaveBeenCalledWith(5, [{ start: 0, end: 20 }], false, expect.anything()),
    );
    expect(await screen.findByText(/would be deleted: Intro \(0:05.0\)/)).toBeTruthy();
    expect(screen.getByText(/1 item would move earlier/)).toBeTruthy();
    // The keyframe before 20 s is at 18 s, so a fast cut keeps 2 s the user marked.
    expect(screen.getByText(/keeps 0:02.0 more than marked/)).toBeTruthy();
  });

  it("marks from the playhead and starts a lossless cut that replaces the original", async () => {
    mocks.start.mockResolvedValue({ jobId: "job-1", itemCount: 1 });
    const user = userEvent.setup();
    renderEditor();

    expect(screen.getByRole("button", { name: "Cut" })).toHaveProperty("disabled", true);
    await user.click(screen.getByRole("button", { name: "Remove start → here" }));
    await user.click(screen.getByRole("checkbox", { name: /Replace the original/ }));
    await user.click(screen.getByRole("button", { name: "Cut and replace" }));

    await waitFor(() =>
      expect(mocks.start).toHaveBeenCalledWith({
        videos: [{ videoId: 5, fileId: 11, remove: [{ start: 0, end: 30 }] }],
        codec: "copy",
        container: "source",
        effort: "highHardware",
        outputFrameRate: null,
        replaceOriginal: true,
      }),
    );
    expect(await screen.findByText(/Cut queued/)).toBeTruthy();
  });

  it("re-encodes with the chosen codec for an exact cut, and accepts typed times", async () => {
    mocks.start.mockResolvedValue({ jobId: "job-2", itemCount: 1 });
    const user = userEvent.setup();
    renderEditor({ initialRemove: [{ start: 40, end: 50 }] });

    const end = screen.getByLabelText("Removal 1 end");
    await user.clear(end);
    await user.type(end, "1:05{Enter}");
    await user.click(screen.getByRole("radio", { name: /Exact/ }));
    expect(await screen.findByText(/GPU encoding with hevc_nvenc/)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Cut" }));

    await waitFor(() =>
      expect(mocks.start).toHaveBeenCalledWith(
        expect.objectContaining({
          videos: [{ videoId: 5, fileId: 11, remove: [{ start: 40, end: 65 }] }],
          codec: "hevc",
          replaceOriginal: false,
        }),
      ),
    );
  });

  it("offers only frame rates below the source's for an exact cut and sends the one chosen", async () => {
    mocks.start.mockResolvedValue({ jobId: "job-3", itemCount: 1 });
    const user = userEvent.setup();
    renderEditor({ initialRemove: [{ start: 40, end: 50 }], sourceFrameRate: 59.94 });

    expect(screen.queryByLabelText("Frame rate")).toBeNull();
    await user.click(screen.getByRole("radio", { name: /Exact/ }));
    const frameRate = screen.getByLabelText("Frame rate");
    expect([...frameRate.querySelectorAll("option")].map((option) => option.textContent)).toEqual([
      "Keep source frame rate",
      "30 fps",
      "24 fps",
    ]);
    await user.selectOptions(frameRate, "30");
    await user.click(screen.getByRole("button", { name: "Cut" }));

    await waitFor(() =>
      expect(mocks.start).toHaveBeenCalledWith(expect.objectContaining({ codec: "hevc", outputFrameRate: 30 })),
    );
  });

  it("refuses to cut the whole video", async () => {
    renderEditor({ initialRemove: [{ start: 0, end: 100 }] });

    expect(screen.getByRole("alert").textContent).toMatch(/removes the whole video/);
    expect(screen.getByRole("button", { name: "Cut" })).toHaveProperty("disabled", true);
    expect(mocks.preview).not.toHaveBeenCalled();
  });
});
