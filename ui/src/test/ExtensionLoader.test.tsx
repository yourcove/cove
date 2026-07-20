import { StrictMode, useState, type ComponentType, type FC, type ReactNode } from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExtensionManifest } from "../api/types";
import { ExtensionLoaderProvider, useExtensions } from "../extensions/ExtensionLoader";
import { ExtensionSlot, RouteRegistryProvider } from "../router/RouteRegistry";

const { getManifestMock } = vi.hoisted(() => ({
  getManifestMock: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/client")>();
  return {
    ...original,
    extensions: {
      ...original.extensions,
      getManifest: getManifestMock,
    },
  };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { troubleshootingModeEnabled: false } } }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: null,
    hasPermission: () => true,
  }),
}));

interface ExtensionBundleDescriptor {
  extensionId: string;
  version: string;
  jsBundleUrl: string;
  cssBundleUrl?: string;
}

interface ExtensionBundleModule {
  components: Record<string, FC<any>>;
  actionHandlers?: Record<string, (...args: any[]) => unknown>;
  onLoad?: () => void | Promise<void>;
  onUnload?: () => void | Promise<void>;
}

type BundleImporter = (url: string) => Promise<{ default: ExtensionBundleModule }>;

type ManifestWithBundles = ExtensionManifest & {
  extensionBundles: ExtensionBundleDescriptor[];
  componentOverrides: Array<{
    targetComponent: string;
    extensionId: string;
    componentName: string;
    priority: number;
  }>;
};

function buildManifest(
  bundle: ExtensionBundleDescriptor,
  componentSlot: { id: string; componentName: string },
  htmlSlot: { id: string; html: string },
): ManifestWithBundles {
  return {
    pages: [],
    slots: [
      {
        id: componentSlot.id,
        slot: "runtime-component-slot",
        extensionId: bundle.extensionId,
        contentType: "component",
        componentName: componentSlot.componentName,
        order: 10,
      },
      {
        id: htmlSlot.id,
        slot: "runtime-html-slot",
        extensionId: bundle.extensionId,
        contentType: "html",
        html: htmlSlot.html,
        order: 10,
      },
    ],
    tabs: [],
    features: [],
    themes: [],
    componentStyles: [],
    layoutStyles: [],
    settingsTabs: [],
    settingsPanels: [],
    pageOverrides: [],
    dialogOverrides: [],
    actions: [],
    tutorialTopics: [],
    listFilters: [],
    listSorts: [],
    frontendRuntimeVersion: "v1",
    extensionBundles: [bundle],
    componentOverrides: [],
  };
}

function RuntimeProbe() {
  const runtime = useExtensions();
  const resolveOwnedComponent = runtime.resolveComponent as unknown as (
    extensionId: string,
    componentName: string,
  ) => FC<any> | undefined;
  const AlphaShared = resolveOwnedComponent("ext.alpha", "SharedName");
  const BetaShared = resolveOwnedComponent("ext.beta", "SharedName");

  return (
    <>
      <div data-testid="runtime-loaded">{String(runtime.loaded)}</div>
      <button type="button" onClick={() => void runtime.refreshManifest()}>
        Refresh extensions
      </button>
      {AlphaShared ? <AlphaShared /> : null}
      {BetaShared ? <BetaShared /> : null}
      <ExtensionSlot slot="runtime-component-slot" context={{}} />
      <ExtensionSlot slot="runtime-html-slot" context={{}} />
    </>
  );
}

