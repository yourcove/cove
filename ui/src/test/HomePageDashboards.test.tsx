import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { HomePage } from "../pages/HomePage";
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
    videosFind: vi.fn(async () => ({ items: [], totalCount: 0 })),
    groupsFind: vi.fn(async () => ({ items: [], totalCount: 0 })),
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
    items: { list: vi.fn(async () => []), page: vi.fn(async () => ({ items: [], totalCount: 0, page: 1, perPage: 12 })) },
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

  it("offers only saved filters supported by built-in collection widgets", async () => {
    state.savedFilters = [
      { id: 1, name: "Supported videos", mode: "videos", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
      { id: 2, name: "Unsupported audio", mode: "audios", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
    ];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    expect(await screen.findByRole("button", { name: /Supported videos/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Unsupported audio/ })).not.toBeInTheDocument();
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
