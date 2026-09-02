import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GlobalSearch } from "../components/GlobalSearch";
import { reportServerResponse, resetServerAvailabilityForTests } from "../state/serverAvailability";

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

afterEach(() => resetServerAvailabilityForTests());

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
    const input = screen.getByRole("combobox", { name: "Search all..." });

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

  it("navigates search results with the arrow keys and opens the active result", async () => {
    searchMock.mockResolvedValue({
      groups: [{
        type: "video",
        items: [
          { id: 42, title: "First match", subtitle: "Example studio" },
          { id: 84, title: "Second match", subtitle: "Another studio" },
        ],
      }],
      failedTypes: [],
    });
    const { navigate } = renderSearch();
    const input = screen.getByRole("combobox", { name: "Search all..." });
    input.focus();

    fireEvent.change(input, { target: { value: "test" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });

    fireEvent.keyDown(input, { key: "ArrowDown" });
    const firstMatch = screen.getAllByRole("option", { name: /First match/ })[0];
    expect(firstMatch).toHaveAttribute("aria-selected", "true");
    expect(firstMatch).toHaveClass("bg-accent/15", "ring-accent");
    expect(input).toHaveAttribute("aria-activedescendant");
    expect(document.getElementById(input.getAttribute("aria-activedescendant")!)).toHaveAttribute("aria-selected", "true");

    fireEvent.keyDown(input, { key: "ArrowDown" });
    expect(screen.getAllByRole("option", { name: /Second match/ })[0]).toHaveAttribute("aria-selected", "true");

    fireEvent.keyDown(input, { key: "ArrowUp" });
    expect(screen.getAllByRole("option", { name: /First match/ })[0]).toHaveAttribute("aria-selected", "true");

    fireEvent.keyDown(input, { key: "Enter" });
    expect(navigate).toHaveBeenCalledWith({ page: "video", id: 42 });
    expect(input).not.toHaveFocus();
  });

  it("clears, closes, and blurs global search on Escape", () => {
    renderSearch();
    const input = screen.getByRole("combobox", { name: "Search all..." });
    input.focus();
    fireEvent.change(input, { target: { value: "test" } });

    fireEvent.keyDown(input, { key: "Escape" });

    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("aria-expanded", "false");
    expect(input).not.toHaveFocus();
  });

  it("closes global search when tabbing away", () => {
    renderSearch();
    const input = screen.getByRole("combobox", { name: "Search all..." });
    fireEvent.change(input, { target: { value: "test" } });

    fireEvent.keyDown(input, { key: "Tab" });

    expect(input).toHaveValue("test");
    expect(input).toHaveAttribute("aria-expanded", "false");
    expect(input).not.toHaveAttribute("aria-activedescendant");
  });

  it("does not search terms shorter than two trimmed characters", async () => {
    renderSearch();

    fireEvent.change(screen.getByRole("combobox", { name: "Search all..." }), { target: { value: "a " } });
    await act(async () => vi.advanceTimersByTime(500));

    expect(searchMock).not.toHaveBeenCalled();
  });

  it("cancels an in-flight request when a newer term is committed", async () => {
    searchMock.mockImplementationOnce(() => new Promise(() => {}));
    renderSearch();
    const input = screen.getByRole("combobox", { name: "Search all..." });

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

  it("reports a search failure instead of claiming there are no results", async () => {
    searchMock.mockRejectedValue(new Error("Search API unavailable"));
    renderSearch();

    fireEvent.change(screen.getByRole("combobox", { name: "Search all..." }), { target: { value: "test" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });

    expect(screen.getAllByRole("alert")).not.toHaveLength(0);
    expect(screen.getAllByText("Search could not be completed.")).not.toHaveLength(0);
    expect(screen.queryByText(/No results found/)).not.toBeInTheDocument();

    const callsBeforeRetry = searchMock.mock.calls.length;
    fireEvent.click(screen.getAllByRole("button", { name: "Try again" })[0]);
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });
    expect(searchMock).toHaveBeenCalledTimes(callsBeforeRetry + 1);
  });

  it("explains a confirmed outage when every search scope fails", async () => {
    reportServerResponse(new Response(null, { status: 502 }));
    searchMock.mockRejectedValue(new Error("Search API unavailable"));
    renderSearch();

    fireEvent.change(screen.getByRole("combobox", { name: "Search all..." }), { target: { value: "test" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });

    expect(screen.getAllByText("Cove can’t reach the server right now.")).not.toHaveLength(0);
    expect(screen.queryByText("Cove could not load any searchable library.")).not.toBeInTheDocument();
  });

  it("distinguishes partial failures from an empty successful search", async () => {
    searchMock.mockResolvedValue({ groups: [], failedTypes: ["video"] });
    renderSearch();

    fireEvent.change(screen.getByRole("combobox", { name: "Search all..." }), { target: { value: "test" } });
    await act(async () => {
      vi.advanceTimersByTime(100);
      await Promise.resolve();
    });
    await act(async () => {
      vi.advanceTimersByTime(0);
      await Promise.resolve();
    });

    expect(screen.getAllByText("Search failed for Videos.")).not.toHaveLength(0);
    expect(screen.getAllByText(/No results found in the searches that completed/)).not.toHaveLength(0);
    expect(screen.queryByText("Search could not be completed.")).not.toBeInTheDocument();
  });
});