function OwnerIsolationProbe({ installedOwner }: { installedOwner: string }) {
  const runtime = useExtensions();
  const [, setProbeRevision] = useState(0);
  const Owned = runtime.resolveComponent(installedOwner, "SharedName");
  const Unrelated = runtime.resolveComponent("ext.unrelated", "SharedName");
  const ownedHandler = runtime.resolveActionHandler(installedOwner, "sharedHandler");
  const unrelatedHandler = runtime.resolveActionHandler("ext.unrelated", "sharedHandler");

  return (
    <>
      <div data-testid="runtime-loaded">{String(runtime.loaded)}</div>
      <div data-testid="owned-component">{String(Boolean(Owned))}</div>
      <div data-testid="unrelated-component">{String(Boolean(Unrelated))}</div>
      <div data-testid="owned-handler">{String(Boolean(ownedHandler))}</div>
      <div data-testid="unrelated-handler">{String(Boolean(unrelatedHandler))}</div>
      <button type="button" onClick={() => setProbeRevision((revision) => revision + 1)}>
        Recheck ownership
      </button>
      <button type="button" onClick={() => void runtime.refreshManifest()}>
        Refresh extensions
      </button>
    </>
  );
}

function renderRuntime(importBundle: BundleImporter) {
  const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
    children: ReactNode;
    importBundle: BundleImporter;
  }>;

  return render(
    <RouteRegistryProvider>
      <InjectableProvider importBundle={importBundle}>
        <RuntimeProbe />
      </InjectableProvider>
    </RouteRegistryProvider>,
  );
}

