import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Suspense, useState } from "react";
import { beforeEach, describe, expect, it, onTestFinished, vi } from "vitest";
import { AppRoutes } from "../App";
import { getCarouselPageDestinations, getWidgetRevealScrollDelta, HomePage } from "../pages/HomePage";
import { navigateToUrl } from "../router/location";
import type { Route } from "../router/location";
import { RouteRegistryProvider } from "../router/RouteRegistry";

const { state, mocks } = vi.hoisted(() => ({
  state: {
    legacyContent: "[]",
    userId: "7",
    dashboardDefinitions: [] as Array<{
      id: string;
      label: string;
      extensionId: string;
      componentName: string;
      editorComponentName?: string;
      description?: string;
      allowMultiple: boolean;
      order: number;
      supportedPresentations?: Array<"flow" | "canvas">;
      defaultPresentation?: "flow" | "canvas";
    }>,
    extensionComponents: {} as Record<string, (props: any) => React.ReactNode>,
    extensionsLoaded: true,
    pagesAwaitingExtensions: [] as string[],
    savedFilters: [] as Array<{
      id: number;
      name: string;
      mode: string;
      findFilter: string;
      objectFilter: string;
      uiOptions: string;
    }>,
    dashboards: [] as Array<{
      id: number;
      name: string;
      isDefault: boolean;
      version: number;
      createdAt: string;
      updatedAt: string;
    }>,
    active: null as null | {
      id: number;
      name: string;
      isDefault: boolean;
      version: number;
      createdAt: string;
      updatedAt: string;
      widgets: Array<{
        instanceId: string;
        owner: string;
        widgetKey: string;
        label: string;
        configuration: unknown;
        presentation?: "flow" | "canvas";
      }>;
    },
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
    groupItemsPage: vi.fn(async (): Promise<any> => ({ items: [], totalCount: 0, page: 1, perPage: 12 })),
    groupGet: vi.fn(),
    groupsFindFiltered: vi.fn(),
    savedFilterGet: vi.fn(),
    savedFiltersList: vi.fn(),
  },
}));

vi.mock("../api/client", () => ({
  videos: {
    find: mocks.videosFind,
    findFiltered: vi.fn(),
    screenshotUrl: (id: number) => `/api/stream/video/${id}/screenshot`,
  },
  performers: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  studios: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  tags: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  galleries: { find: vi.fn(async () => ({ items: [], totalCount: 0 })), findFiltered: vi.fn() },
  images: { thumbnailUrl: (id: number) => `/api/images/${id}/thumbnail` },
  groups: {
    find: mocks.groupsFind,
    findFiltered: mocks.groupsFindFiltered,
    get: mocks.groupGet,
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
    manifest: { dashboardWidgets: state.dashboardDefinitions, pages: [] },
    loaded: state.extensionsLoaded,
    extensionsSettling: !state.extensionsLoaded,
    isPageAwaitingExtensions: (page: string) => state.pagesAwaitingExtensions.includes(page),
    resolveComponent: (_extensionId: string, componentName: string) => state.extensionComponents[componentName],
    getExtensionRevision: () => 0,
    getPageOverride: () => undefined,
  }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { id: state.userId, kind: "user" }, hasPermission: () => true }),
}));

function summary(id: number, name: string, isDefault = false) {
  return { id, name, isDefault, version: 1, createdAt: "", updatedAt: "" };
}

function dashboard(
  id: number,
  name: string,
  isDefault = false,
  widgets: NonNullable<typeof state.active>["widgets"] = [],
) {
  return { ...summary(id, name, isDefault), widgets };
}

function renderHome(
  onNavigate = vi.fn(),
  dashboardId?: number,
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
  return {
    onNavigate,
    ...render(
      <QueryClientProvider client={client}>
        <HomePage onNavigate={onNavigate} dashboardId={dashboardId} />
      </QueryClientProvider>,
    ),
  };
}

function findCurrentDashboard(name: string) {
  return screen.findByRole("link", { name, current: "page" });
}

