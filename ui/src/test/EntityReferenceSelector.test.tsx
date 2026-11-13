import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { EntityReferenceMultiSelector, EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { toTagOption } from "../utils/tags";
import { toPerformerOption } from "../utils/performers";
import { MutationFailureNotice } from "../components/MutationFailureNotice";
import { createAppQueryClient } from "../queryClient";
import { resetMutationFailureForTests } from "../state/mutationFailure";

const mocks = vi.hoisted(() => ({
  performersFind: vi.fn(),
  performersGet: vi.fn(),
  tagsCreate: vi.fn(),
  tagsFind: vi.fn(),
}));

vi.mock("../api/client", () => ({
  faces: {},
  galleries: {},
  groups: {},
  images: {},
  performers: { find: mocks.performersFind, get: mocks.performersGet },
  studios: {},
  tags: { create: mocks.tagsCreate, find: mocks.tagsFind },
  videos: {},
}));

beforeEach(() => {
  mocks.performersFind.mockReset();
  mocks.performersGet.mockReset();
  mocks.tagsCreate.mockReset();
  mocks.tagsFind.mockReset();
});

afterEach(() => {
  Object.defineProperty(document, "fullscreenElement", { configurable: true, value: null });
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
    expect(firstOption.querySelector(".lucide-plus")).toBeInTheDocument();

    const scrollIntoView = vi.fn();
    firstOption.scrollIntoView = scrollIntoView;
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", firstOption.id);
    expect(firstOption).toHaveAttribute("aria-selected", "true");
    expect(firstOption).toHaveClass("bg-accent", "text-white");
    expect(document.activeElement).toBe(input);
    expect(scrollIntoView).toHaveBeenCalledWith({ block: "nearest" });

    await user.keyboard("{ArrowDown}{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", secondOption.id);

    await user.keyboard("{Enter}");
    expect(onChange).toHaveBeenCalledWith([1]);
    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("aria-expanded", "false");
  });

  it("clears the query and closes on Escape without changing the selection", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Massage" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[99]} onChange={onChange} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "mass");
    await screen.findByRole("option", { name: /Massage/i });
    await user.keyboard("{ArrowDown}{Escape}");

    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("aria-expanded", "false");
    expect(input).not.toHaveAttribute("aria-controls");
    expect(input).not.toHaveAttribute("aria-activedescendant");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(document.activeElement).toBe(input);
    expect(onChange).not.toHaveBeenCalled();
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
        <EntityReferenceMultiSelector entityType="tag" values={[1, 2]} lockedIds={[2]} onChange={vi.fn()} />
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

  it("colors selected tag chips like their Details-tab badges", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10, 11, 12]}
          onChange={vi.fn()}
          seedOptions={[
            toTagOption({
              id: 10,
              name: "Grouped",
              color: null,
              tagGroupColor: "#336699",
              tagGroupName: "Example group",
            }),
            toTagOption({ id: 11, name: "Own color", color: "#cc0000", tagGroupColor: "#336699", tagGroupName: null }),
            toTagOption({ id: 12, name: "Plain", color: null, tagGroupColor: null, tagGroupName: null }),
          ]}
        />
      </QueryClientProvider>,
    );

    const chip = (name: string) => screen.getByText(name).parentElement!;
    expect(chip("Grouped")).toHaveStyle({ borderColor: "rgba(51, 102, 153, 0.58)" });
    expect(screen.getByTitle("Tag group: Example group")).toBeInTheDocument();
    expect(chip("Own color")).toHaveStyle({ borderColor: "rgba(204, 0, 0, 0.58)" });
    expect(chip("Plain").getAttribute("style")).toBeNull();
    expect(chip("Plain").querySelector('[title^="Tag group"]')).toBeNull();
  });

  it("names an uncolored group on its tag chips", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10]}
          onChange={vi.fn()}
          seedOptions={[
            toTagOption({ id: 10, name: "Grouped", color: null, tagGroupColor: null, tagGroupName: "Plain group" }),
          ]}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Grouped").parentElement!.getAttribute("style")).toBeNull();
    expect(screen.getByTitle("Tag group: Plain group")).toBeInTheDocument();
  });

  it("colors tag chips from the parent's payload rather than an older cached list", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(
      ["tags", "all"],
      [{ id: 10, name: "Grouped", color: null, tagGroupColor: "#00ff00", tagGroupName: "Old group" }],
    );

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10]}
          onChange={vi.fn()}
          seedOptions={[
            toTagOption({ id: 10, name: "Grouped", color: null, tagGroupColor: "#336699", tagGroupName: "New group" }),
          ]}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Grouped").parentElement).toHaveStyle({ borderColor: "rgba(51, 102, 153, 0.58)" });
    expect(screen.getByTitle("Tag group: New group")).toBeInTheDocument();
  });

  it("keeps the dropdown results mounted while the next search is loading", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    let resolveNextSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    const nextSearch = new Promise<{ items: Array<{ id: number; name: string }> }>((resolve) => {
      resolveNextSearch = resolve;
    });
    mocks.tagsFind.mockResolvedValueOnce({ items: [{ id: 1, name: "Massage" }] }).mockReturnValueOnce(nextSearch);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    vi.spyOn(input, "getBoundingClientRect").mockReturnValue({
      x: 20,
      y: 100,
      left: 20,
      top: 100,
      right: 220,
      bottom: 140,
      width: 200,
      height: 40,
      toJSON: () => ({}),
    });
    vi.stubGlobal("scrollY", 480);
    vi.stubGlobal("visualViewport", {
      pageLeft: 0,
      pageTop: 500,
      height: 300,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    });
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
    expect(input).toHaveAttribute("aria-activedescendant", firstResult.id);
    await user.click(screen.getByRole("option", { name: /Massage/i }));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.queryByText("Loading...")).not.toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Create “ma”" })).toBeInTheDocument();

    resolveNextSearch({ items: [{ id: 2, name: "Makeup" }] });
    expect(await screen.findByRole("option", { name: /Makeup/i })).toBeInTheDocument();
    expect(input).not.toHaveAttribute("aria-activedescendant");
  });

  it("keeps the highlighted create option mounted and current while the next search is loading", async () => {
    const user = userEvent.setup();
    let resolveNextSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    const nextSearch = new Promise<{ items: Array<{ id: number; name: string }> }>((resolve) => {
      resolveNextSearch = resolve;
    });
    mocks.tagsFind.mockResolvedValueOnce({ items: [] }).mockReturnValueOnce(nextSearch);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    fireEvent.change(input, { target: { value: "Novel" } });
    const createOption = await screen.findByRole("option", { name: "Create “Novel”" });
    act(() => input.focus());
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", createOption.id);

    fireEvent.change(input, { target: { value: "Novel tag" } });
    await waitFor(() => expect(mocks.tagsFind).toHaveBeenCalledTimes(2));
    const updatedCreateOption = screen.getByRole("option", { name: "Create “Novel tag”" });
    expect(updatedCreateOption).toBe(createOption);
    expect(input).toHaveAttribute("aria-activedescendant", updatedCreateOption.id);

    resolveNextSearch({ items: [] });
    await waitFor(() => expect(screen.getByRole("listbox")).not.toHaveAttribute("aria-busy"));
  });

  it("waits for the initial search before offering creation for an exact match", async () => {
    let resolveSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    mocks.tagsFind.mockReturnValue(
      new Promise((resolve) => {
        resolveSearch = resolve;
      }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    fireEvent.change(input, { target: { value: "Existing" } });
    expect(await screen.findByText("Loading...")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Create “Existing”" })).not.toBeInTheDocument();

    resolveSearch({ items: [{ id: 1, name: "Existing" }] });
    expect(await screen.findByRole("option", { name: "Existing" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Create “Existing”" })).not.toBeInTheDocument();
  });

  it("clears a highlighted create option when placeholder results prove an exact match", async () => {
    const user = userEvent.setup();
    let resolveNextSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    mocks.tagsFind.mockResolvedValueOnce({ items: [{ id: 1, name: "Existing" }] }).mockReturnValueOnce(
      new Promise((resolve) => {
        resolveNextSearch = resolve;
      }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    const input = screen.getByPlaceholderText("Search tags...");
    fireEvent.change(input, { target: { value: "Exist" } });
    const createOption = await screen.findByRole("option", { name: "Create “Exist”" });
    act(() => input.focus());
    await user.keyboard("{ArrowDown}{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", createOption.id);

    fireEvent.change(input, { target: { value: "Existing" } });
    await waitFor(() => expect(mocks.tagsFind).toHaveBeenCalledTimes(2));
    expect(screen.getByRole("listbox")).toHaveAttribute("aria-busy", "true");
    expect(screen.queryByRole("option", { name: "Create “Existing”" })).not.toBeInTheDocument();
    await waitFor(() => expect(input).not.toHaveAttribute("aria-activedescendant"));

    resolveNextSearch({ items: [{ id: 1, name: "Existing" }] });
  });
});

describe("EntityReferenceSelector", () => {
  it("associates an external label with the combobox input", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <label htmlFor="starting-performer">Starting performer</label>
        <EntityReferenceSelector entityType="tag" inputId="starting-performer" onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(screen.getByLabelText("Starting performer")).toHaveAttribute("id", "starting-performer");
  });

  it("returns focus to the input after keyboard-clearing an input-displayed selection", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    function StatefulSelector() {
      const [value, setValue] = useState<number | undefined>(7);
      return (
        <EntityReferenceSelector
          entityType="tag"
          value={value}
          selectedDisplay="input"
          selectedLabel="Existing tag"
          onChange={(next) => {
            onChange(next);
            setValue(next);
          }}
        />
      );
    }

    render(
      <QueryClientProvider client={queryClient}>
        <StatefulSelector />
      </QueryClientProvider>,
    );

    const input = screen.getByRole("combobox");
    const clear = screen.getByRole("button", { name: "Clear selected tag" });
    act(() => clear.focus());
    await user.keyboard("{Enter}");

    expect(onChange).toHaveBeenCalledWith(undefined);
    expect(input).toHaveValue("");
    expect(input).toHaveFocus();
  });

  it("does not show an add icon for replacement options but keeps it for create-new", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 7, name: "Massage" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="tag" onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    await user.type(screen.getByPlaceholderText("Search tags..."), "mass");
    const existingOption = await screen.findByRole("option", { name: "Massage" });
    const createOption = screen.getByRole("option", { name: "Create “mass”" });

    expect(existingOption.querySelector(".lucide-plus")).not.toBeInTheDocument();
    expect(createOption.querySelector(".lucide-plus")).toBeInTheDocument();
  });

  it("portals results into an interaction surface and selects them by click", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 7, name: "Existing tag" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const portalContainer = document.createElement("div");
    document.body.append(portalContainer);

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector
          entityType="tag"
          onChange={onChange}
          allowCreate={false}
          dropdownPortalContainer={portalContainer}
        />
      </QueryClientProvider>,
    );

    await user.type(screen.getByPlaceholderText("Search tags..."), "Existing");
    const option = await screen.findByRole("option", { name: /Existing tag/i });
    expect(option.closest("[role=listbox]")?.parentElement).toBe(portalContainer);

    await user.click(option);
    expect(onChange).toHaveBeenCalledWith(7, { id: 7, label: "Existing tag" });
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();

    portalContainer.remove();
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

  it("portals results into the active fullscreen ancestor using container-relative coordinates", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Fullscreen tag" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <div data-testid="fullscreen-root">
          <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
        </div>
      </QueryClientProvider>,
    );

    const fullscreenRoot = screen.getByTestId("fullscreen-root");
    Object.defineProperty(document, "fullscreenElement", {
      configurable: true,
      value: fullscreenRoot,
    });
    vi.spyOn(fullscreenRoot, "getBoundingClientRect").mockReturnValue({
      x: 100,
      y: 200,
      left: 100,
      top: 200,
      right: 900,
      bottom: 800,
      width: 800,
      height: 600,
      toJSON: () => ({}),
    });

    const input = screen.getByPlaceholderText("Search tags...");
    vi.spyOn(input, "getBoundingClientRect").mockReturnValue({
      x: 120,
      y: 240,
      left: 120,
      top: 240,
      right: 320,
      bottom: 280,
      width: 200,
      height: 40,
      toJSON: () => ({}),
    });
    await user.type(input, "full");

    const result = await screen.findByRole("option", { name: /Fullscreen tag/i });
    const dropdown = result.parentElement;
    expect(dropdown?.parentElement).toBe(fullscreenRoot);
    expect(fullscreenRoot).toHaveStyle({ position: "relative" });
    expect(dropdown).toHaveStyle({ left: "20px", top: "84px", width: "200px" });
  });

  it("lets callers hide the create-new affordance", async () => {
    const user = userEvent.setup();
    mocks.tagsFind.mockResolvedValue({ items: [] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} allowCreate={false} />
      </QueryClientProvider>,
    );

    await user.type(screen.getByPlaceholderText("Search tags..."), "Restricted");
    expect(await screen.findByText("No tags found")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Create “Restricted”" })).not.toBeInTheDocument();
  });
});

// The video list's filter chips keep the first 5,000 tags by name under ["tags", "all"]; a library with more
// tags than that must still be searched on the server.
describe("searching with a partial list of every tag in the cache", () => {
  const partialList = [{ id: 1, name: "Group Makeup Specific: Solo" }];
  const serverResults = { items: [{ id: 2, name: "Solo" }, { id: 3, name: "Solo Female" }, ...partialList] };

  function renderWithCachedList(selector: React.ReactElement) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["tags", "all"], partialList);
    render(<QueryClientProvider client={queryClient}>{selector}</QueryClientProvider>);
  }

  it("finds a tag the cached list does not hold in the multi selector", async () => {
    mocks.tagsFind.mockResolvedValue(serverResults);
    const onChange = vi.fn();
    renderWithCachedList(<EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Solo");

    await userEvent.click(await screen.findByRole("option", { name: "Solo Female" }));
    expect(mocks.tagsFind).toHaveBeenCalledWith(expect.objectContaining({ q: "Solo" }), expect.anything());
    expect(onChange).toHaveBeenCalledWith([3]);
  });

  it("finds a tag the cached list does not hold in the single selector", async () => {
    mocks.tagsFind.mockResolvedValue(serverResults);
    const onChange = vi.fn();
    renderWithCachedList(<EntityReferenceSelector entityType="tag" onChange={onChange} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Solo");

    expect(await screen.findByRole("option", { name: "Solo" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole("option", { name: "Solo" }));
    expect(onChange).toHaveBeenCalledWith(2, expect.objectContaining({ label: "Solo" }));
  });
});

describe("searching again after creating", () => {
  it("offers the created tag rather than creating it again", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [] });
    // From the create on, the server finds the new tag.
    mocks.tagsCreate.mockImplementation(async () => {
      mocks.tagsFind.mockResolvedValue({ items: [{ id: 9, name: "Fresh Tag" }] });
      return { id: 9, name: "Fresh Tag" };
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
      </QueryClientProvider>,
    );
    const input = screen.getByPlaceholderText("Search tags...");
    await userEvent.type(input, "Fresh Tag");
    await userEvent.click(await screen.findByRole("option", { name: /Create/ }));
    await waitFor(() => expect(mocks.tagsCreate).toHaveBeenCalledOnce());

    await userEvent.type(input, "Fresh Tag");

    expect(await screen.findByRole("option", { name: "Fresh Tag" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
  });
});

describe("tag descriptions in the selectors", () => {
  const description = "Scenes that start the action within a few minutes.";

  it("shows a selected tag chip's description on hover", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10]}
          onChange={vi.fn()}
          seedOptions={[
            toTagOption({
              id: 10,
              name: "Described",
              description,
              color: null,
              tagGroupColor: null,
              tagGroupName: null,
            }),
          ]}
        />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(screen.getByText("Described"));

    expect(screen.getByRole("tooltip", { name: "About Described" })).toHaveTextContent(description);
  });

  it("shows the selected tag's description on hover in the single selector", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(
      ["entity-reference-selector", "tag", "selected", 10],
      toTagOption({
        id: 10,
        name: "Described",
        description,
        color: null,
        tagGroupColor: null,
        tagGroupName: null,
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="tag" value={10} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(screen.getByText("Described"));

    expect(screen.getByRole("tooltip", { name: "About Described" })).toHaveTextContent(description);
  });

  it("shows a search result's image and description in the chip hover popup, and still selects it on click", async () => {
    const onChange = vi.fn();
    mocks.tagsFind.mockResolvedValue({
      items: [
        { id: 1, name: "Described", description, imagePath: "/described.jpg" },
        { id: 2, name: "Undescribed" },
      ],
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "scrib");
    const option = await screen.findByRole("option", { name: /Described/ });

    // Only screen readers get the description in the row; sighted users see it on hover.
    expect(within(option).getByText(description).closest(".sr-only")).not.toBeNull();
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();

    fireEvent.mouseEnter(within(option).getByText("Described"));

    const popup = screen.getByRole("tooltip", { name: "Media for Described" });
    expect(popup).toHaveTextContent(description);
    expect(within(popup).getByRole("img", { name: "Described" })).toHaveAttribute("src", "/described.jpg");

    fireEvent.mouseEnter(within(screen.getByRole("option", { name: "Undescribed" })).getByText("Undescribed"));
    expect(screen.queryByRole("tooltip", { name: /Undescribed/ })).not.toBeInTheDocument();

    await userEvent.click(within(screen.getByRole("option", { name: /Described/ })).getByText("Described"));
    expect(onChange).toHaveBeenCalledWith([1]);
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
  });

  it("shows the hover popup on the single selector's results", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Described", description }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="tag" onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "scrib");
    fireEvent.mouseEnter(within(await screen.findByRole("option", { name: /Described/ })).getByText("Described"));

    expect(screen.getByRole("tooltip", { name: "About Described" })).toHaveTextContent(description);
  });

  it("shows a selected chip's image like the Details tab badge", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[10]}
          onChange={vi.fn()}
          seedOptions={[
            toTagOption({
              id: 10,
              name: "Pictured",
              imagePath: "/pictured.jpg",
              color: null,
              tagGroupColor: null,
              tagGroupName: null,
            }),
          ]}
        />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(screen.getByText("Pictured"));

    expect(screen.getByRole("img", { name: "Pictured" })).toHaveAttribute("src", "/pictured.jpg");
  });

  it("does not describe non-tag chips", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="performer"
          values={[10]}
          onChange={vi.fn()}
          seedOptions={[{ id: 10, label: "Someone", tagDescription: description }]}
        />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(screen.getByText("Someone"));

    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
  });
});

describe("performer images in the selectors", () => {
  it("shows a search result's image in a portrait hover popup, and still selects it on click", async () => {
    const onChange = vi.fn();
    mocks.performersFind.mockResolvedValue({
      items: [
        { id: 1, name: "Pictured", imagePath: "/pictured.jpg" },
        { id: 2, name: "Unpictured" },
      ],
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector entityType="performer" values={[]} onChange={onChange} />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByPlaceholderText("Search performers..."), "pic");
    const option = await screen.findByRole("option", { name: "Pictured" });
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();

    fireEvent.mouseEnter(within(option).getByText("Pictured"));

    const popup = screen.getByRole("tooltip", { name: "Media for Pictured" });
    expect(within(popup).getByRole("img", { name: "Pictured" })).toHaveAttribute("src", "/pictured.jpg");
    expect(popup.querySelector<HTMLElement>("[data-media-frame]")?.style.aspectRatio).toBe("2 / 3");

    fireEvent.mouseEnter(within(screen.getByRole("option", { name: "Unpictured" })).getByText("Unpictured"));
    expect(screen.queryByRole("tooltip", { name: /Unpictured/ })).not.toBeInTheDocument();

    await userEvent.click(within(screen.getByRole("option", { name: "Pictured" })).getByText("Pictured"));
    expect(onChange).toHaveBeenCalledWith([1]);
  });

  it("shows the image on the single selector's results", async () => {
    mocks.performersFind.mockResolvedValue({ items: [{ id: 1, name: "Pictured", imagePath: "/pictured.jpg" }] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="performer" onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByPlaceholderText("Search performers..."), "pic");
    fireEvent.mouseEnter(within(await screen.findByRole("option", { name: "Pictured" })).getByText("Pictured"));

    expect(screen.getByRole("img", { name: "Pictured" })).toHaveAttribute("src", "/pictured.jpg");
  });

  it("shows a selected performer chip's image on hover", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceMultiSelector
          entityType="performer"
          values={[10]}
          onChange={vi.fn()}
          // Edit forms seed their chips from the performers a video, image or gallery already lists.
          seedOptions={[toPerformerOption({ id: 10, name: "Pictured", favorite: false, imagePath: "/pictured.jpg" })]}
        />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(screen.getByText("Pictured"));

    expect(screen.getByRole("img", { name: "Pictured" })).toHaveAttribute("src", "/pictured.jpg");
  });

  it("shows the selected performer's image in the single selector, as loaded by id", async () => {
    mocks.performersGet.mockResolvedValue({ id: 10, name: "Pictured", imagePath: "/pictured.jpg" });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <EntityReferenceSelector entityType="performer" value={10} onChange={vi.fn()} />
      </QueryClientProvider>,
    );

    fireEvent.mouseEnter(await screen.findByText("Pictured"));

    expect(mocks.performersGet).toHaveBeenCalledWith(10);
    expect(screen.getByRole("img", { name: "Pictured" })).toHaveAttribute("src", "/pictured.jpg");
  });
});

// Tag names and aliases share one namespace on the server, so a name typed as an existing tag's alias is that tag.
describe("typing an existing tag's alias", () => {
  const aliased = { id: 7, name: "Canonical Tag", aliases: ["Alias Name"] };

  function renderSelector(selector: React.ReactElement) {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(<QueryClientProvider client={queryClient}>{selector}</QueryClientProvider>);
  }

  it("offers the aliased tag instead of creating a duplicate", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [aliased] });
    const onChange = vi.fn();
    renderSelector(<EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "alias name{Enter}");

    expect(onChange).toHaveBeenCalledWith([7]);
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
    expect(mocks.tagsCreate).not.toHaveBeenCalled();
  });

  it("says the aliased tag is already added in the multi selector", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [aliased] });
    renderSelector(
      <EntityReferenceMultiSelector
        entityType="tag"
        values={[7]}
        onChange={vi.fn()}
        seedOptions={[toTagOption(aliased)]}
      />,
    );

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Alias Name");

    expect(
      await screen.findByText("“Alias Name” is an alias of “Canonical Tag”, which is already added."),
    ).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
    expect(screen.queryByText("No tags found")).not.toBeInTheDocument();
  });

  it("says the aliased tag is already selected in the single selector", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [aliased] });
    renderSelector(<EntityReferenceSelector entityType="tag" value={7} onChange={vi.fn()} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Alias Name");

    expect(
      await screen.findByText("“Alias Name” is an alias of “Canonical Tag”, which is already added."),
    ).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
  });
});

