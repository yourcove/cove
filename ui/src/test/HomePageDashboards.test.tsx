import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getCarouselPageDestinations, HomePage } from "../pages/HomePage";
import { navigateToUrl } from "../router/location";

const { state, mocks } = vi.hoisted(() => ({
  state: {
    legacyContent: "[]",
    userId: "7",
    dashboardDefinitions: [] as Array<{ id: string; label: string; extensionId: string; componentName: string; editorComponentName?: string; description?: string; allowMultiple: boolean; order: number; supportedPresentations?: Array<"flow" | "canvas">; defaultPresentation?: "flow" | "canvas" }>,
    extensionComponents: {} as Record<string, (props: any) => React.ReactNode>,
    savedFilters: [] as Array<{ id: number; name: string; mode: string; findFilter: string; objectFilter: string; uiOptions: string }>,
    dashboards: [] as Array<{ id: number; name: string; isDefault: boolean; version: number; createdAt: string; updatedAt: string }>,
    active: null as null | { id: number; name: string; isDefault: boolean; version: number; createdAt: string; updatedAt: string; widgets: Array<{ instanceId: string; owner: string; widgetKey: string; label: string; configuration: unknown; presentation?: "flow" | "canvas" }> },
  },
  mocks: {
    bootstrap: vi.fn(),
    list: vi.fn(),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    videosFind: vi.fn(async (): Promise<any> => ({ items: [], totalCount: 0 })),
    groupsFind: vi.fn(async (): Promise<any> => ({ items: [], totalCount: 0 })),
    groupItemsPage: vi.fn(async () => ({ items: [], totalCount: 0, page: 1, perPage: 12 })),
    savedFilterGet: vi.fn(),
    savedFiltersList: vi.fn(),
  },
}));

vi.mock("../api/client", () => ({
  videos: { find: mocks.videosFind, findFiltered: vi.fn(), screenshotUrl: (id: number) => `/api/stream/video/${id}/screenshot` },
  performers: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  studios: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  tags: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  galleries: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  groups: {
    find: mocks.groupsFind,
    findFiltered: vi.fn(),
    items: { list: vi.fn(async () => []), page: mocks.groupItemsPage },
  },
  savedFilters: { get: mocks.savedFilterGet, list: mocks.savedFiltersList },
  dashboards: {
    bootstrap: mocks.bootstrap,
    list: mocks.list,
    get: mocks.get,
    create: mocks.create,
    update: mocks.update,
    duplicate: vi.fn(),
    setDefault: vi.fn(),
    delete: mocks.delete,
  },
}));

vi.mock("../hooks/useEntityEngagementBatch", () => ({
  useEntityEngagementBatch: () => ({ engagementById: new Map() }),
}));

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
}));

vi.mock("../utils/userUiPreferences", () => ({
  readAuthenticatedUserHomePageContent: () => state.legacyContent,
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    manifest: { dashboardWidgets: state.dashboardDefinitions },
    resolveComponent: (_extensionId: string, componentName: string) => state.extensionComponents[componentName],
    getExtensionRevision: () => 0,
  }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { id: state.userId, kind: "user" }, hasPermission: () => true }),
}));

function summary(id: number, name: string, isDefault = false) {
  return { id, name, isDefault, version: 1, createdAt: "", updatedAt: "" };
}

function dashboard(id: number, name: string, isDefault = false, widgets: NonNullable<typeof state.active>["widgets"] = []) {
  return { ...summary(id, name, isDefault), widgets };
}

function renderHome(onNavigate = vi.fn(), dashboardId?: number, client = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  return {
    onNavigate,
    ...render(
      <QueryClientProvider client={client}>
        <HomePage onNavigate={onNavigate} dashboardId={dashboardId} />
      </QueryClientProvider>,
    ),
  };
}

