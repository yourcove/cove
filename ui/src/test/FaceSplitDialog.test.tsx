import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { FaceHostTrack } from "../api/types";
import { FaceSplitDialog } from "../components/FaceSplitDialog";

const api = vi.hoisted(() => ({
  hostTracks: vi.fn(),
  split: vi.fn(),
}));

vi.mock("../api/client", () => ({
  faces: {
    hostTracks: api.hostTracks,
    split: api.split,
    detectionCropUrl: () => undefined,
  },
}));

function track(groupKey: string, suggestedGroup: number): FaceHostTrack {
  return { groupKey, sampleCount: 1, detectionCount: 1, suggestedGroup };
}

function renderWithClient(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
  return {
    rerender: (next: ReactNode) => result.rerender(<QueryClientProvider client={client}>{next}</QueryClientProvider>),
  };
}

function dialog(props: { open: boolean; faceId: number | null }) {
  return (
    <FaceSplitDialog
      open={props.open}
      faceId={props.faceId}
      faceTitle="Face"
      hostType="video"
      hostId={7}
      onClose={() => {}}
    />
  );
}

const pressedKeys = () =>
  [...document.querySelectorAll<HTMLButtonElement>("button[aria-pressed]")].map(
    (button) => button.getAttribute("aria-pressed") === "true",
  );

describe("FaceSplitDialog", () => {
  beforeEach(() => {
    api.hostTracks.mockImplementation(async (faceId: number) =>
      faceId === 1 ? [track("a", 0), track("b", 0), track("c", 1)] : [track("x", 0), track("y", 1), track("z", 1)],
    );
    api.split.mockResolvedValue({ targetFaceId: 9 });
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("preselects the provider's proposal and splits what the user then selects", async () => {
    renderWithClient(dialog({ open: true, faceId: 1 }));

    await waitFor(() => expect(pressedKeys()).toEqual([false, false, true]));

    fireEvent.click(document.querySelectorAll("button[aria-pressed]")[0]);
    expect(pressedKeys()).toEqual([true, false, true]);

    fireEvent.click(screen.getByRole("button", { name: /Separate 2 appearances/ }));
    await waitFor(() =>
      expect(api.split).toHaveBeenCalledWith(1, { hostType: "video", hostId: 7, groupKeys: ["c", "a"] }),
    );
  });

  it("drops a manual selection when the caller closes and reopens it, or switches faces", async () => {
    const { rerender } = renderWithClient(dialog({ open: true, faceId: 1 }));
    await waitFor(() => expect(pressedKeys()).toEqual([false, false, true]));

    fireEvent.click(document.querySelectorAll("button[aria-pressed]")[2]);
    expect(pressedKeys()).toEqual([false, false, false]);

    // The detail pages clear the face when they close the dialog.
    rerender(dialog({ open: false, faceId: null }));
    rerender(dialog({ open: true, faceId: 1 }));
    await waitFor(() => expect(pressedKeys()).toEqual([false, false, true]));

    fireEvent.click(document.querySelectorAll("button[aria-pressed]")[0]);
    expect(pressedKeys()).toEqual([true, false, true]);

    rerender(dialog({ open: true, faceId: 2 }));
    await waitFor(() => expect(pressedKeys()).toEqual([false, true, true]));
  });

  it("restores the proposal when reopened on the same face it kept while closed", async () => {
    const { rerender } = renderWithClient(dialog({ open: true, faceId: 1 }));
    await waitFor(() => expect(pressedKeys()).toEqual([false, false, true]));

    rerender(dialog({ open: false, faceId: 1 }));
    rerender(dialog({ open: true, faceId: 1 }));
    await waitFor(() => expect(pressedKeys()).toEqual([false, false, true]));
  });
});