describe("typing an existing tag's name or alias when it cannot be offered", () => {
  function renderSelector(selector: React.ReactElement) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={queryClient}>{selector}</QueryClientProvider>);
  }

  it("says a tag typed by its own name is already added", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 7, name: "Canonical Tag" }] });
    renderSelector(<EntityReferenceMultiSelector entityType="tag" values={[7]} onChange={vi.fn()} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "canonical tag");

    expect(await screen.findByText("“Canonical Tag” is already added.")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
  });

  it("does not offer to create an excluded tag's alias", async () => {
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 7, name: "Canonical Tag", aliases: ["Alias Name"] }] });
    renderSelector(<EntityReferenceMultiSelector entityType="tag" values={[]} excludeIds={[7]} onChange={vi.fn()} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Alias Name");

    expect(await screen.findByText("No tags found")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
  });

  it("keeps an exact alias match among many tags whose names contain the alias", async () => {
    const namesContaining = Array.from({ length: 30 }, (_, index) => ({ id: index + 100, name: `Solo ${index}` }));
    mocks.tagsFind.mockResolvedValue({
      items: [...namesContaining, { id: 7, name: "Single Performer", aliases: ["Solo"] }],
    });
    renderSelector(<EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />);

    await userEvent.type(screen.getByPlaceholderText("Search tags..."), "Solo");

    const options = await screen.findAllByRole("option");
    expect(options[0]).toHaveTextContent("Single Performer");
    expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
  });
});

