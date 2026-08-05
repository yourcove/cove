import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GlobalSearch } from "../components/GlobalSearch";

const { searchMock } = vi.hoisted(() => ({ searchMock: vi.fn() }));

vi.mock("../api/client", () => ({
  globalSearch: { find: searchMock },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    permissions: ["*"],
    hasPermission: () => true,
  }),
}));

vi.mock("../utils/interactionTracking", () => ({ trackInteraction: vi.fn() }));

function renderSearch(navigate = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <GlobalSearch navigate={navigate} />
    </QueryClientProvider>,
  );
  return { navigate, queryClient };
}

describe("GlobalSearch", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    searchMock.mockResolvedValue({
      groups: [{ type: "video", items: [{ id: 42, title: "Matching video", subtitle: "Example studio" }] }],
      failedTypes: [],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it("debounces rapid typing into one consolidated request", async () => {
    renderSearch();
    const input = screen.getByRole("textbox", { name: "Search all..." });

    fireEvent.change(input, { target: { value: "quick" } });
    fireEvent.change(input, { target: { value: "quick search" } });

    expect(searchMock).not.toHaveBeenCalled();
    await act(async () => vi.advanceTimersByTime(99));
    expect(searchMock).not.toHaveBeenCalled();
    await act(async () => {
      vi.advanceTimersByTime(1);
      await Promise.resolve();
    });
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });

    expect(searchMock).toHaveBeenCalledTimes(1);
    expect(searchMock).toHaveBeenCalledWith("quick search", 8, expect.any(AbortSignal));
    expect(screen.getAllByText("Matching video")).toHaveLength(2);
  });

  it("does not search terms shorter than two trimmed characters", async () => {
    renderSearch();

    fireEvent.change(screen.getByRole("textbox", { name: "Search all..." }), { target: { value: "a " } });
    await act(async () => vi.advanceTimersByTime(500));

    expect(searchMock).not.toHaveBeenCalled();
  });

  it("cancels an in-flight request when a newer term is committed", async () => {
    searchMock.mockImplementationOnce(() => new Promise(() => {}));
    renderSearch();
    const input = screen.getByRole("textbox", { name: "Search all..." });

    fireEvent.change(input, { target: { value: "first term" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });
    const firstSignal = searchMock.mock.calls[0][2] as AbortSignal;
    expect(firstSignal.aborted).toBe(false);

    fireEvent.change(input, { target: { value: "newer term" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });

    expect(firstSignal.aborted).toBe(true);
    expect(searchMock).toHaveBeenCalledTimes(2);
    expect(searchMock.mock.calls[1][0]).toBe("newer term");
  });
});
