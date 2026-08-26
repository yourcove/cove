import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ImageSelectionActions } from "../components/ImageSelectionActions";

const bulkDelete = vi.fn();

vi.mock("../api/client", () => ({
  images: {
    bulkDelete: (...args: unknown[]) => bulkDelete(...args),
    bulkUpdate: vi.fn(),
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock("../components/ExtensionSelectionActions", () => ({
  ExtensionSelectionActions: () => null,
}));

describe("ImageSelectionActions deletion jobs", () => {
  beforeEach(() => bulkDelete.mockReset());

  it("closes the confirmation and clears selection after the deletion job is queued", async () => {
    bulkDelete.mockResolvedValue({ jobId: "image-delete-job", itemCount: 2 });
    const onSelectNone = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const user = userEvent.setup();

    render(
      <QueryClientProvider client={queryClient}>
        <ImageSelectionActions selectedIds={new Set([3, 7])} onSelectNone={onSelectNone} />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Delete" }));
    await user.click(screen.getByRole("dialog").querySelector("button.bg-red-600")!);

    await waitFor(() => expect(onSelectNone).toHaveBeenCalledOnce());
    expect(bulkDelete).toHaveBeenCalledWith([3, 7], { deleteFile: false, deleteGenerated: false });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(invalidate).not.toHaveBeenCalled();
  });

  it("waits for the job completion signal before refreshing the owning list", async () => {
    bulkDelete.mockResolvedValue({ jobId: "image-delete-job", itemCount: 2 });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const user = userEvent.setup();

    render(
      <QueryClientProvider client={queryClient}>
        <ImageSelectionActions selectedIds={new Set([3])} onSelectNone={vi.fn()} queryKey="tag-images" />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Delete" }));
    await user.click(screen.getByRole("dialog").querySelector("button.bg-red-600")!);

    await waitFor(() => expect(bulkDelete).toHaveBeenCalledOnce());
    expect(invalidate).not.toHaveBeenCalled();
  });
});
