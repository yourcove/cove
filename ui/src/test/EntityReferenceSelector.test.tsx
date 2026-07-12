import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";

const mocks = vi.hoisted(() => ({ tagsFind: vi.fn() }));

vi.mock("../api/client", () => ({
  faces: {},
  galleries: {},
  groups: {},
  images: {},
  performers: {},
  studios: {},
  tags: { find: mocks.tagsFind },
  videos: {},
}));

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("EntityReferenceMultiSelector", () => {
  it("does not render a remove button for locked tag chips", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["entity-reference-selector", "tag", "selected", 1], { id: 1, label: "Manual" });
    queryClient.setQueryData(["entity-reference-selector", "tag", "selected", 2], { id: 2, label: "Derived" });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[1, 2]}
          lockedIds={[2]}
          onChange={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(await screen.findByText("Derived")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Manual" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Derived/i })).not.toBeInTheDocument();
  });

  it("renders selected chips from seedOptions without fetching each by id", async () => {
    // No per-id cache is primed and `tags.get` is not mocked, so any per-chip fetch would fail/stall.
    // Seeding with the labels the parent already has must resolve the chips synchronously instead.
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10, 11]}
          onChange={vi.fn()}
          seedOptions={[
            { id: 10, label: "Massage" },
            { id: 11, label: "Outdoor" },
          ]}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Massage")).toBeInTheDocument();
    expect(screen.getByText("Outdoor")).toBeInTheDocument();
    expect(screen.queryByText("Loading tag...")).not.toBeInTheDocument();
  });

  it("keeps the dropdown results mounted while the next search is loading", async () => {
    const user = userEvent.setup();
    let resolveNextSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    const nextSearch = new Promise<{ items: Array<{ id: number; name: string }> }>((resolve) => {
      resolveNextSearch = resolve;
    });
    mocks.tagsFind
      .mockResolvedValueOnce({ items: [{ id: 1, name: "Massage" }] })
      .mockReturnValueOnce(nextSearch);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    vi.spyOn(input, "getBoundingClientRect").mockReturnValue({
      x: 20, y: 100, left: 20, top: 100, right: 220, bottom: 140,
      width: 200, height: 40, toJSON: () => ({}),
    });
    vi.stubGlobal("scrollY", 480);
    vi.stubGlobal("visualViewport", { pageLeft: 0, pageTop: 500, height: 300, addEventListener: vi.fn(), removeEventListener: vi.fn() });
    await user.type(input, "m");
    const firstResult = await screen.findByRole("button", { name: /Massage/i });
    const dropdown = firstResult.parentElement;
    expect(dropdown).toHaveClass("absolute", "z-[200]", "overflow-y-auto", "overflow-x-hidden");
    expect(dropdown?.parentElement).toBe(document.body);
    expect(dropdown).toHaveStyle({ left: "20px", top: "624px", width: "200px" });

    await user.type(input, "a");
    await waitFor(() => expect(mocks.tagsFind).toHaveBeenCalledTimes(2));
    expect(screen.getByRole("button", { name: /Massage/i })).toBeInTheDocument();
    expect(screen.queryByText("Loading...")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create “ma”" })).not.toBeInTheDocument();

    resolveNextSearch({ items: [{ id: 2, name: "Makeup" }] });
    expect(await screen.findByRole("button", { name: /Makeup/i })).toBeInTheDocument();
  });
});