describe("HomePage dashboards", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    state.legacyContent = "[]";
    state.userId = "7";
    state.dashboardDefinitions = [];
    state.extensionComponents = {};
    state.extensionsLoaded = true;
    state.pagesAwaitingExtensions = [];
    state.savedFilters = [];
    state.dashboards = [summary(1, "Home", true)];
    state.active = dashboard(1, "Home", true);
    mocks.bootstrap.mockImplementation(async (widgets) => {
      if (state.dashboards.length === 0) {
        state.dashboards = [summary(1, "Home", true)];
        state.active = dashboard(1, "Home", true, widgets ?? []);
      }
      return state.active;
    });
    mocks.list.mockImplementation(async () => state.dashboards);
    mocks.get.mockImplementation(async (id: number) => {
      if (state.active?.id === id) return state.active;
      throw new Error("Dashboard not found");
    });
    mocks.create.mockImplementation(async (name: string) => dashboard(2, name));
    mocks.update.mockImplementation(async (_id: number, request: { name: string }) => ({
      ...state.active!,
      name: request.name,
    }));
    mocks.savedFilterGet.mockImplementation(async () => ({
      id: 5,
      name: `Filter ${state.userId}`,
      mode: "videos",
      findFilter: "{}",
      objectFilter: "{}",
      uiOptions: "{}",
    }));
    mocks.savedFiltersList.mockImplementation(async () => state.savedFilters);
    mocks.groupsFind.mockImplementation(async () => ({ items: [], totalCount: 0 }));
    mocks.groupItemsPage.mockImplementation(async () => ({ items: [], totalCount: 0, page: 1, perPage: 12 }));
    mocks.groupsFindFiltered.mockImplementation(async () => ({
      items: [{ id: 3, name: "Continue Watching", kind: "dynamic", querySourceKey: "continue-watching" }],
      totalCount: 1,
    }));
    mocks.groupGet.mockImplementation(async (id: number) =>
      id === 3
        ? { id, name: "Continue Watching", kind: "dynamic", querySourceKey: "continue-watching" }
        : { id, name: "Weekend queue", kind: "dynamic" },
    );
    mocks.delete.mockImplementation(async (id: number) => {
      state.dashboards = state.dashboards
        .filter((item) => item.id !== id)
        .map((item) => ({ ...item, isDefault: true }));
      const fallback = state.dashboards[0];
      state.active = fallback ? dashboard(fallback.id, fallback.name, true) : null;
    });

    class ResizeObserverMock {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverMock);
    vi.stubGlobal("scrollBy", vi.fn());
  });

  it("bootstraps the first dashboard from the legacy home-page layout", async () => {
    state.dashboards = [];
    state.legacyContent = JSON.stringify([
      { type: "continueWatching" },
      { type: "custom", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
    ]);

    renderHome();

    await waitFor(() => expect(mocks.bootstrap).toHaveBeenCalledOnce());
    expect(mocks.bootstrap).toHaveBeenCalledWith([
      expect.objectContaining({
        owner: "cove.core",
        widgetKey: "collection",
        label: "Continue Watching",
        configuration: { source: "group", groupId: 3 },
      }),
      expect.objectContaining({
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recently Added Videos",
        configuration: {
          source: "premade",
          mode: "videos",
          sortBy: "created_at",
          direction: "desc",
          header: "Recently Added Videos",
        },
      }),
    ]);
  });

  it("skips the bootstrap payload and its group lookup once the account has a dashboard", async () => {
    state.legacyContent = JSON.stringify([{ type: "continueWatching" }]);

    renderHome();

    await waitFor(() => expect(mocks.get).toHaveBeenCalledWith(1));
    expect(mocks.bootstrap).not.toHaveBeenCalled();
    expect(mocks.groupsFindFiltered).not.toHaveBeenCalled();
  });

  it("gives legacy saved-filter widgets an identifying fallback label", async () => {
    state.dashboards = [];
    state.legacyContent = JSON.stringify([{ type: "continueWatching" }, { type: "saved", savedFilterId: 42 }]);

    renderHome();

    await waitFor(() => expect(mocks.bootstrap).toHaveBeenCalledOnce());
    expect(mocks.bootstrap).toHaveBeenCalledWith([
      expect.objectContaining({ configuration: { source: "group", groupId: 3 } }),
      expect.objectContaining({
        owner: "cove.core",
        widgetKey: "collection",
        label: "Saved filter #42",
        configuration: { source: "saved", savedFilterId: 42 },
      }),
    ]);
  });

  it("switches non-default dashboards through their stable URL", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "Research")];
    const { onNavigate } = renderHome();

    const research = await screen.findByRole("link", { name: "Research" });
    expect(await findCurrentDashboard("Home")).toHaveAttribute("href", "/");
    expect(research).toHaveAttribute("href", "/dashboard/2");
    expect(research).not.toHaveAttribute("aria-current");
    expect(screen.queryByRole("heading", { name: "Cove" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "Home" })).toBeInTheDocument();

    fireEvent.click(await findCurrentDashboard("Home"));
    expect(onNavigate).not.toHaveBeenCalled();

    fireEvent.click(research);
    expect(onNavigate).toHaveBeenCalledWith({ page: "dashboard", id: 2 });
  });

  it("fades the switcher edges that have dashboards scrolled out of view", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "Research"), summary(3, "Archive")];
    renderHome();

    const nav = await screen.findByRole("navigation", { name: "Dashboards" });
    expect(nav).not.toHaveAttribute("data-fade-start");
    expect(nav).not.toHaveAttribute("data-fade-end");

    Object.defineProperty(nav, "clientWidth", { configurable: true, value: 200 });
    Object.defineProperty(nav, "scrollWidth", { configurable: true, value: 300 });
    fireEvent.scroll(nav);
    expect(nav).not.toHaveAttribute("data-fade-start");
    expect(nav).toHaveAttribute("data-fade-end");

    nav.scrollLeft = 50;
    fireEvent.scroll(nav);
    expect(nav).toHaveAttribute("data-fade-start");
    expect(nav).toHaveAttribute("data-fade-end");

    nav.scrollLeft = 100;
    fireEvent.scroll(nav);
    expect(nav).toHaveAttribute("data-fade-start");
    expect(nav).not.toHaveAttribute("data-fade-end");
  });

  it("scrolls the current dashboard clear of the switcher's edge", async () => {
    onTestFinished(() => {
      vi.restoreAllMocks();
    });
    // Each name is 100px wide and starts every 100px; the nav shows 200px of them.
    const layout = (element: HTMLElement) => {
      const index = ["Home", "Research", "Archive", "Later"].indexOf(element.textContent ?? "");
      return element.tagName === "A" && index >= 0 ? { offsetLeft: index * 100, offsetWidth: 100 } : null;
    };
    vi.spyOn(HTMLElement.prototype, "offsetLeft", "get").mockImplementation(function (this: HTMLElement) {
      return layout(this)?.offsetLeft ?? 0;
    });
    vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(function (this: HTMLElement) {
      return layout(this)?.offsetWidth ?? 0;
    });
    vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockReturnValue(200);
    const getComputedStyle = window.getComputedStyle.bind(window);
    vi.spyOn(window, "getComputedStyle").mockImplementation((element, pseudo) => {
      const style = getComputedStyle(element, pseudo);
      if (element.tagName === "NAV") Object.defineProperty(style, "scrollPaddingLeft", { value: "32px" });
      return style;
    });
    state.dashboards = [summary(1, "Home", true), summary(2, "Research"), summary(3, "Archive"), summary(4, "Later")];
    state.active = dashboard(3, "Archive");

    renderHome(vi.fn(), 3);

    await findCurrentDashboard("Archive");
    // Archive spans 200–300px; with 32px kept clear it needs to end at 332px of a 200px view.
    expect(screen.getByRole("navigation", { name: "Dashboards" }).scrollLeft).toBe(132);
  });

  it("falls back to the default dashboard when a requested dashboard is missing", async () => {
    const { onNavigate } = renderHome(vi.fn(), 404);

    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "home" }));
    expect(await findCurrentDashboard("Home")).toBeInTheDocument();
  });

  it("keeps an anonymous standard dashboard read-only", async () => {
    state.legacyContent = JSON.stringify([
      { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recent Videos" },
    ]);
    mocks.list.mockRejectedValueOnce(new Error("API Error 401: unauthorized"));

    renderHome();

    expect(await findCurrentDashboard("Standard")).toHaveAttribute("href", "/");
    expect(screen.queryByRole("button", { name: /Customize/ })).not.toBeInTheDocument();
  });

  it("does not reuse one user's personal dashboard cache for another user", async () => {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: 30_000 }, mutations: { retry: false } },
    });
    const first = renderHome(vi.fn(), undefined, client);
    expect(await findCurrentDashboard("Home")).toBeInTheDocument();
    first.unmount();

    state.userId = "8";
    state.dashboards = [summary(2, "Second Home", true)];
    state.active = dashboard(2, "Second Home", true);
    renderHome(vi.fn(), undefined, client);

    expect(await findCurrentDashboard("Second Home")).toBeInTheDocument();
    expect(mocks.get).toHaveBeenLastCalledWith(2);
  });

  it("does not reuse personal widget queries when accounts share entity ids", async () => {
    const widgets = [
      {
        instanceId: "continue",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Continue Watching",
        configuration: { source: "group", groupId: 3 },
      },
      {
        instanceId: "saved",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Saved",
        configuration: { source: "saved", savedFilterId: 5 },
      },
    ];
    state.active = dashboard(1, "Home", true, widgets);
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: 30_000 }, mutations: { retry: false } },
    });
    const first = renderHome(vi.fn(), undefined, client);
    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(mocks.groupGet).toHaveBeenCalledTimes(1));
    first.unmount();

    state.userId = "8";
    state.active = dashboard(1, "Home", true, widgets);
    renderHome(vi.fn(), undefined, client);

    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.groupGet).toHaveBeenCalledTimes(2));
  });

  it("shows and retries a failed built-in collection widget", async () => {
    mocks.videosFind.mockRejectedValueOnce(new Error("Collection request failed"));
    mocks.videosFind.mockResolvedValueOnce({
      items: [{ id: 101, title: "Recovered video", files: [], tags: [], performers: [] }],
      totalCount: 1,
    });
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "collection",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent videos",
        configuration: {
          source: "premade",
          mode: "videos",
          sortBy: "date",
          direction: "desc",
          header: "Recent videos",
        },
      },
    ]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Recent videos could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Recent videos" }));

    expect(await screen.findByText("Recovered video")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows and retries a failed Continue Watching widget", async () => {
    mocks.groupGet.mockRejectedValueOnce(new Error("Continue Watching request failed"));
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "continue",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Continue Watching",
        configuration: { source: "group", groupId: 3 },
      },
    ]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Continue Watching could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Continue Watching" }));

    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(mocks.groupGet).toHaveBeenCalledTimes(2);
    expect(mocks.groupGet).toHaveBeenLastCalledWith(3);
  });

  it("shows and retries a failed Continue Watching item request", async () => {
    mocks.groupItemsPage.mockRejectedValueOnce(new Error("Continue Watching items failed"));
    mocks.groupItemsPage.mockResolvedValueOnce({ items: [], totalCount: 0, page: 1, perPage: 12 });
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "continue",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Continue Watching",
        configuration: { source: "group", groupId: 3 },
      },
    ]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Continue Watching could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Continue Watching" }));

    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(mocks.groupItemsPage).toHaveBeenCalledTimes(2);
  });

  it("identifies a successfully empty saved filter only while editing", async () => {
    let resolveItems!: (value: { items: never[]; totalCount: number }) => void;
    mocks.savedFilterGet.mockRejectedValueOnce(new Error("Saved filter request failed"));
    mocks.savedFilterGet.mockResolvedValue({
      id: 5,
      name: "Warnings",
      mode: "videos",
      findFilter: "{}",
      objectFilter: "{}",
      uiOptions: "{}",
    });
    mocks.videosFind.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveItems = resolve;
        }),
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "saved",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Saved filter",
        configuration: { source: "saved", savedFilterId: 5 },
      },
    ]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Saved filter #5 could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Saved filter #5" }));

    await waitFor(() => expect(mocks.savedFilterGet).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.videosFind).toHaveBeenCalledOnce());
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Customize/ }));
    expect(screen.getByRole("heading", { name: "Warnings" })).toBeInTheDocument();
    expect(screen.queryByText("No matching entities.")).not.toBeInTheDocument();

    await act(async () => resolveItems({ items: [], totalCount: 0 }));
    expect(await screen.findByText("No matching entities.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Warnings" })).toBeInTheDocument();
    expect(screen.getByText("Saved filter", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Configure/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Remove/ })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("heading", { name: "Warnings" })).not.toBeInTheDocument();
    expect(screen.queryByText("No matching entities.")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Customize/ }));
    expect(await screen.findByText("No matching entities.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    expect(screen.queryByRole("heading", { name: "Warnings" })).not.toBeInTheDocument();
    expect(screen.queryByText("No matching entities.")).not.toBeInTheDocument();
  });

  it("removes a deleted saved filter widget without exposing the API response", async () => {
    mocks.savedFilterGet.mockRejectedValueOnce(
      new Error(
        'API Error 404: {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404}',
      ),
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "saved",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Review queue",
        configuration: { source: "saved", savedFilterId: 5 },
      },
    ]);
    mocks.update.mockImplementationOnce(
      async (_id: number, request: { name: string; widgets: NonNullable<typeof state.active>["widgets"] }) => {
        state.active = { ...state.active!, name: request.name, version: 2, widgets: request.widgets };
        return state.active;
      },
    );

    renderHome();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Review queue is unavailable");
    expect(alert).toHaveTextContent("The saved filter was deleted.");
    expect(alert).not.toHaveTextContent("API Error");
    expect(screen.queryByRole("button", { name: "Retry Review queue" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Remove Review queue" }));

    await waitFor(() =>
      expect(mocks.update).toHaveBeenCalledWith(1, {
        name: "Home",
        expectedVersion: 1,
        widgets: [],
      }),
    );
    await waitFor(() => expect(screen.queryByRole("button", { name: "Remove Review queue" })).not.toBeInTheDocument());
  });

  it("preserves concurrent dashboard changes when removing a deleted saved filter widget", async () => {
    mocks.savedFilterGet.mockRejectedValue(new Error("API Error 404: Not Found"));
    const missingWidget = {
      instanceId: "saved",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Review queue",
      configuration: { source: "saved", savedFilterId: 5 },
    };
    const concurrentWidget = {
      instanceId: "concurrent",
      owner: "cove.core",
      widgetKey: "collection",
      label: "Recently Added Videos",
      configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc" },
    };
    state.active = dashboard(1, "Home", true, [missingWidget]);
    mocks.update
      .mockImplementationOnce(async () => {
        state.active = { ...dashboard(1, "Renamed elsewhere", true, [missingWidget, concurrentWidget]), version: 2 };
        throw new Error("API Error 409: DASHBOARD_VERSION_CONFLICT");
      })
      .mockImplementationOnce(
        async (_id: number, request: { name: string; widgets: NonNullable<typeof state.active>["widgets"] }) => {
          state.active = { ...state.active!, name: request.name, version: 3, widgets: request.widgets };
          return state.active;
        },
      );

    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: "Remove Review queue" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledTimes(2));
    expect(mocks.update).toHaveBeenLastCalledWith(1, {
      name: "Renamed elsewhere",
      expectedVersion: 2,
      widgets: [concurrentWidget],
    });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Remove Review queue" })).not.toBeInTheDocument());
  });

  it("shows and retries a saved-filter item-query failure", async () => {
    mocks.videosFind.mockRejectedValueOnce(new Error("Saved collection request failed"));
    mocks.videosFind.mockResolvedValueOnce({
      items: [{ id: 102, title: "Recovered saved video", files: [], tags: [], performers: [] }],
      totalCount: 1,
    });
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "saved",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Saved filter",
        configuration: { source: "saved", savedFilterId: 5 },
      },
    ]);

    renderHome();

    expect(await screen.findByRole("alert")).toHaveTextContent("Filter 7 could not be loaded");
    fireEvent.click(screen.getByRole("button", { name: "Retry Filter 7" }));

    expect(await screen.findByText("Recovered saved video")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("disables dashboard draft controls while a save is pending", async () => {
    let resolveUpdate!: (value: NonNullable<typeof state.active>) => void;
    mocks.update.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveUpdate = resolve;
        }),
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "one",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent",
        configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent" },
      },
    ]);
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
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "one",
        owner: "cove.core",
        widgetKey: "collection",
        label: "A long dashboard widget label",
        configuration: {
          source: "premade",
          mode: "videos",
          sortBy: "date",
          direction: "desc",
          header: "A long dashboard widget label",
        },
      },
    ]);
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
    const enabledButtons = within(dialog)
      .getAllByRole("button")
      .filter((button) => !button.hasAttribute("disabled"));
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

  it("navigates and selects catalog widgets with the keyboard", async () => {
    const user = userEvent.setup();
    renderHome();
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    const dialog = screen.getByRole("dialog", { name: "Add Widget" });
    const search = within(dialog).getByRole("searchbox", { name: "Search widgets" });
    const firstWidget = within(dialog).getByRole("button", { name: /^Recently Released Videos/ });
    const secondWidget = within(dialog).getByRole("button", { name: /^Recently Added Videos/ });
    await waitFor(() => expect(search).toHaveFocus());

    await user.keyboard("{ArrowDown}");
    expect(firstWidget).toHaveFocus();
    await user.keyboard("{ArrowDown}");
    expect(secondWidget).toHaveFocus();
    await user.keyboard("{ArrowUp}");
    expect(firstWidget).toHaveFocus();
    await user.keyboard("{ArrowUp}");
    expect(search).toHaveFocus();

    await user.keyboard("{ArrowDown}{Enter}");
    expect(screen.queryByRole("dialog", { name: "Add Widget" })).not.toBeInTheDocument();
    expect(screen.getByText("Recently Released Videos", { selector: "span" })).toBeInTheDocument();
  });

  it("manages focus and Escape for the widget configuration dialog", async () => {
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "one",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent",
        configuration: { source: "premade", mode: "videos", sortBy: "date", direction: "desc", header: "Recent" },
      },
    ]);
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

  it("scrolls an appended widget only enough to reveal it", async () => {
    const scrollBy = vi.spyOn(window, "scrollBy").mockImplementation(() => undefined);
    const innerHeight = vi.spyOn(window, "innerHeight", "get").mockReturnValue(720);
    const getBoundingClientRect = vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (
      this: Element,
    ) {
      if (this.tagName === "HEADER") return { bottom: 124 } as DOMRect;
      if (this.tagName === "SECTION" && this.textContent?.includes("Recently Added Videos")) {
        return { top: 800, bottom: 1100, height: 300 } as DOMRect;
      }
      return { bottom: 0, top: 0, height: 0 } as DOMRect;
    });
    try {
      renderHome();

      fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
      fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
      fireEvent.click(screen.getByRole("button", { name: /^Recently Added Videos/ }));

      await waitFor(() => expect(scrollBy).toHaveBeenCalledWith({ top: 396, behavior: "smooth" }));
      expect(screen.queryByRole("heading", { name: "Add Widget" })).not.toBeInTheDocument();
      const addedWidget = screen.getByText("Recently Added Videos", { selector: "span" }).closest("section")!;
      expect(addedWidget).toContainElement(screen.getByText("Recently Added Videos", { selector: "span" }));
      expect(addedWidget.parentElement?.parentElement).toHaveClass("pb-6");
      expect(addedWidget.parentElement?.parentElement).not.toHaveClass("pb-[calc(100dvh-4px)]");
      expect(getWidgetRevealScrollDelta({ top: 800, bottom: 1700, height: 900 }, 124, 720)).toBe(672);
      expect(getWidgetRevealScrollDelta({ top: 300, bottom: 600, height: 300 }, 124, 720)).toBe(0);
    } finally {
      scrollBy.mockRestore();
      innerHeight.mockRestore();
      getBoundingClientRect.mockRestore();
    }
  });

  it("preserves configuration when an extension widget is unavailable", async () => {
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "pulse-1",
        owner: "example.extension",
        widgetKey: "pulse",
        label: "Library Pulse",
        configuration: { metrics: ["videos", "groups"] },
      },
    ]);

    renderHome();

    expect(await screen.findByText("Library Pulse")).toBeInTheDocument();
    expect(screen.getByText(/Configuration has been preserved/)).toBeInTheDocument();
  });

  // The dashboard can render before extension modules finish importing; their widgets are pending,
  // not unavailable.
  it("holds a placeholder for an extension widget while extension modules are still loading", async () => {
    state.extensionsLoaded = false;
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "pulse-1",
        owner: "example.extension",
        widgetKey: "pulse",
        label: "Library Pulse",
        configuration: {},
      },
    ]);

    renderHome();

    await screen.findByRole("button", { name: /Customize/ });
    expect(screen.getByText("Loading widget").closest('[role="status"][aria-busy="true"]')).not.toBeNull();
    expect(screen.queryByText(/Configuration has been preserved/)).toBeNull();
  });

  it("shows the dashboard's shape instead of a spinner while its layout loads", async () => {
    mocks.list.mockImplementation(() => new Promise(() => {}));

    const { container } = renderHome();

    expect(container.querySelector("[data-dashboard-skeleton]")).not.toBeNull();
    expect(screen.getByRole("status")).toHaveTextContent("Loading dashboard");
    expect(container.querySelectorAll("[data-row-skeleton]").length).toBeGreaterThan(0);
    expect(container.querySelector(".animate-spin")).toBeNull();
  });

  it("holds a row skeleton while a saved filter loads instead of collapsing the widget", async () => {
    mocks.savedFilterGet.mockImplementation(() => new Promise(() => {}));
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "saved",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Saved filter",
        configuration: { source: "saved", savedFilterId: 5 },
      },
    ]);

    const { container } = renderHome();

    await screen.findByRole("button", { name: /Customize/ });
    expect(container.querySelector("[data-row-skeleton]")).not.toBeNull();
    expect(container.querySelectorAll("[data-card-skeleton]").length).toBeGreaterThan(0);
  });

  it("sizes skeleton cards like the cards that replace them", async () => {
    let resolveItems!: (value: unknown) => void;
    const performersFind = vi.mocked((await import("../api/client")).performers.find);
    performersFind.mockImplementationOnce(
      () =>
        new Promise<any>((resolve) => {
          resolveItems = resolve;
        }),
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "performers",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Performers",
        configuration: { source: "premade", mode: "performers", sortBy: "name", direction: "asc", header: "People" },
      },
    ]);

    const { container } = renderHome();

    await screen.findByRole("heading", { name: "People" });
    const skeleton = container.querySelector("[data-card-skeleton]")!;
    expect(skeleton).not.toBeNull();
    const skeletonMedia = skeleton.firstElementChild!;
    const skeletonBody = skeletonMedia.nextElementSibling!;

    await act(async () => resolveItems({ items: [{ id: 8, name: "Avery", imagePath: "/p/8.jpg" }], totalCount: 1 }));
    const card = await screen.findByRole("link", { name: /Avery/ });
    const cardMedia = card.firstElementChild!;
    const cardBody = cardMedia.nextElementSibling!;

    const sizing = (element: Element) =>
      [...element.classList].filter((name) => /^(w-\[|aspect-|min-h-)/.test(name)).sort();
    expect(sizing(skeleton)).toEqual(["w-[160px]"]);
    expect(sizing(card)).toEqual(sizing(skeleton));
    expect(sizing(skeletonMedia)).toEqual(["aspect-[2/3]"]);
    expect(sizing(cardMedia)).toEqual(sizing(skeletonMedia));
    expect(sizing(cardBody)).toEqual(sizing(skeletonBody));
    expect(sizing(cardBody)).toHaveLength(1);
  });

  it("shows a card's no-image placeholder when its image fails to load", async () => {
    vi.mocked((await import("../api/client")).performers.find).mockResolvedValueOnce({
      items: [{ id: 8, name: "Avery", imagePath: "/p/8.jpg" }],
      totalCount: 1,
    } as any);
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "performers",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Performers",
        configuration: { source: "premade", mode: "performers", sortBy: "name", direction: "asc", header: "People" },
      },
    ]);

    renderHome();

    const card = await screen.findByRole("link", { name: /Avery/ });
    const image = card.querySelector("img")!;
    expect(image).not.toBeNull();
    fireEvent.error(image);
    expect(card.querySelector("img")).toBeNull();
    expect(card.querySelector("svg.lucide-user")).not.toBeNull();
  });

  it("fetches the first visible cards' images eagerly and fades them in once loaded", async () => {
    const videoItems = Array.from({ length: 8 }, (_, index) => ({
      id: index + 1,
      title: `Clip ${index + 1}`,
      files: [],
      tags: [],
      performers: [],
    }));
    mocks.videosFind.mockImplementation(async () => ({ items: videoItems, totalCount: videoItems.length }));
    const premade = (instanceId: string, header: string) => ({
      instanceId,
      owner: "cove.core",
      widgetKey: "collection",
      label: header,
      configuration: { source: "premade", mode: "videos", sortBy: "created_at", direction: "desc", header },
    });
    // An extension widget on top is not a row of images and must not take one of the eager slots.
    const extensionWidget = {
      instanceId: "ext",
      owner: "example.extension",
      widgetKey: "pulse",
      label: "Library Pulse",
      configuration: {},
    };
    state.active = dashboard(1, "Home", true, [
      extensionWidget,
      premade("a", "First"),
      premade("b", "Second"),
      premade("c", "Third"),
    ]);

    renderHome();

    const rowImages = async (header: string) => {
      const row = (await screen.findByRole("heading", { name: header })).closest(".recommendation-row")!;
      await within(row as HTMLElement).findAllByRole("link", { name: /Clip/ });
      return [...row.querySelectorAll("img")];
    };
    const first = await rowImages("First");
    expect(first.map((image) => image.getAttribute("loading"))).toEqual([
      ...Array(6).fill("eager"),
      ...Array(2).fill("lazy"),
    ]);
    expect((await rowImages("Second"))[0]).toHaveAttribute("loading", "eager");
    expect((await rowImages("Third")).every((image) => image.getAttribute("loading") === "lazy")).toBe(true);

    expect(first[0]).toHaveClass("opacity-0");
    fireEvent.load(first[0]);
    expect(first[0]).toHaveClass("opacity-100");
  });

  it("does not duplicate a single-instance extension widget", async () => {
    state.dashboardDefinitions = [
      {
        id: "singleton",
        label: "Singleton",
        extensionId: "example.extension",
        componentName: "Widget",
        allowMultiple: false,
        order: 1,
      },
    ];
    state.extensionComponents.Widget = () => <div>Singleton body</div>;
    state.active = dashboard(1, "Home", true, [
      { instanceId: "one", owner: "example.extension", widgetKey: "singleton", label: "Singleton", configuration: {} },
    ]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));

    expect(screen.getAllByRole("button", { name: /Duplicate/ })).toHaveLength(1);
  });

  it("rejects non-JSON configuration emitted by an extension editor", async () => {
    state.dashboardDefinitions = [
      {
        id: "configurable",
        label: "Configurable",
        extensionId: "example.extension",
        componentName: "Widget",
        editorComponentName: "Editor",
        allowMultiple: true,
        order: 1,
      },
    ];
    state.extensionComponents.Widget = () => <div>Configurable body</div>;
    state.extensionComponents.Editor = ({ onChange }: { onChange: (configuration: unknown) => void }) => (
      <button
        onClick={() => {
          const cyclic: Record<string, unknown> = {};
          cyclic.self = cyclic;
          onChange(cyclic);
        }}
      >
        Emit invalid configuration
      </button>
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "one",
        owner: "example.extension",
        widgetKey: "configurable",
        label: "Configurable",
        configuration: {},
      },
    ]);
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

  it("offers dynamic groups in the widget catalog and persists the selected group", async () => {
    mocks.groupsFind.mockImplementation(async (_filter?: unknown, extra?: { kind?: string }) =>
      extra?.kind === "dynamic"
        ? {
            items: [
              { id: 9, name: "Weekend queue", kind: "dynamic", querySourceKey: "filter" },
              { id: 3, name: "Continue Watching", kind: "dynamic", querySourceKey: "continue-watching" },
            ],
            totalCount: 2,
          }
        : { items: [], totalCount: 0 },
    );
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    const section = await screen.findByRole("region", { name: "Dynamic Groups" });
    expect(mocks.groupsFind).toHaveBeenCalledWith(expect.objectContaining({ perPage: 1000 }), { kind: "dynamic" });
    expect(within(section).getByRole("button", { name: /^Continue Watching/ })).toBeInTheDocument();
    fireEvent.click(within(section).getByRole("button", { name: /^Weekend queue/ }));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: [
          expect.objectContaining({
            owner: "cove.core",
            widgetKey: "collection",
            label: "Weekend queue",
            configuration: { source: "group", groupId: 9 },
          }),
        ],
      }),
    );
  });

  it("renders a dynamic group widget with its members and links View All to the group", async () => {
    mocks.groupItemsPage.mockImplementation(async () => ({
      items: [
        {
          id: -1,
          groupId: 9,
          orderIndex: 0,
          kind: "video",
          videoId: 41,
          hostType: "video",
          hostId: 41,
          title: "First clip",
        },
        {
          id: -2,
          groupId: 9,
          orderIndex: 1,
          kind: "image",
          imageId: 52,
          hostType: "image",
          hostId: 52,
          title: "A picture",
        },
        { id: -3, groupId: 9, orderIndex: 2, kind: "text", hostType: "text", hostId: 63, title: "A story" },
      ],
      totalCount: 3,
      page: 1,
      perPage: 25,
    }));
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "group",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Stale label",
        configuration: { source: "group", groupId: 9 },
      },
    ]);

    const { onNavigate } = renderHome();

    expect(await screen.findByRole("heading", { name: "Weekend queue" })).toBeInTheDocument();
    expect(mocks.groupItemsPage).toHaveBeenCalledWith(9, { page: 1, perPage: 25 });
    expect(await screen.findByRole("link", { name: /First clip/ })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /A picture/ })).toBeInTheDocument();
    expect(screen.queryByText("Resume")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("link", { name: /A story/ }));
    expect(onNavigate).toHaveBeenLastCalledWith({ page: "text", id: 63 });
    fireEvent.click(screen.getByRole("button", { name: "View All" }));
    expect(onNavigate).toHaveBeenLastCalledWith({ page: "group", id: 9 });
  });

  it("shows an empty dynamic group widget only while editing", async () => {
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "group",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Weekend queue",
        configuration: { source: "group", groupId: 9 },
      },
    ]);

    renderHome();

    await waitFor(() => expect(mocks.groupItemsPage).toHaveBeenCalledWith(9, { page: 1, perPage: 25 }));
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Weekend queue" })).not.toBeInTheDocument());
    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    expect(await screen.findByText("This group has no items.")).toBeInTheDocument();
  });

  it("offers to remove a dynamic group widget whose group was deleted", async () => {
    mocks.groupGet.mockRejectedValue(new Error("API Error 404: Not Found"));
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "group",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Weekend queue",
        configuration: { source: "group", groupId: 9 },
      },
    ]);
    mocks.update.mockImplementationOnce(
      async (_id: number, request: { name: string; widgets: NonNullable<typeof state.active>["widgets"] }) => {
        state.active = { ...state.active!, name: request.name, version: 2, widgets: request.widgets };
        return state.active;
      },
    );

    renderHome();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Weekend queue is unavailable");
    expect(alert).toHaveTextContent("The group was deleted or is not visible to you.");
    expect(alert).not.toHaveTextContent("API Error");

    fireEvent.click(screen.getByRole("button", { name: "Remove Weekend queue" }));

    await waitFor(() =>
      expect(mocks.update).toHaveBeenCalledWith(1, { name: "Home", expectedVersion: 1, widgets: [] }),
    );
  });

  it("persists a saved filter's name as its widget label", async () => {
    state.savedFilters = [
      { id: 5, name: "Review queue", mode: "videos", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
    ];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(await screen.findByRole("button", { name: /^Review queue/ }));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: [
          expect.objectContaining({
            label: "Review queue",
            configuration: { source: "saved", savedFilterId: 5 },
          }),
        ],
      }),
    );
  });

  it("caps a saved filter widget label at the dashboard API limit", async () => {
    const longName = `${"Long saved filter ".repeat(12)}Long saved filter`;
    const expectedLabel = `${longName.slice(0, 196).trimEnd()}… #5`;
    state.savedFilters = [
      { id: 5, name: longName, mode: "videos", findFilter: "{}", objectFilter: "{}", uiOptions: "{}" },
    ];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(await screen.findByRole("button", { name: new RegExp(`^${longName}`) }));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: [expect.objectContaining({ label: expectedLabel })],
      }),
    );
    expect(expectedLabel).toHaveLength(200);
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
    state.dashboardDefinitions = [
      {
        id: "curation-queue",
        label: "Curation Queue",
        description: "Review metadata warnings.",
        extensionId: "example.extension",
        componentName: "Widget",
        allowMultiple: true,
        order: 1,
      },
    ];
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
      items: Array.from({ length: 25 }, (_, index) => ({
        id: index + 1,
        title: `Video ${index + 1}`,
        files: [],
        tags: [],
        performers: [],
      })),
      totalCount: 25,
    });
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "collection",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent videos",
        configuration: {
          source: "premade",
          mode: "videos",
          sortBy: "date",
          direction: "desc",
          header: "Recent videos",
        },
      },
    ]);
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
    expect(screen.getByRole("button", { name: "Go to carousel page 14" }).firstElementChild).toHaveClass(
      "bg-foreground",
    );
    fireEvent.click(screen.getByRole("button", { name: "Go to carousel page 15" }));

    expect(scrollTo).toHaveBeenLastCalledWith({ left: 5310, behavior: "smooth" });
    expect(screen.getByRole("button", { name: "Go to carousel page 15" }).firstElementChild).toHaveClass(
      "bg-foreground",
    );
    expect(screen.getByRole("button", { name: "Previous Recent videos page" })).toHaveClass("focus:opacity-100");
    expect(screen.queryByRole("button", { name: "Next Recent videos page" })).not.toBeInTheDocument();
    expect(getCarouselPageDestinations(5700, 390)).toHaveLength(15);
    expect(getCarouselPageDestinations(5700, 390).at(-1)).toBe(5310);
    expect(getCarouselPageDestinations(781, 390)).toEqual([0, 390]);
  });

  it("blocks canvas catalog items until the dashboard is empty", async () => {
    state.dashboardDefinitions = [
      {
        id: "group-feed",
        label: "Group Feed",
        description: "Browse one group as a mixed feed.",
        extensionId: "example.extension",
        componentName: "GroupFeedWidget",
        allowMultiple: false,
        order: 1,
        supportedPresentations: ["canvas"],
        defaultPresentation: "canvas",
      },
    ];
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "flow",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent",
        configuration: {},
        presentation: "flow",
      },
    ]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));

    expect(screen.getByRole("button", { name: /Group Feed/ })).toBeDisabled();
    expect(screen.getByText(/Canvas widgets need an empty dashboard/)).toBeInTheDocument();
  });

  it("adds a dual-presentation Canvas-default widget as Flow on a populated dashboard", async () => {
    state.dashboardDefinitions = [
      {
        id: "adaptive",
        label: "Adaptive Widget",
        extensionId: "example.extension",
        componentName: "AdaptiveWidget",
        allowMultiple: true,
        order: 1,
        supportedPresentations: ["flow", "canvas"],
        defaultPresentation: "canvas",
      },
    ];
    state.extensionComponents.AdaptiveWidget = ({ presentation }: { presentation: string }) => (
      <div>Adaptive {presentation}</div>
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "flow",
        owner: "cove.core",
        widgetKey: "collection",
        label: "Recent",
        configuration: {},
        presentation: "flow",
      },
    ]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(screen.getByRole("button", { name: /Adaptive Widget/ }));

    expect(await screen.findByText("Adaptive flow")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: expect.arrayContaining([expect.objectContaining({ widgetKey: "adaptive", presentation: "flow" })]),
      }),
    );
  });

  it("adds a canvas-only contribution with its presentation and passes it to the widget", async () => {
    state.dashboardDefinitions = [
      {
        id: "group-feed",
        label: "Group Feed",
        extensionId: "example.extension",
        componentName: "GroupFeedWidget",
        allowMultiple: false,
        order: 1,
        supportedPresentations: ["canvas"],
        defaultPresentation: "canvas",
      },
    ];
    state.extensionComponents.GroupFeedWidget = ({ presentation }: { presentation: string }) => (
      <div>Rendered as {presentation}</div>
    );
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /Add Widget/ }));
    fireEvent.click(screen.getByRole("button", { name: /Group Feed/ }));

    expect(await screen.findByText("Rendered as canvas")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: [expect.objectContaining({ widgetKey: "group-feed", presentation: "canvas" })],
      }),
    );
  });

  it("lets users repair a saved presentation the contribution no longer supports", async () => {
    state.dashboardDefinitions = [
      {
        id: "adaptive",
        label: "Adaptive Widget",
        extensionId: "example.extension",
        componentName: "AdaptiveWidget",
        allowMultiple: true,
        order: 1,
        supportedPresentations: ["flow"],
        defaultPresentation: "flow",
      },
    ];
    state.extensionComponents.AdaptiveWidget = ({ presentation }: { presentation: string }) => (
      <div>Adaptive {presentation}</div>
    );
    state.active = dashboard(1, "Home", true, [
      {
        instanceId: "adaptive",
        owner: "example.extension",
        widgetKey: "adaptive",
        label: "Adaptive Widget",
        configuration: {},
        presentation: "canvas",
      },
    ]);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    const presentation = screen.getByRole("combobox", { name: "Presentation for Adaptive Widget" });
    expect(presentation).toHaveValue("unsupported");
    fireEvent.change(presentation, { target: { value: "flow" } });
    expect(await screen.findByText("Adaptive flow")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(mocks.update).toHaveBeenCalled());
    expect(mocks.update.mock.calls.at(-1)?.[1]).toEqual(
      expect.objectContaining({
        widgets: [expect.objectContaining({ widgetKey: "adaptive", presentation: "flow" })],
      }),
    );
  });

  // An extension that overrides a built-in page must not have the built-in page flash in first.
  it("holds a page an extension is still loading instead of rendering the built-in page", async () => {
    state.pagesAwaitingExtensions = ["home"];
    const { container } = render(
      <QueryClientProvider client={new QueryClient()}>
        <RouteRegistryProvider>
          <AppRoutes route={{ page: "home" }} navigate={vi.fn()} />
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(container.querySelector(".animate-spin")).not.toBeNull();
    expect(screen.queryByRole("button", { name: /Customize/ })).toBeNull();
    expect(mocks.list).not.toHaveBeenCalled();
  });

  it("creates another personal dashboard and opens it for editing", async () => {
    const prompt = vi.spyOn(window, "prompt");
    const created = dashboard(2, "New Dashboard");
    mocks.create.mockImplementation(async () => {
      state.dashboards = [summary(1, "Home", true), summary(2, "New Dashboard")];
      return created;
    });
    mocks.update.mockImplementation(async (_id: number, request: { name: string }) => {
      created.name = request.name;
      state.dashboards = [summary(1, "Home", true), summary(2, request.name)];
      return { ...created };
    });
    mocks.get.mockImplementation(async (id: number) => (id === created.id ? created : dashboard(1, "Home", true)));
    const onNavigate = vi.fn();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    function RoutedDashboardApp() {
      const [route, setRoute] = useState<Route>({ page: "home" });
      return (
        <>
          <button onClick={() => setRoute({ page: "home" })}>Test go home</button>
          <AppRoutes
            route={route}
            navigate={(nextRoute) => {
              onNavigate(nextRoute);
              setRoute(nextRoute);
            }}
          />
        </>
      );
    }
    render(
      <QueryClientProvider client={client}>
        <RouteRegistryProvider>
          <Suspense fallback={<div>Loading route</div>}>
            <RoutedDashboardApp />
          </Suspense>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: /New Dashboard/ }));

    await waitFor(() => expect(mocks.create).toHaveBeenCalledWith("New Dashboard"));
    expect(prompt).not.toHaveBeenCalled();
    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "dashboard", id: 2 }));
    expect(await screen.findByText("Editing Dashboard")).toBeInTheDocument();
    const nameInput = screen.getByRole<HTMLInputElement>("textbox", { name: "Dashboard name" });
    expect(nameInput).toHaveValue("New Dashboard");
    expect(nameInput).toHaveFocus();
    expect(nameInput.selectionStart).toBe(0);
    expect(nameInput.selectionEnd).toBe("New Dashboard".length);

    fireEvent.change(nameInput, { target: { value: "Discovery" } });
    fireEvent.keyDown(nameInput, { key: "Enter" });
    await waitFor(() => expect(mocks.update).toHaveBeenCalledWith(2, expect.objectContaining({ name: "Discovery" })));
    expect(screen.queryByText("Editing Dashboard")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Test go home" }));
    expect(await findCurrentDashboard("Home")).toBeInTheDocument();
    expect(screen.queryByText("Editing Dashboard")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("link", { name: "Discovery" }));
    expect(await findCurrentDashboard("Discovery")).toBeInTheDocument();
    expect(screen.queryByText("Editing Dashboard")).not.toBeInTheDocument();
  });

  it("gives a new dashboard an available default name", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "New Dashboard"), summary(3, "new dashboard 2")];
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /New Dashboard/ }));

    await waitFor(() => expect(mocks.create).toHaveBeenCalledWith("New Dashboard 3"));
  });

  it("refreshes to the fallback after deleting the default dashboard at the home URL", async () => {
    state.dashboards = [summary(1, "Home", true), summary(2, "Fallback")];
    vi.spyOn(window, "confirm").mockReturnValue(true);
    renderHome();

    fireEvent.click(await screen.findByRole("button", { name: /Customize/ }));
    fireEvent.click(screen.getByRole("button", { name: /^Delete$/ }));

    expect(await findCurrentDashboard("Fallback")).toBeInTheDocument();
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
