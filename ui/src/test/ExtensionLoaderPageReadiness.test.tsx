import type { ComponentType, ReactNode } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExtensionManifest } from "../api/types";
import { ExtensionLoaderProvider, useExtensions } from "../extensions/ExtensionLoader";
import { RouteRegistryProvider } from "../router/RouteRegistry";

// The shell renders while extension modules import. These pin, against the real loader, which pages
// it holds meanwhile and that nothing holds them for good.

const { getManifestMock } = vi.hoisted(() => ({ getManifestMock: vi.fn() }));

vi.mock("../api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/client")>();
  return { ...original, extensions: { ...original.extensions, getManifest: getManifestMock } };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { troubleshootingModeEnabled: false } } }),
}));

const authMock = vi.hoisted(() => ({ user: { id: "7", kind: "user" }, hasPermission: () => true }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => authMock }));

const PAGE_OVERRIDE_CACHE_KEY = "cove-extension-page-overrides";

type BundleImporter = (url: string) => Promise<unknown>;

function buildManifest({
  bundles = [],
  pages = [],
  overrides = [],
}: {
  bundles?: Array<{ extensionId: string; jsBundleUrl: string }>;
  pages?: Array<{ extensionId: string; route: string }>;
  overrides?: Array<{ extensionId: string; targetPage: string }>;
}): ExtensionManifest {
  return {
    extensionBundles: bundles.map((bundle) => ({ ...bundle, version: "1.0.0" })),
    pages: pages.map((page) => ({ ...page, label: page.route, componentName: "Page", showInNav: false, navOrder: 0 })),
    slots: [],
    tabs: [],
    features: [],
    themes: [],
    componentStyles: [],
    layoutStyles: [],
    settingsTabs: [],
    settingsPanels: [],
    componentOverrides: [],
    pageOverrides: overrides.map((override) => ({ ...override, componentName: "Page", priority: 0 })),
    dialogOverrides: [],
    actions: [],
    listFilters: [],
    listSorts: [],
  };
}

function ReadinessProbe() {
  const runtime = useExtensions();
  return (
    <>
      <div data-testid="loaded">{String(runtime.loaded)}</div>
      <div data-testid="manifest">{String(runtime.manifest !== null)}</div>
      <div data-testid="settling">{String(runtime.extensionsSettling)}</div>
      <div data-testid="ext-page">{String(runtime.isPageAwaitingExtensions("ext-page", false))}</div>
      <div data-testid="videos">{String(runtime.isPageAwaitingExtensions("videos", true))}</div>
      <div data-testid="tags">{String(runtime.isPageAwaitingExtensions("tags", true))}</div>
      <button type="button" onClick={() => void runtime.refreshManifest()}>
        Refresh
      </button>
    </>
  );
}