describe("ExtensionLoaderProvider reconciliation", () => {
  beforeEach(() => {
    getManifestMock.mockReset();
    window.localStorage.clear();
  });

  afterEach(() => {
    cleanup();
    document
      .querySelectorAll('[data-cove-extension-bundle="true"]')
      .forEach((element) => element.remove());
  });

  it("loads per-extension bundle descriptors with the injected importer and resolves components by owner", async () => {
    const alphaBundle = {
      extensionId: "ext.alpha",
      version: "1.2.3",
      jsBundleUrl: "/api/extensions/assets/ext.alpha/ui.mjs?v=alpha",
      cssBundleUrl: "/api/extensions/assets/ext.alpha/ui.css?v=alpha",
    };
    const alphaOnLoad = vi.fn();
    const importer = vi.fn<BundleImporter>().mockResolvedValue({
      default: {
        components: {
          SharedName: () => <div>Alpha owned component</div>,
          AlphaSlot: () => <div>Alpha component slot</div>,
        },
        onLoad: alphaOnLoad,
      },
    });
    getManifestMock.mockResolvedValue(
      buildManifest(
        alphaBundle,
        { id: "alpha-component", componentName: "AlphaSlot" },
        { id: "alpha-html", html: "<span>Alpha HTML slot</span>" },
      ),
    );

    renderRuntime(importer);

    await waitFor(() => expect(screen.getByTestId("runtime-loaded")).toHaveTextContent("true"));
    expect(importer).toHaveBeenCalledTimes(1);
    expect(importer).toHaveBeenCalledWith(alphaBundle.jsBundleUrl);
    expect(alphaOnLoad).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Alpha owned component")).toBeInTheDocument();
    expect(screen.getByText("Alpha component slot")).toBeInTheDocument();
    expect(screen.getByText("Alpha HTML slot")).toBeInTheDocument();
    const stylesheet = document.querySelector(
      'link[data-cove-extension-bundle="true"][data-extension-id="ext.alpha"]',
    );
    expect(stylesheet).toHaveAttribute("href", alphaBundle.cssBundleUrl);
  });

  it("does not treat an installed extension whose ID matches the legacy key as a global fallback", async () => {
    const installedOwner = "__cove_legacy_bundle__";
    const manifest = buildManifest(
      {
        extensionId: installedOwner,
        version: "1.0.0",
        jsBundleUrl: "/sentinel-owner.mjs?v=1",
      },
      { id: "sentinel-component", componentName: "SentinelSlot" },
      { id: "sentinel-html", html: "<span>Sentinel HTML slot</span>" },
    );
    manifest.slots = [];
    getManifestMock.mockResolvedValue(manifest);
    const importer = vi.fn<BundleImporter>().mockResolvedValue({
      default: {
        components: { SharedName: () => <div>Sentinel owned component</div> },
        actionHandlers: { sharedHandler: vi.fn() },
      },
    });
    const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
      children: ReactNode;
      importBundle: BundleImporter;
    }>;

    render(
      <RouteRegistryProvider>
        <InjectableProvider importBundle={importer}>
          <OwnerIsolationProbe installedOwner={installedOwner} />
        </InjectableProvider>
      </RouteRegistryProvider>,
    );

    await waitFor(() => expect(screen.getByTestId("runtime-loaded")).toHaveTextContent("true"));
    expect(screen.getByTestId("owned-component")).toHaveTextContent("true");
    expect(screen.getByTestId("owned-handler")).toHaveTextContent("true");
    expect(screen.getByTestId("unrelated-component")).toHaveTextContent("false");
    expect(screen.getByTestId("unrelated-handler")).toHaveTextContent("false");
  });

  it("keeps aggregate fallback isolated while transitioning to a same-named installed owner", async () => {
    const installedOwner = "__cove_legacy_bundle__";
    const legacyManifest = buildManifest(
      {
        extensionId: "unused-owned-descriptor",
        version: "1.0.0",
        jsBundleUrl: "/unused.mjs",
      },
      { id: "unused-component", componentName: "UnusedSlot" },
      { id: "unused-html", html: "<span>Unused HTML slot</span>" },
    );
    legacyManifest.extensionBundles = [];
    legacyManifest.slots = [];
    legacyManifest.jsBundleUrl = "/legacy-aggregate.mjs";

    const ownedManifest = buildManifest(
      {
        extensionId: installedOwner,
        version: "2.0.0",
        jsBundleUrl: "/same-named-owner.mjs?v=2",
      },
      { id: "owned-component", componentName: "OwnedSlot" },
      { id: "owned-html", html: "<span>Owned HTML slot</span>" },
    );
    ownedManifest.slots = [];

    let releaseOwnedLoad!: () => void;
    let markOwnedLoadStarted!: () => void;
    const ownedLoadStarted = new Promise<void>((resolve) => {
      markOwnedLoadStarted = resolve;
    });
    const ownedLoadCompleted = vi.fn();
    const importer = vi.fn<BundleImporter>(async (url) => ({
      default: {
        components: { SharedName: () => <div>{url}</div> },
        actionHandlers: { sharedHandler: vi.fn() },
        onLoad: url.includes("same-named-owner")
          ? () => {
              markOwnedLoadStarted();
              return new Promise<void>((resolve) => {
                releaseOwnedLoad = resolve;
              }).then(ownedLoadCompleted);
            }
          : undefined,
      },
    }));
    getManifestMock
      .mockResolvedValueOnce(legacyManifest)
      .mockResolvedValueOnce(ownedManifest);
    const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
      children: ReactNode;
      importBundle: BundleImporter;
    }>;

    render(
      <RouteRegistryProvider>
        <InjectableProvider importBundle={importer}>
          <OwnerIsolationProbe installedOwner={installedOwner} />
        </InjectableProvider>
      </RouteRegistryProvider>,
    );

    await waitFor(() => expect(screen.getByTestId("runtime-loaded")).toHaveTextContent("true"));
    expect(screen.getByTestId("unrelated-component")).toHaveTextContent("true");
    expect(screen.getByTestId("unrelated-handler")).toHaveTextContent("true");

    fireEvent.click(screen.getByRole("button", { name: "Refresh extensions" }));
    await ownedLoadStarted;
    fireEvent.click(screen.getByRole("button", { name: "Recheck ownership" }));

    expect(screen.getByTestId("owned-component")).toHaveTextContent("true");
    expect(screen.getByTestId("owned-handler")).toHaveTextContent("true");
    expect(screen.getByTestId("unrelated-component")).toHaveTextContent("false");
    expect(screen.getByTestId("unrelated-handler")).toHaveTextContent("false");

    releaseOwnedLoad();
    await waitFor(() => expect(ownedLoadCompleted).toHaveBeenCalledTimes(1));
  });

  it("restores the runtime after the development StrictMode effect cycle", async () => {
    const alphaBundle = {
      extensionId: "ext.alpha",
      version: "1.2.3",
      jsBundleUrl: "/api/extensions/assets/ext.alpha/ui.mjs?v=alpha",
      cssBundleUrl: "/api/extensions/assets/ext.alpha/ui.css?v=alpha",
    };
    getManifestMock.mockResolvedValue(
      buildManifest(
        alphaBundle,
        { id: "alpha-component", componentName: "AlphaSlot" },
        { id: "alpha-html", html: "<span>Alpha HTML slot</span>" },
      ),
    );
    const importer = vi.fn<BundleImporter>().mockResolvedValue({
      default: {
        components: {
          SharedName: () => <div>Alpha owned component</div>,
          AlphaSlot: () => <div>Alpha component slot</div>,
        },
      },
    });
    const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
      children: ReactNode;
      importBundle: BundleImporter;
    }>;

    render(
      <StrictMode>
        <RouteRegistryProvider>
          <InjectableProvider importBundle={importer}>
            <RuntimeProbe />
          </InjectableProvider>
        </RouteRegistryProvider>
      </StrictMode>,
    );

    await waitFor(() => expect(screen.getByTestId("runtime-loaded")).toHaveTextContent("true"));
    expect(screen.getByText("Alpha owned component")).toBeInTheDocument();
    expect(document.querySelector('link[data-extension-id="ext.alpha"]')).toHaveAttribute(
      "href",
      alphaBundle.cssBundleUrl,
    );
  });

  it("reconciles A to B on refresh and unloads the active bundle on provider unmount", async () => {
    const alphaManifest = buildManifest(
      {
        extensionId: "ext.alpha",
        version: "1.0.0",
        jsBundleUrl: "/alpha.mjs?v=1",
      },
      { id: "alpha-component", componentName: "AlphaSlot" },
      { id: "alpha-html", html: "<span>Alpha HTML slot</span>" },
    );
    const betaManifest = buildManifest(
      {
        extensionId: "ext.beta",
        version: "2.0.0",
        jsBundleUrl: "/beta.mjs?v=2",
      },
      { id: "beta-component", componentName: "BetaSlot" },
      { id: "beta-html", html: "<span>Beta HTML slot</span>" },
    );
    const alphaOnLoad = vi.fn();
    const alphaOnUnload = vi.fn();
    const betaOnLoad = vi.fn();
    const betaOnUnload = vi.fn();
    const modules: Record<string, { default: ExtensionBundleModule }> = {
      "/alpha.mjs?v=1": {
        default: {
          components: {
            SharedName: () => <div>Alpha owned component</div>,
            AlphaSlot: () => <div>Alpha component slot</div>,
          },
          onLoad: alphaOnLoad,
          onUnload: alphaOnUnload,
        },
      },
      "/beta.mjs?v=2": {
        default: {
          components: {
            SharedName: () => <div>Beta owned component</div>,
            BetaSlot: () => <div>Beta component slot</div>,
          },
          onLoad: betaOnLoad,
          onUnload: betaOnUnload,
        },
      },
    };
    const importer = vi.fn<BundleImporter>(async (url) => modules[url]);
    getManifestMock
      .mockResolvedValueOnce(alphaManifest)
      .mockResolvedValueOnce(betaManifest);

    const view = renderRuntime(importer);

    await waitFor(() => expect(alphaOnLoad).toHaveBeenCalledTimes(1));
    expect(screen.getByText("Alpha owned component")).toBeInTheDocument();
    expect(screen.getByText("Alpha component slot")).toBeInTheDocument();
    expect(screen.getByText("Alpha HTML slot")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Refresh extensions" }));

    await waitFor(() => expect(betaOnLoad).toHaveBeenCalledTimes(1));
    expect(alphaOnUnload).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Alpha owned component")).not.toBeInTheDocument();
    expect(screen.queryByText("Alpha component slot")).not.toBeInTheDocument();
    expect(screen.queryByText("Alpha HTML slot")).not.toBeInTheDocument();
    expect(screen.getByText("Beta owned component")).toBeInTheDocument();
    expect(screen.getByText("Beta component slot")).toBeInTheDocument();
    expect(screen.getByText("Beta HTML slot")).toBeInTheDocument();
    expect(importer.mock.calls.map(([url]) => url)).toEqual(["/alpha.mjs?v=1", "/beta.mjs?v=2"]);

    view.unmount();

    expect(alphaOnUnload).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(betaOnUnload).toHaveBeenCalledTimes(1));
  });

  it("does not let a delayed old-provider unload remove a newly mounted runtime", async () => {
    const alphaManifest = buildManifest(
      {
        extensionId: "ext.alpha",
        version: "1.0.0",
        jsBundleUrl: "/alpha.mjs?v=1",
        cssBundleUrl: "/alpha.css?v=1",
      },
      { id: "shared-component", componentName: "AlphaSlot" },
      { id: "shared-html", html: "<span>Alpha HTML slot</span>" },
    );
    const betaManifest = buildManifest(
      {
        extensionId: "ext.beta",
        version: "2.0.0",
        jsBundleUrl: "/beta.mjs?v=2",
        cssBundleUrl: "/beta.css?v=2",
      },
      { id: "shared-component", componentName: "BetaSlot" },
      { id: "shared-html", html: "<span>Beta HTML slot</span>" },
    );
    let releaseAlphaUnload!: () => void;
    let markAlphaUnloadStarted!: () => void;
    const alphaUnloadStarted = new Promise<void>((resolve) => {
      markAlphaUnloadStarted = resolve;
    });
    const alphaUnload = vi.fn(() => {
      markAlphaUnloadStarted();
      return new Promise<void>((resolve) => {
        releaseAlphaUnload = resolve;
      });
    });
    const modules: Record<string, { default: ExtensionBundleModule }> = {
      "/alpha.mjs?v=1": {
        default: {
          components: {
            SharedName: () => <div>Alpha owned component</div>,
            AlphaSlot: () => <div>Alpha component slot</div>,
          },
          onUnload: alphaUnload,
        },
      },
      "/beta.mjs?v=2": {
        default: {
          components: {
            SharedName: () => <div>Beta owned component</div>,
            BetaSlot: () => <div>Beta component slot</div>,
          },
        },
      },
    };
    const importer = vi.fn<BundleImporter>(async (url) => modules[url]);
    getManifestMock
      .mockResolvedValueOnce(alphaManifest)
      .mockResolvedValueOnce(betaManifest);
    const InjectableProvider = ExtensionLoaderProvider as ComponentType<{
      children: ReactNode;
      importBundle: BundleImporter;
    }>;
    const tree = (runtimeKey: string) => (
      <RouteRegistryProvider>
        <InjectableProvider key={runtimeKey} importBundle={importer}>
          <RuntimeProbe />
        </InjectableProvider>
      </RouteRegistryProvider>
    );
    const view = render(tree("alpha"));
    await waitFor(() => expect(screen.getByText("Alpha component slot")).toBeInTheDocument());

    view.rerender(tree("beta"));
    await alphaUnloadStarted;
    await waitFor(() => expect(screen.getByText("Beta component slot")).toBeInTheDocument());
    expect(document.querySelector('link[data-extension-id="ext.beta"]')).toHaveAttribute("href", "/beta.css?v=2");

    releaseAlphaUnload();
    await waitFor(() => expect(document.querySelector('link[data-extension-id="ext.alpha"]')).not.toBeInTheDocument());

    expect(screen.getByText("Beta component slot")).toBeInTheDocument();
    expect(screen.getByText("Beta HTML slot")).toBeInTheDocument();
    expect(document.querySelector('link[data-extension-id="ext.beta"]')).toHaveAttribute("href", "/beta.css?v=2");
  });

  it("ignores an older manifest response that arrives after a refresh", async () => {
    const alphaManifest = buildManifest(
      {
        extensionId: "ext.alpha",
        version: "1.0.0",
        jsBundleUrl: "/alpha.mjs?v=1",
      },
      { id: "alpha-component", componentName: "AlphaSlot" },
      { id: "alpha-html", html: "<span>Alpha HTML slot</span>" },
    );
    const betaManifest = buildManifest(
      {
        extensionId: "ext.beta",
        version: "2.0.0",
        jsBundleUrl: "/beta.mjs?v=2",
      },
      { id: "beta-component", componentName: "BetaSlot" },
      { id: "beta-html", html: "<span>Beta HTML slot</span>" },
    );
    let resolveInitialManifest!: (manifest: ExtensionManifest) => void;
    getManifestMock
      .mockImplementationOnce(() => new Promise<ExtensionManifest>((resolve) => {
        resolveInitialManifest = resolve;
      }))
      .mockResolvedValueOnce(betaManifest);

    const importer = vi.fn<BundleImporter>(async (url) => {
      const components: Record<string, FC<any>> = url.includes("beta")
        ? {
            SharedName: () => <div>Beta owned component</div>,
            BetaSlot: () => <div>Beta component slot</div>,
          }
        : {
            SharedName: () => <div>Alpha owned component</div>,
            AlphaSlot: () => <div>Alpha component slot</div>,
          };
      return { default: { components } };
    });

    renderRuntime(importer);
    fireEvent.click(screen.getByRole("button", { name: "Refresh extensions" }));

    await waitFor(() => expect(screen.getByText("Beta owned component")).toBeInTheDocument());
    resolveInitialManifest(alphaManifest);
    await waitFor(() => expect(getManifestMock).toHaveBeenCalledTimes(2));

    expect(screen.queryByText("Alpha owned component")).not.toBeInTheDocument();
    expect(screen.getByText("Beta owned component")).toBeInTheDocument();
    expect(importer.mock.calls.map(([url]) => url)).toEqual(["/beta.mjs?v=2"]);
  });

  it("does not commit a slow stale import when the newer refresh fails", async () => {
    const alphaManifest = buildManifest(
      {
        extensionId: "ext.alpha",
        version: "1.0.0",
        jsBundleUrl: "/alpha.mjs?v=1",
        cssBundleUrl: "/alpha.css?v=1",
      },
      { id: "alpha-component", componentName: "AlphaSlot" },
      { id: "alpha-html", html: "<span>Alpha HTML slot</span>" },
    );
    const failedRefreshManifest = buildManifest(
      {
        extensionId: "ext.beta",
        version: "2.0.0",
        jsBundleUrl: "/beta.mjs?v=2",
        cssBundleUrl: "/beta.css?v=2",
      },
      { id: "beta-component", componentName: "BetaSlot" },
      { id: "beta-html", html: "<span>Beta HTML slot</span>" },
    );
    getManifestMock
      .mockResolvedValueOnce(alphaManifest)
      .mockResolvedValueOnce(failedRefreshManifest);
    let resolveAlphaImport!: (module: { default: ExtensionBundleModule }) => void;
    const alphaImport = new Promise<{ default: ExtensionBundleModule }>((resolve) => {
      resolveAlphaImport = resolve;
    });
    const importer = vi.fn<BundleImporter>(async (url) => {
      if (url === "/alpha.mjs?v=1") return alphaImport;
      throw new Error("newer bundle import failed");
    });

    renderRuntime(importer);
    await waitFor(() => expect(importer).toHaveBeenCalledWith("/alpha.mjs?v=1"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh extensions" }));
    resolveAlphaImport({
      default: {
        components: {
          SharedName: () => <div>Alpha owned component</div>,
          AlphaSlot: () => <div>Alpha component slot</div>,
        },
      },
    });
    await waitFor(() => expect(importer).toHaveBeenCalledWith("/beta.mjs?v=2"));

    expect(screen.queryByText("Alpha owned component")).not.toBeInTheDocument();
    expect(screen.queryByText("Alpha component slot")).not.toBeInTheDocument();
    expect(document.querySelector('link[data-extension-id="ext.alpha"]')).not.toBeInTheDocument();
    expect(document.querySelector('link[data-extension-id="ext.beta"]')).not.toBeInTheDocument();
  });
});