describe("HomePage dashboards", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    state.legacyContent = "[]";
    state.userId = "7";
    state.dashboardDefinitions = [];
    state.extensionComponents = {};
    state.savedFilters = [];
    state.dashboards = [summary(1, "Home", true)];
    state.active = dashboard(1, "Home", true);
    mocks.bootstrap.mockImplementation(async () => state.active);
    mocks.list.mockImplementation(async () => state.dashboards);
    mocks.get.mockImplementation(async (id: number) => {
      if (state.active?.id === id) return state.active;
      throw new Error("Dashboard not found");
    });
    mocks.create.mockImplementation(async (name: string) => dashboard(2, name));
    mocks.update.mockImplementation(async (_id: number, request: { name: string }) => ({ ...state.active!, name: request.name }));
    mocks.savedFilterGet.mockImplementation(async () => ({ id: 5, name: `Filter ${state.userId}`, mode: "videos", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" }));
    mocks.savedFiltersList.mockImplementation(async () => state.savedFilters);
    mocks.delete.mockImplementation(async (id: number) => {
      state.dashboards = state.dashboards.filter((item) => item.id !== id).map((item) => ({ ...item, isDefault: true }));
      const fallback = state.dashboards[0];
      state.active = fallback ? dashboard(fallback.id, fallback.name, true) : null;
    });

    class ResizeObserverMock {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverMock);
  });

  it("bootstraps the first dashboard from the legacy home-page layout", async () => {
    state.legacyContent = JSON.stringify([
      { type: "continueWatching" },
      { type: "custom", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
    ]);

    renderHome();

    await waitFor(() => expect(mocks.bootstrap).toHaveBeenCalledOnce());
    expect(mocks.bootstrap).toHaveBeenCalledWith([
      expect.objectContaining({ owner: "cove.core", widgetKey: "continue-watching", label: "Continue Watching", configuration: {} }),
      expect.objectContaining({
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recently Added Videos",
        configuration: { source: "premade", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
      }),
    ]);
  });

  it("switches non-default dashboards through their stable URL", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "Research")];
    const { onNavigate } = renderHome();

    fireEvent.change(await screen.findByRole("combobox", { name: "Dashboard" }), { target: { value: "2" } });

    expect(onNavigate).toHaveBeenCalledWith({ page: "dashboard", id: 2 });
  });

  it("falls back to the default dashboard when a requested dashboard is missing", async () => {
    const { onNavigate } = renderHome(vi.fn(), 404);

    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "home" }));
    expect(await screen.findByRole("combobox", { name: "Dashboard" })).toHaveValue("1");
  });

  it("keeps an anonymous standard dashboard read-only", async () => {
    state.legacyContent = JSON.stringify([
      { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recent Videos" },
    ]);
    mocks.bootstrap.mockRejectedValueOnce(new Error("API Error 401: unauthorized"));

    renderHome();

    expect(await screen.findByRole("combobox", { name: "Dashboard" })).toHaveValue("0");
    expect(screen.getByRole("combobox", { name: "Dashboard" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: /Customize/ })).not.toBeInTheDocument();
  });

  it("does not reuse one user's personal dashboard cache for another user", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 }, mutations: { retry: false } } });
    const first = renderHome(vi.fn(), undefined, client);
    expect(await screen.findByRole("combobox", { name: "Dashboard" })).toHaveValue("1");
    first.unmount();

    state.userId = "8";
    state.dashboards = [summary(2, "Second Home", true)];
    state.active = dashboard(2, "Second Home", true);
    renderHome(vi.fn(), undefined, client);

    expect(await screen.findByRole("combobox", { name: "Dashboard" })).toHaveValue("2");
    expect(mocks.get).toHaveBeenLastCalledWith(2);
  });

  it("does not reuse personal widget queries when accounts share entity ids", async () => {
    const widgets = [
      { instanceId: "continue", owner: "cove.core", widgetKey: "continue-watching", label: "Continue Watching", configuration: {} },
      { instanceId: "saved", owner: "cove.core", widgetKey: "collection", label: "Saved", configuration: { source: "saved", savedFilterId: 5 } },
    ];
    state.active = dashboard(1, "Home", true, widgets);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 }, mutations: { retry: false } } });
    const first = renderHome(vi.fn(), undefined, client);
    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(mocks.groupsFind).toHaveBeenCalledTimes(1));
    first.unmount();

    state.userId = "8";
    state.active = dashboard(1, "Home", true, widgets);
    renderHome(vi.fn(), undefined, client);

    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.groupsFind).toHaveBeenCalledTimes(2));
  });

  it("shows and retries a failed built-in collection widget", async () => {
    mocks.videosFind.mockRejectedValueOnce(new Error("Collection request failed"));
    mocks.videosFind.mockResolvedValueOnce({ items: [{ id: 101, title: "Recovered video", files: [], tags: [], performers: [] }], totalCount: 1 });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "collection",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Recent videos",
      configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent videos" },
    }]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Recent videos could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Recent videos" }));

    expect(await screen.findByText("Recovered video")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows and retries a failed Continue Watching widget", async () => {
    mocks.groupsFind.mockRejectedValueOnce(new Error("Continue Watching request failed"));
    mocks.groupsFind.mockResolvedValueOnce({ items: [], totalCount: 0 });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "continue",
      owner: "cove.core",
      widgetKey: "continue-watching",
      label: "Continue Watching",
      configuration: {},
    }]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Continue Watching could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Continue Watching" }));

    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(mocks.groupsFind).toHaveBeenCalledTimes(2);
  });

  it("shows and retries a failed Continue Watching item request", async () => {
    mocks.groupsFind.mockResolvedValueOnce({ items: [{ id: 3, querySourceKey: "continue-watching" }], totalCount: 1 });
    mocks.groupItemsPage.mockRejectedValueOnce(new Error("Continue Watching items failed"));
    mocks.groupItemsPage.mockResolvedValueOnce({ items: [], totalCount: 0, page: 1, perPage: 12 });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "continue",
      owner: "cove.core",
      widgetKey: "continue-watching",
      label: "Continue Watching",
      configuration: {},
    }]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Continue Watching could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Continue Watching" }));

    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(mocks.groupItemsPage).toHaveBeenCalledTimes(2);
  });

  it("shows a saved-filter definition failure and still hides a successful empty retry", async () => {
    mocks.savedFilterGet.mockRejectedValueOnce(new Error("Saved filter request failed"));
    mocks.savedFilterGet.mockResolvedValueOnce({ id: 5, name: "Warnings", mode: "videos", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "saved",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Saved filter",
      configuration: { source: "saved", savedFilterId: 5 },
    }]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Saved filter could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Saved filter" }));

    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.videosFind).toHaveBeenCalledOnce());
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Warnings" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Customize/ }));
    expect(screen.getByText("Saved filter", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Configure/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Remove/ })).toBeInTheDocument();
  });

  it("shows and retries a saved-filter item-query failure", async () => {
    mocks.videosFind.mockRejectedValueOnce(new Error("Saved collection request failed"));
    mocks.videosFind.mockResolvedValueOnce({ items: [{ id: 102, title: "Recovered saved video", files: [], tags: [], performers: [] }], totalCount: 1 });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "saved",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Saved filter",
      configuration: { source: "saved", savedFilterId: 5 },
    }]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Filter 7 could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Filter 7" }));

    expect(await screen.findByText("Recovered saved video")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("disables dashboard draft controls while a save is pending", async () => {
    let resolveUpdate!: (value: NonNullable<typeof state.active>) => void;
    mocks.update.mockImplementationOnce(() => new Promise((resolve) => { resolveUpdate = resolve; }));
    state.active = dashboard(1, "Home", true, [{ instanceId: "one", owner: "cove.core", widgetKey: "collection", label: "Recent", configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent" } }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Configure/ }));
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument());
    expect(screen.getByRole("textbox", { name: "Dashboard name" })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Configure/ })).toBeDisabled();
    for (const button of screen.getAllByRole("button", { name: /Duplicate/ })) expect(button).toBeDisabled();
    expect(screen.getByRole("button", { name: /Remove/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Add Widget/ })).toBeDisabled();

    await act(async () => resolveUpdate(state.active!));
  });

  it("places the add widget action below the dashboard editing toolbar", async () => {
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));

    const addWidget = screen.getByRole("button", { name: /Add Widget/ });
    const toolbar = addWidget.parentElement?.firstElementChild;
    expect(addWidget.closest("header")).toBeNull();
    expect(toolbar?.tagName).toBe("HEADER");
    expect(toolbar).toHaveClass("sticky", "top-14");
    expect(addWidget.parentElement?.children[1]).toBe(addWidget);
  });

  it("gives mobile widget titles a separate row above wrapping actions", async () => {
    state.active = dashboard(1, "Home", true, [{
      instanceId: "one",
      owner: "cove.core",
      widgetKey: "collection",
      label: "A long dashboard widget label",
      configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "A long dashboard widget label" },
    }]);
    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));

    const label = screen.getByText("A long dashboard widget label", { selector: "span" });
    const titleRow = label.parentElement!;
    const widgetHeader = titleRow.parentElement!;
    const actionRow = screen.getByRole("button", { name: /Configure/ }).parentElement!;
    expect(widgetHeader).toHaveClass("flex-col", "sm:flex-row");
    expect(titleRow).toHaveClass("min-w-0", "sm:flex-1");
    expect(actionRow).not.toBe(titleRow);
    expect(actionRow).toHaveClass("flex-wrap", "sm:justify-end");
  });

  it("traps catalog focus, closes on Escape, and restores the Add Widget trigger", async () => {
    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    const trigger = screen.getByRole("button", { name: /Add Widget/ });
    trigger.focus();
    fireEvent.click(trigger);

    const dialog = screen.getByRole("dialog", { name: "Add Widget" });
    const close = within(dialog).getByRole("button", { name: "Close" });
    await waitFor(() => expect(within(dialog).getByRole("searchbox", { name: "Search widgets" })).toHaveFocus());
    const enabledButtons = within(dialog).getAllByRole("button").filter((button) => !button.hasAttribute("disabled"));
    const lastButton = enabledButtons.at(-1)!;
    lastButton.focus();
    fireEvent.keyDown(lastButton, { key: "Tab" });
    expect(close).toHaveFocus();
    fireEvent.keyDown(close, { key: "Tab", shiftKey: true });
    expect(lastButton).toHaveFocus();

    fireEvent.keyDown(dialog, { key: "Escape" });
    expect(screen.queryByRole("dialog", { name: "Add Widget" })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it("manages focus and Escape for the widget configuration dialog", async () => {
    state.active = dashboard(1, "Home", true, [{
      instanceId: "one",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Recent",
      configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent" },
    }]);
    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    const trigger = screen.getByRole("button", { name: /Configure/ });
    trigger.focus();
    fireEvent.click(trigger);

    const dialog = screen.getByRole("dialog", { name: "Configure Recent" });
    const close = within(dialog).getByRole("button", { name: "Close" });
    await waitFor(() => expect(close).toHaveFocus());
    const save = within(dialog).getByRole("button", { name: "Save" });
    save.focus();
    fireEvent.keyDown(save, { key: "Tab" });
    expect(close).toHaveFocus();

    fireEvent.keyDown(dialog, { key: "Escape" });
    expect(screen.queryByRole("dialog", { name: "Configure Recent" })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it("scrolls an appended widget into view after adding it", async () => {
    const originalScrollIntoView = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
    const scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, "scrollIntoView", { configurable: true, value: scrollIntoView });
    const getBoundingClientRect = vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
      if (this.tagName === "HEADER") return { bottom: 180 } as DOMRect;
      return { bottom: 0, top: 0 } as DOMRect;
    });
    try {
      renderHome();

      fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
      fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
      fireEvent.click(screen.getByRole("button", { name: /^Recently Added Videos/ }));

      await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({ behavior: "smooth", block: "start" }));
      expect(screen.queryByRole("heading", { name: "Add Widget" })).not.toBeInTheDocument();
      const addedWidget = scrollIntoView.mock.contexts[0] as HTMLElement;
      expect(addedWidget).toContainElement(screen.getByText("Recently Added Videos", { selector: "span" }));
      expect(addedWidget).toHaveStyle({ scrollMarginTop: "184px" });
      expect(addedWidget?.parentElement?.parentElement).toHaveClass("pb-[calc(100dvh-4px)]");
    } finally {
      if (originalScrollIntoView) Object.defineProperty(Element.prototype, "scrollIntoView", originalScrollIntoView);
      else delete (Element.prototype as Partial<Element>).scrollIntoView;
      getBoundingClientRect.mockRestore();
    }
  });

  it("preserves configuration when an extension widget is unavailable", async () => {
    state.active = dashboard(1, "Home", true, [{
      instanceId: "pulse-1",
      owner: "example.extension",
      widgetKey: "pulse",
      label: "Library Pulse",
      configuration: { metrics: ["videos", "groups"] },
    }]);

    renderHome();

    expect(await screen.findByText("Library Pulse")).toBeInTheDocument();
    expect(screen.getByText(/Configuration has been preserved/)).toBeInTheDocument();
  });

  it("does not duplicate a single-instance extension widget", async () => {
    state.dashboardDefinitions = [{ id: "singleton", label: "Singleton", extensionId: "example.extension", componentName: "Widget", allowMultiple: false, order: 1 }];
    state.extensionComponents.Widget = () => <div>Singleton body</div>;
    state.active = dashboard(1, "Home", true, [{ instanceId: "one", owner: "example.extension", widgetKey: "singleton", label: "Singleton", configuration: {} }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));

    expect(screen.getAllByRole("button", { name: /Duplicate/ })).toHaveLength(1);
  });

  it("rejects non-JSON configuration emitted by an extension editor", async () => {
    state.dashboardDefinitions = [{ id: "configurable", label: "Configurable", extensionId: "example.extension", componentName: "Widget", editorComponentName: "Editor", allowMultiple: true, order: 1 }];
    state.extensionComponents.Widget = () => <div>Configurable body</div>;
    state.extensionComponents.Editor = ({ onChange }: { onChange: (configuration: unknown) => void }) => (
      <button onClick={() => {
        const cyclic: Record<string, unknown> = {};
        cyclic.self = cyclic;
        onChange(cyclic);
      }}>Emit invalid configuration</button>
    );
    state.active = dashboard(1, "Home", true, [{ instanceId: "one", owner: "example.extension", widgetKey: "configurable", label: "Configurable", configuration: {} }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Configure/ }));
    fireEvent.click(screen.getByRole("button", { name: "Emit invalid configuration" }));

    expect(screen.getByRole("alert")).toHaveTextContent("valid JSON data");
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("offers audio, text, and segment saved filters in the built-in collection catalog", async () => {
    state.savedFilters = [
      { id: 1, name: "Saved audio", mode: "audios", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
      { id: 2, name: "Saved text", mode: "texts", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
      { id: 3, name: "Saved spans", mode: "segments", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
      { id: 4, name: "Saved raw segments", mode: "rawsegments", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
    ];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    expect(await screen.findByRole("button", { name: /Saved audio/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Saved text/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Saved spans/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Saved raw segments/ })).toBeInTheDocument();
  });

  it("searches a large catalog while keeping widget sources clearly grouped", async () => {
    state.savedFilters = Array.from({ length: 105 }, (_, index) => ({
      id: index + 1,
      name: `Saved filter ${index}`,
      mode: "videos",
      findFilter: "{}",
      objectFilter: "{}",
      uiOptions: "{}",
    }));
    state.dashboardDefinitions = [{
      id: "curation-queue",
      label: "Curation Queue",
      description: "Review metadata warnings.",
      extensionId: "example.extension",
      componentName: "Widget",
      allowMultiple: true,
      order: 1,
    }];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    const dialog = screen.getByRole("dialog", { name: "Add Widget" });
    await waitFor(() => {
      const groupHeadings = within(dialog).getAllByRole("heading", { level: 3 });
      expect(groupHeadings.map((heading) => heading.textContent)).toEqual(["Built-in", "Saved Filters", "Extensions"]);
    });

    const search = within(dialog).getByRole("searchbox", { name: "Search widgets" });
    fireEvent.change(search, { target: { value: "Saved filter 104" } });
    expect(within(dialog).getByRole("button", { name: /^Saved filter 104/ })).toBeInTheDocument();
    expect(within(dialog).queryByRole("heading", { name: "Built-in" })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole("heading", { name: "Extensions" })).not.toBeInTheDocument();

    fireEvent.change(search, { target: { value: "nothing here" } });
    expect(within(dialog).getByText("No widgets match “nothing here”.")).toBeInTheDocument();
  });

  it("keeps the final partial carousel page selected after scrolling", async () => {
    mocks.videosFind.mockResolvedValueOnce({
      items: Array.from({ length: 25 }, (_, index) => ({ id: index + 1, title: `Video ${index + 1}`, files: [], tags: [], performers: [] })),
      totalCount: 25,
    });
    state.active = dashboard(1, "Home", true, [{
      instanceId: "collection",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Recent videos",
      configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent videos" },
    }]);
    const { container } = renderHome();
    await screen.findByText("Video 25");
    const scroller = container.querySelector<HTMLElement>(".recommendation-row .group\\/row > .flex")!;
    Object.defineProperties(scroller, {
      clientWidth: { configurable: true, value: 390 },
      scrollWidth: { configurable: true, value: 5700 },
      scrollLeft: { configurable: true, value: 0, writable: true },
    });
    const scrollTo = vi.fn((options: ScrollToOptions) => {
      scroller.scrollLeft = options.left === 5070 ? 5016 : Number(options.left);
      fireEvent.scroll(scroller);
    });
    Object.defineProperty(scroller, "scrollTo", { configurable: true, value: scrollTo });
    fireEvent.scroll(scroller);

    const next = await screen.findByRole("button", { name: "Next Recent videos page" });
    expect(next).toHaveClass("focus:opacity-100");
    fireEvent.click(screen.getByRole("button", { name: "Go to carousel page 14" }));
    expect(screen.getByRole("button", { name: "Go to carousel page 14" }).firstElementChild).toHaveClass("bg-foreground");
    fireEvent.click(screen.getByRole("button", { name: "Go to carousel page 15" }));

    expect(scrollTo).toHaveBeenLastCalledWith({ left: 5310, behavior: "smooth" });
    expect(screen.getByRole("button", { name: "Go to carousel page 15" }).firstElementChild).toHaveClass("bg-foreground");
    expect(screen.getByRole("button", { name: "Previous Recent videos page" })).toHaveClass("focus:opacity-100");
    expect(screen.queryByRole("button", { name: "Next Recent videos page" })).not.toBeInTheDocument();
    expect(getCarouselPageDestinations(5700, 390)).toHaveLength(15);
    expect(getCarouselPageDestinations(5700, 390).at(-1)).toBe(5310);
    expect(getCarouselPageDestinations(781, 390)).toEqual([0, 390]);
  });

  it("blocks canvas catalog items until the dashboard is empty", async () => {
    state.dashboardDefinitions = [{
      id: "group-feed",
      label: "Group Feed",
      description: "Browse one group as a mixed feed.",
      extensionId: "example.extension",
      componentName: "GroupFeedWidget",
      allowMultiple: false,
      order: 1,
      supportedPresentations: ["canvas"],
      defaultPresentation: "canvas",
    }];
    state.active = dashboard(1, "Home", true, [{ instanceId: "flow", owner: "cove.core", widgetKey: "collection", label: "Recent", configuration: {}, presentation: "flow" }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    expect(screen.getByRole("button", { name: /Group Feed/ })).toBeDisabled();
    expect(screen.getByText(/Canvas widgets need an empty dashboard/)).toBeInTheDocument();
  });

  it("adds a dual-presentation Canvas-default widget as Flow on a populated dashboard", async () => {
    state.dashboardDefinitions = [{
      id: "adaptive",
      label: "Adaptive Widget",
      extensionId: "example.extension",
      componentName: "AdaptiveWidget",
      allowMultiple: true,
      order: 1,
      supportedPresentations: ["flow", "canvas"],
      defaultPresentation: "canvas",
    }];
    state.extensionComponents.AdaptiveWidget = ({ presentation }: { presentation: string }) => <div>Adaptive {presentation}</div>;
    state.active = dashboard(1, "Home", true, [{ instanceId: "flow", owner: "cove.core", widgetKey: "collection", label: "Recent", configuration: {}, presentation: "flow" }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(screen.getByRole("button", { name: /Adaptive Widget/ }));

    expect(await screen.findByText("Adaptive flow")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(expect.objectContaining({
      widgets: expect.arrayContaining([expect.objectContaining({ widgetKey: "adaptive", presentation: "flow" })]),
    }));
  });

  it("adds a canvas-only contribution with its presentation and passes it to the widget", async () => {
    state.dashboardDefinitions = [{
      id: "group-feed",
      label: "Group Feed",
      extensionId: "example.extension",
      componentName: "GroupFeedWidget",
      allowMultiple: false,
      order: 1,
      supportedPresentations: ["canvas"],
      defaultPresentation: "canvas",
    }];
    state.extensionComponents.GroupFeedWidget = ({ presentation }: { presentation: string }) => <div>Rendered as {presentation}</div>;
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(screen.getByRole("button", { name: /Group Feed/ }));

    expect(await screen.findByText("Rendered as canvas")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(expect.objectContaining({
      widgets: [expect.objectContaining({ widgetKey: "group-feed", presentation: "canvas" })],
    }));
  });

  it("lets users repair a saved presentation the contribution no longer supports", async () => {
    state.dashboardDefinitions = [{
      id: "adaptive",
      label: "Adaptive Widget",
      extensionId: "example.extension",
      componentName: "AdaptiveWidget",
      allowMultiple: true,
      order: 1,
      supportedPresentations: ["flow"],
      defaultPresentation: "flow",
    }];
    state.extensionComponents.AdaptiveWidget = ({ presentation }: { presentation: string }) => <div>Adaptive {presentation}</div>;
    state.active = dashboard(1, "Home", true, [{ instanceId: "adaptive", owner: "example.extension", widgetKey: "adaptive", label: "Adaptive Widget", configuration: {}, presentation: "canvas" }]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    const presentation = screen.getByRole("combobox", { name: "Presentation for Adaptive Widget" });
    expect(presentation).toHaveValue("unsupported");
    fireEvent.change(presentation, { target: { value: "flow" } });
    expect(await screen.findByText("Adaptive flow")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(expect.objectContaining({
      widgets: [expect.objectContaining({ widgetKey: "adaptive", presentation: "flow" })],
    }));
  });

  it("creates another personal dashboard and opens it", async () => {
    vi.spyOn(window, "prompt").mockReturnValue("Discovery");
    const { onNavigate } = renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /New Dashboard/ }));

    await waitFor(() => expect(mocks.create).toHaveBeenCalledWith("Discovery"));
    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "dashboard", id: 2 }));
  });

  it("refreshes to the fallback after deleting the default dashboard at the home URL", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "Fallback")];
    vi.spyOn(window, "confirm").mockReturnValue(true);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /^Delete$/ }));

    expect(await screen.findByRole("combobox", { name: "Dashboard" })).toHaveValue("2");
    expect(screen.queryByText("Editing Dashboard")).not.toBeInTheDocument();
  });

  it("blocks in-app navigation while dashboard edits are unsaved", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.change(screen.getByRole("textbox", { name: "Dashboard name" }), { target: { value: "Changed" } });

    await act(async () => {
      expect(navigateToUrl("/videos")).toBe(false);
    });
    expect(window.location.pathname).toBe("/");

    await act(async () => {
      window.history.pushState(null, "", "/videos");
      window.dispatchEvent(new PopStateEvent("popstate", { state: null }));
    });
    expect(window.location.pathname).toBe("/");
  });
});