function renderLoader(importBundle: BundleImporter) {
  const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
    children: ReactNode;
    importBundle: BundleImporter;
  }>;
  return render(
    <RouteRegistryProvider>
      <InjectableProvider importBundle={importBundle}>
        <ReadinessProbe />
      </InjectableProvider>
    </RouteRegistryProvider>,
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const pageModule = { default: { components: { Page: () => null } } };
const probe = (id: string) => screen.getByTestId(id).textContent;

describe("ExtensionLoaderProvider page readiness", () => {
  beforeEach(() => {
    getManifestMock.mockReset();
    localStorage.clear();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("holds a page override while its module imports, and releases it once the import resolves", async () => {
    getManifestMock.mockResolvedValue(
      buildManifest({
        bundles: [{ extensionId: "ext.a", jsBundleUrl: "/a.mjs" }],
        overrides: [{ extensionId: "ext.a", targetPage: "videos" }],
      }),
    );
    const bundle = deferred<unknown>();
    renderLoader(() => bundle.promise);

    await waitFor(() => expect(getManifestMock).toHaveBeenCalled());
    await waitFor(() => expect(probe("videos")).toBe("true"));
    expect(probe("tags")).toBe("false");

    await act(async () => bundle.resolve(pageModule));

    await waitFor(() => expect(probe("loaded")).toBe("true"));
    expect(probe("videos")).toBe("false");
    expect(JSON.parse(localStorage.getItem(PAGE_OVERRIDE_CACHE_KEY) ?? "null")).toEqual({ u: "7", pages: ["videos"] });
  });

  it("releases a held page override when its module fails to import", async () => {
    getManifestMock.mockResolvedValue(
      buildManifest({
        bundles: [{ extensionId: "ext.a", jsBundleUrl: "/a.mjs" }],
        overrides: [{ extensionId: "ext.a", targetPage: "videos" }],
      }),
    );
    const bundle = deferred<unknown>();
    renderLoader(() => bundle.promise);

    await waitFor(() => expect(probe("videos")).toBe("true"));
    await act(async () => bundle.reject(new Error("bundle failed")));

    await waitFor(() => expect(probe("loaded")).toBe("true"));
    expect(probe("videos")).toBe("false");
  });

  it("before the manifest arrives, holds extension routes and last load's overrides but not other built-in pages", async () => {
    localStorage.setItem(PAGE_OVERRIDE_CACHE_KEY, JSON.stringify({ u: "7", pages: ["tags"] }));
    getManifestMock.mockReturnValue(new Promise(() => {}));
    renderLoader(async () => pageModule);

    await waitFor(() => expect(getManifestMock).toHaveBeenCalled());
    expect(probe("ext-page")).toBe("true");
    expect(probe("tags")).toBe("true");
    expect(probe("videos")).toBe("false");
  });

  it("ignores overrides cached for another user", async () => {
    localStorage.setItem(PAGE_OVERRIDE_CACHE_KEY, JSON.stringify({ u: "8", pages: ["tags"] }));
    getManifestMock.mockReturnValue(new Promise(() => {}));
    renderLoader(async () => pageModule);

    await waitFor(() => expect(getManifestMock).toHaveBeenCalled());
    expect(probe("tags")).toBe("false");
  });

  it("stops holding every extension page once a hung module overruns the settle timeout", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    getManifestMock.mockResolvedValue(
      buildManifest({
        bundles: [
          { extensionId: "ext.hung", jsBundleUrl: "/hung.mjs" },
          { extensionId: "ext.ok", jsBundleUrl: "/ok.mjs" },
        ],
        pages: [{ extensionId: "ext.ok", route: "ext-page" }],
      }),
    );
    renderLoader((url) => (url === "/hung.mjs" ? new Promise(() => {}) : Promise.resolve(pageModule)));

    await act(async () => {});
    expect(probe("ext-page")).toBe("true");
    expect(probe("settling")).toBe("true");

    await act(async () => vi.advanceTimersByTime(4000));

    expect(probe("ext-page")).toBe("false");
    expect(probe("settling")).toBe("false");
    expect(probe("loaded")).toBe("false");
  });

  it("does not report loaded when the first load is overtaken by a refresh that has not landed", async () => {
    const initial = deferred<ExtensionManifest>();
    const refresh = deferred<ExtensionManifest>();
    getManifestMock.mockReturnValueOnce(initial.promise).mockReturnValueOnce(refresh.promise);
    renderLoader(async () => pageModule);

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await act(async () =>
      initial.resolve(buildManifest({ overrides: [{ extensionId: "ext.a", targetPage: "tags" }] })),
    );

    expect(probe("loaded")).toBe("false");
    expect(probe("manifest")).toBe("false");

    await act(async () =>
      refresh.resolve(buildManifest({ overrides: [{ extensionId: "ext.b", targetPage: "videos" }] })),
    );

    await waitFor(() => expect(probe("loaded")).toBe("true"));
    expect(probe("manifest")).toBe("true");
  });

  it("does not report loaded when the overtaken first load fails before the refresh lands", async () => {
    const initial = deferred<ExtensionManifest>();
    const refresh = deferred<ExtensionManifest>();
    getManifestMock.mockReturnValueOnce(initial.promise).mockReturnValueOnce(refresh.promise);
    renderLoader(async () => pageModule);

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await act(async () => initial.reject(new Error("API Error 500: manifest unavailable")));

    expect(probe("loaded")).toBe("false");

    await act(async () =>
      refresh.resolve(buildManifest({ overrides: [{ extensionId: "ext.b", targetPage: "videos" }] })),
    );

    await waitFor(() => expect(probe("loaded")).toBe("true"));
    expect(probe("manifest")).toBe("true");
  });

  it("ignores an older manifest that lands after a newer refresh", async () => {
    const initial = deferred<ExtensionManifest>();
    getManifestMock
      .mockReturnValueOnce(initial.promise)
      .mockResolvedValueOnce(buildManifest({ overrides: [{ extensionId: "ext.b", targetPage: "videos" }] }));
    const hung = vi.fn(() => new Promise(() => {}));
    renderLoader(hung);

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(probe("loaded")).toBe("true"));

    await act(async () =>
      initial.resolve(
        buildManifest({
          bundles: [{ extensionId: "ext.a", jsBundleUrl: "/a.mjs" }],
          overrides: [{ extensionId: "ext.a", targetPage: "tags" }],
        }),
      ),
    );

    expect(probe("loaded")).toBe("true");
    expect(probe("tags")).toBe("false");
    expect(hung).not.toHaveBeenCalled();
  });
});