describe("a create the server rejects", () => {
  const detail = 'A tag alias with name "Taken" already exists. Tag names and tag aliases must be unique.';

  afterEach(() => resetMutationFailureForTests());

  it.each(["multi", "single"] as const)(
    "shows the server's reason in place of Create in the %s selector",
    async (kind) => {
      mocks.tagsFind.mockResolvedValue({ items: [] });
      mocks.tagsCreate.mockRejectedValue(new Error(`API Error 409: ${JSON.stringify({ message: detail })}`));
      const queryClient = createAppQueryClient();
      render(
        <QueryClientProvider client={queryClient}>
          <MutationFailureNotice />
          {kind === "multi" ? (
            <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={vi.fn()} />
          ) : (
            <EntityReferenceSelector entityType="tag" onChange={vi.fn()} />
          )}
        </QueryClientProvider>,
      );
      const input = screen.getByPlaceholderText("Search tags...");

      await userEvent.type(input, "Taken");
      await userEvent.click(await screen.findByRole("option", { name: "Create “Taken”" }));

      expect(await within(screen.getByRole("listbox")).findByRole("alert")).toHaveTextContent(detail);
      expect(screen.queryByRole("option", { name: /Create/ })).not.toBeInTheDocument();
      expect(screen.queryByText("No tags found")).not.toBeInTheDocument();
      expect(screen.queryByText("Couldn’t complete the action")).not.toBeInTheDocument();

      await userEvent.type(input, "x");
      expect(screen.queryByRole("alert")).not.toBeInTheDocument();
      expect(await screen.findByRole("option", { name: "Create “Takenx”" })).toBeInTheDocument();
    },
  );
});
