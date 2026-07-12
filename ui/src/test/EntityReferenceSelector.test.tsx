import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { EntityReferenceMultiSelector, EntityReferenceSelector } from "../components/EntityReferenceSelector";

const mocks = vi.hoisted(() => ({ tagsCreate: vi.fn(), tagsFind: vi.fn() }));

vi.mock("../api/client", () => ({
  faces: {},
  galleries: {},
  groups: {},
  images: {},
  performers: {},
  studios: {},
  tags: { create: mocks.tagsCreate, find: mocks.tagsFind },
  videos: {},
}));

afterEach(() => {
  vi.restoreAllMocks();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
});

describe("EntityReferenceMultiSelector", () => {
  it("exposes portal-rendered combobox options and selects the active option with clamped keyboard navigation", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({
      items: [
        { id: 1, name: "Massage" },
        { id: 2, name: "Makeup" },
      ],
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "Makeup");
    const firstOption = await screen.findByRole("option", { name: /Makeup/i });
    const secondOption = screen.getByRole("option", { name: /Massage/i });
    const listbox = screen.getByRole("listbox");

    expect(input).toHaveAttribute("role", "combobox");
    expect(input).toHaveAttribute("aria-autocomplete", "list");
    expect(input).toHaveAttribute("aria-expanded", "true");
    expect(input).toHaveAttribute("aria-controls", listbox.id);
    expect(listbox.parentElement).toBe(document.body);

    const scrollIntoView = vi.fn();
    firstOption.scrollIntoView = scrollIntoView;
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", firstOption.id);
    expect(firstOption).toHaveAttribute("aria-selected", "true");
    expect(document.activeElement).toBe(input);
    expect(scrollIntoView).toHaveBeenCalledWith({ block: "nearest" });

    await user.keyboard("{ArrowDown}{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", secondOption.id);

    await user.keyboard("{Enter}");
    expect(onChange).toHaveBeenCalledWith([1]);
    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("aria-expanded", "false");
  });

  it("closes on Escape without clearing the query or selection", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Massage" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "mass");
    await screen.findByRole("option", { name: /Massage/i });
    await user.keyboard("{ArrowDown}{Escape}");

    expect(input).toHaveValue("mass");
    expect(input).toHaveAttribute("aria-expanded", "false");
    expect(input).not.toHaveAttribute("aria-controls");
    expect(input).not.toHaveAttribute("aria-activedescendant");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(document.activeElement).toBe(input);
  });

  it("closes on Tab and an outside pointer interaction while preserving native editable keys", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Massage" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "mass");
    await screen.findByRole("option", { name: /Massage/i });

    expect(fireEvent.keyDown(input, { key: "Home" })).toBe(true);
    expect(fireEvent.keyDown(input, { key: "End" })).toBe(true);
    await user.keyboard("{Tab}");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(input).toHaveValue("mass");

    await user.click(input);
    expect(await screen.findByRole("listbox")).toBeInTheDocument();
    await user.click(document.body);
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(input).toHaveValue("mass");
  });

  it("activates the create option from the keyboard", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.tagsCreate.mockResolvedValue({ id: 12, name: "Novel tag" });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "Novel tag");
    const createOption = await screen.findByRole("option", { name: "Create “Novel tag”" });
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", createOption.id);
    await user.keyboard("{Enter}");

    await waitFor(() => expect(mocks.tagsCreate).toHaveBeenCalledWith({ name: "Novel tag" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledWith([12]));
  });

  it("can suppress creation for selector contexts that only accept existing entities", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} creatable={false} />
      </QueryClientProvider>,
    );

    await user.type(screen.getByPlaceholderText("Search tags..."), "Missing");
    expect(await screen.findByText("No tags found")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/i })).not.toBeInTheDocument();
  });

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
    const onChange = vi.fn();
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
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />
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
    const firstResult = await screen.findByRole("option", { name: /Massage/i });
    const dropdown = firstResult.parentElement;
    expect(dropdown).toHaveClass("absolute", "z-[200]", "overflow-y-auto", "overflow-x-hidden");
    expect(dropdown?.parentElement).toBe(document.body);
    expect(dropdown).toHaveStyle({ left: "20px", top: "624px", width: "200px" });

    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", firstResult.id);
    await user.type(input, "a");
    await waitFor(() => expect(mocks.tagsFind).toHaveBeenCalledTimes(2));
    expect(screen.getByRole("option", { name: /Massage/i })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: /Massage/i })).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("listbox")).toHaveAttribute("aria-busy", "true");
    expect(input).not.toHaveAttribute("aria-activedescendant");
    await user.keyboard("{ArrowDown}{Enter}");
    await user.click(screen.getByRole("option", { name: /Massage/i }));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.queryByText("Loading...")).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Create “ma”" })).not.toBeInTheDocument();

    resolveNextSearch({ items: [{ id: 2, name: "Makeup" }] });
    expect(await screen.findByRole("option", { name: /Makeup/i })).toBeInTheDocument();
    expect(input).not.toHaveAttribute("aria-activedescendant");
  });
});

describe("EntityReferenceSelector", () => {
  it("selects an active option from the keyboard and returns its metadata", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 7, name: "Massage" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="tag" onChange={onChange} creatable={false} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "mass");
    const option = await screen.findByRole("option", { name: /Massage/i });
    await user.keyboard("{ArrowDown}{Enter}");

    expect(onChange).toHaveBeenCalledWith(7, expect.objectContaining({ id: 7, label: "Massage" }));
    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("aria-expanded", "false");
    expect(option).not.toBeInTheDocument();
  });
});
