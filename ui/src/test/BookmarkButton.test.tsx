import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BookmarkButton } from "../components/BookmarkButton";

const mocks = vi.hoisted(() => ({
  batch: vi.fn(),
  toggle: vi.fn(),
}));

vi.mock("../api/client", () => ({
  bookmarks: { batch: mocks.batch, toggle: mocks.toggle },
}));

describe("BookmarkButton", () => {
  beforeEach(() => {
    mocks.batch.mockReset();
    mocks.toggle.mockReset();
  });

  it("toggles the saved state", async () => {
    mocks.batch.mockResolvedValue([{ hostType: "video", hostId: 5, saved: false }]);
    mocks.toggle.mockResolvedValue({ hostType: "video", hostId: 5, saved: true });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <BookmarkButton hostType="video" hostId={5} />
      </QueryClientProvider>,
    );

    const button = await screen.findByRole("button", { name: "Save for Later" });
    await waitFor(() => expect(mocks.batch).toHaveBeenCalledTimes(1));
    fireEvent.click(button);

    expect(await screen.findByRole("button", { name: "Remove from Save for Later" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(mocks.toggle).toHaveBeenCalledWith({ hostType: "video", hostId: 5, saved: true });
  });
});
