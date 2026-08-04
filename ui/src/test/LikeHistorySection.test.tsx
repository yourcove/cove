import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import { LikeHistorySection } from "../components/LikeHistorySection";

function renderSection(canAddHistoricalLike: boolean, onAddHistoricalLike = vi.fn().mockResolvedValue(undefined)) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <LikeHistorySection
        likeHistory={["2024-01-02T03:04:05.000Z"]}
        canAddHistoricalLike={canAddHistoricalLike}
        onAddHistoricalLike={onAddHistoricalLike}
      />
    </QueryClientProvider>,
  );
  return onAddHistoricalLike;
}

describe("LikeHistorySection", () => {
  it("shows dated history without exposing write actions to read-only users", () => {
    renderSection(false);
    expect(screen.getByText(/2024-01-02/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Like history actions" })).not.toBeInTheDocument();
  });

  it("submits a selected historical date as an ISO timestamp", async () => {
    const onAddHistoricalLike = renderSection(true);
    fireEvent.click(screen.getByRole("button", { name: "Like history actions" }));
    fireEvent.click(screen.getByRole("button", { name: "Add historical like" }));
    fireEvent.change(screen.getByLabelText("Date and time"), { target: { value: "2024-07-03T12:00" } });
    fireEvent.click(screen.getByRole("button", { name: "Add like" }));

    await waitFor(() => expect(onAddHistoricalLike).toHaveBeenCalledWith(new Date("2024-07-03T12:00").toISOString()));
  });
});
