import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MoveVideoFileDialog } from "../components/MoveVideoFileDialog";
import type { Video, VideoFile } from "../api/types";

const api = vi.hoisted(() => ({ findFiltered: vi.fn(), assignFile: vi.fn() }));
vi.mock("../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/client")>()),
  videos: api,
  entityImages: { videoCoverUrl: (id: number) => `/cover/${id}` },
}));
vi.mock("../hooks/useRegisterKeyboardActionHandler", () => ({ useRegisterKeyboardActionHandler: vi.fn() }));

const file = {
  id: 11,
  basename: "alternate.mp4",
  path: "/media/alternate.mp4",
  duration: 60,
  size: 1024,
  width: 640,
  height: 360,
} as VideoFile;
const candidates = [1, 2, 3].map(
  (id) => ({ id, title: `Video ${id}`, files: [], updatedAt: "2026-01-01" }) as unknown as Video,
);
function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  const onClose = vi.fn();
  const onMoved = vi.fn();
  render(
    <QueryClientProvider client={client}>
      <MoveVideoFileDialog file={file} sourceVideoId={1} onClose={onClose} onMoved={onMoved} />
    </QueryClientProvider>,
  );
  return { onClose, onMoved, invalidate };
}
beforeEach(() => {
  vi.clearAllMocks();
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute("open");
  };
  api.findFiltered.mockResolvedValue({ items: candidates, totalCount: 3 });
  api.assignFile.mockResolvedValue(undefined);
});

describe("moving a video file", () => {
  it("shows the file, excludes its owner, selects exactly one destination, and refreshes both videos", async () => {
    const { onClose, onMoved, invalidate } = setup();
    expect(screen.getByRole("dialog")).toHaveAttribute("aria-modal", "true");
    expect(screen.getByText("alternate.mp4")).toBeInTheDocument();
    expect(screen.getByText("/media/alternate.mp4")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Move file" })).toBeDisabled();
    expect(await screen.findByRole("radio", { name: /Video 1/ })).toBeDisabled();
    fireEvent.click(screen.getByRole("radio", { name: "Video 2" }));
    fireEvent.click(screen.getByRole("radio", { name: "Video 3" }));
    expect(screen.getByRole("radio", { name: "Video 2" })).not.toBeChecked();
    expect(screen.getByRole("radio", { name: "Video 3" })).toBeChecked();
    expect(api.assignFile).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Move file" }));
    await waitFor(() => expect(onClose).toHaveBeenCalledOnce());
    expect(api.assignFile).toHaveBeenCalledExactlyOnceWith(3, 11);
    expect(onMoved).toHaveBeenCalledOnce();
    for (const queryKey of [["video", 1], ["video", 3], ["videos"]])
      expect(invalidate).toHaveBeenCalledWith({ queryKey });
  });

  it("searches and pages using the shared controls while keeping the destination visible", async () => {
    api.findFiltered.mockResolvedValue({ items: candidates, totalCount: 40 });
    setup();
    fireEvent.click(await screen.findByRole("radio", { name: "Video 2" }));
    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    await waitFor(() =>
      expect(api.findFiltered).toHaveBeenLastCalledWith(
        expect.objectContaining({ findFilter: expect.objectContaining({ page: 2 }) }),
      ),
    );
    api.findFiltered.mockResolvedValue({ items: [], totalCount: 0 });
    fireEvent.change(screen.getByRole("textbox", { name: "Search list" }), { target: { value: "missing" } });
    await screen.findByText("No videos match your search.");
    expect(api.findFiltered).toHaveBeenLastCalledWith(
      expect.objectContaining({ findFilter: expect.objectContaining({ q: "missing", page: 1 }) }),
    );
    expect(screen.getByText("Destination: Video 2")).toBeInTheDocument();
  });

  it("prevents duplicate moves and keeps a failed move open for retry", async () => {
    let rejectMove!: (error: Error) => void;
    api.assignFile.mockImplementationOnce(
      () =>
        new Promise((_, reject) => {
          rejectMove = reject;
        }),
    );
    const { onClose } = setup();
    fireEvent.click(await screen.findByRole("radio", { name: "Video 2" }));
    fireEvent.click(screen.getByRole("button", { name: "Move file" }));
    expect(await screen.findByRole("button", { name: "Moving…" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    rejectMove(new Error("Access denied"));
    expect(await screen.findByRole("alert")).toHaveTextContent("Access denied");
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Move file" }));
    await waitFor(() => expect(onClose).toHaveBeenCalledOnce());
  });

  it("prevents choosing clips that use their parent video’s files", async () => {
    api.findFiltered.mockResolvedValue({
      items: [...candidates, { ...candidates[1], id: 4, title: "Clip", parentVideoId: 2 }],
      totalCount: 4,
    });
    setup();
    expect(await screen.findByRole("radio", { name: /Clip/ })).toBeDisabled();
  });

  it("reports search failures and retries without moving a file", async () => {
    api.findFiltered.mockRejectedValueOnce(new Error("Offline"));
    const { onClose } = setup();
    expect(await screen.findByRole("alert")).toHaveTextContent("Offline");
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await screen.findByRole("radio", { name: "Video 2" });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalledOnce();
    expect(api.assignFile).not.toHaveBeenCalled();
  });
});
