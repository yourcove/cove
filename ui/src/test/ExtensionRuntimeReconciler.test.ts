import { describe, expect, it, vi } from "vitest";
import { createExtensionRuntimeReconciler } from "../extensions/ExtensionRuntimeReconciler";

type TestComponent = () => null;
type TestActionHandler = () => string;

interface TestBundleModule {
  default: {
    components?: Record<string, TestComponent>;
    actionHandlers?: Record<string, TestActionHandler>;
    onLoad?: () => void | Promise<void>;
    onUnload?: () => void;
  };
}

interface TestRegistration {
  components: Record<string, TestComponent>;
  actionHandlers: Record<string, TestActionHandler>;
}

function descriptor(extensionId: string, version: number) {
  return {
    extensionId,
    version: String(version),
    jsBundleUrl: `/api/extensions/assets/${extensionId}/ui.mjs?v=${version}`,
  };
}

function createRegistrationAdapter(events: string[]) {
  const components = new Map<string, TestComponent>();
  const actionHandlers = new Map<string, TestActionHandler>();

  return {
    components,
    actionHandlers,
    adapter: {
      register(extensionId: string, registration: TestRegistration) {
        events.push(`register:${extensionId}`);

        for (const [name, component] of Object.entries(registration.components)) {
          components.set(`${extensionId}:${name}`, component);
        }
        for (const [name, handler] of Object.entries(registration.actionHandlers)) {
          actionHandlers.set(`${extensionId}:${name}`, handler);
        }

        let unregistered = false;
        return () => {
          if (unregistered) return;
          unregistered = true;
          events.push(`unregister:${extensionId}`);

          for (const name of Object.keys(registration.components)) {
            components.delete(`${extensionId}:${name}`);
          }
          for (const name of Object.keys(registration.actionHandlers)) {
            actionHandlers.delete(`${extensionId}:${name}`);
          }
        };
      },
    },
  };
}

function createBundleImporter(bundles: Map<string, TestBundleModule>, events: string[]) {
  return vi.fn(async (url: string) => {
    events.push(`import:${url}`);
    const bundle = bundles.get(url);
    if (!bundle) throw new Error(`Unexpected bundle URL: ${url}`);
    return bundle;
  });
}

describe("ExtensionRuntimeReconciler", () => {
  it("loads and registers each extension independently before calling onLoad", async () => {
    const events: string[] = [];
    const alpha = descriptor("alpha", 1);
    const beta = descriptor("beta", 1);
    const AlphaPanel = () => null;
    const BetaPanel = () => null;
    const alphaAction = () => "alpha";
    const betaAction = () => "beta";
    const bundles = new Map<string, TestBundleModule>([
      [
        alpha.jsBundleUrl,
        {
          default: {
            components: { Panel: AlphaPanel },
            actionHandlers: { run: alphaAction },
            onLoad: () => {
              events.push("load:alpha");
            },
          },
        },
      ],
      [
        beta.jsBundleUrl,
        {
          default: {
            components: { Panel: BetaPanel },
            actionHandlers: { run: betaAction },
            onLoad: () => {
              events.push("load:beta");
            },
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const importBundle = createBundleImporter(bundles, events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle,
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alpha, beta]);

    expect(importBundle).toHaveBeenCalledTimes(2);
    expect(registrations.components.get("alpha:Panel")).toBe(AlphaPanel);
    expect(registrations.components.get("beta:Panel")).toBe(BetaPanel);
    expect(registrations.actionHandlers.get("alpha:run")).toBe(alphaAction);
    expect(registrations.actionHandlers.get("beta:run")).toBe(betaAction);
    expect(events.indexOf("register:alpha")).toBeLessThan(events.indexOf("load:alpha"));
    expect(events.indexOf("register:beta")).toBeLessThan(events.indexOf("load:beta"));
  });

  it("does not reload or rerun lifecycle hooks for an unchanged descriptor", async () => {
    const events: string[] = [];
    const alpha = descriptor("alpha", 1);
    const onLoad = vi.fn();
    const onUnload = vi.fn();
    const bundles = new Map<string, TestBundleModule>([
      [alpha.jsBundleUrl, { default: { components: { Panel: () => null }, onLoad, onUnload } }],
    ]);
    const registrations = createRegistrationAdapter(events);
    const importBundle = createBundleImporter(bundles, events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle,
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alpha]);
    await reconciler.reconcile([{ ...alpha }]);

    expect(importBundle).toHaveBeenCalledTimes(1);
    expect(onLoad).toHaveBeenCalledTimes(1);
    expect(onUnload).not.toHaveBeenCalled();
    expect(events.filter((event) => event === "register:alpha")).toHaveLength(1);
  });

  it("replaces a versioned bundle and unloads the old version before loading the new one", async () => {
    const events: string[] = [];
    const alphaV1 = descriptor("alpha", 1);
    const alphaV2 = descriptor("alpha", 2);
    const PanelV1 = () => null;
    const PanelV2 = () => null;
    const bundles = new Map<string, TestBundleModule>([
      [
        alphaV1.jsBundleUrl,
        {
          default: {
            components: { Panel: PanelV1 },
            onLoad: () => {
              events.push("load:alpha:v1");
            },
            onUnload: () => {
              events.push("unload:alpha:v1");
            },
          },
        },
      ],
      [
        alphaV2.jsBundleUrl,
        {
          default: {
            components: { Panel: PanelV2 },
            onLoad: () => {
              events.push("load:alpha:v2");
            },
            onUnload: () => {
              events.push("unload:alpha:v2");
            },
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const importBundle = createBundleImporter(bundles, events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle,
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alphaV1]);
    events.length = 0;
    await reconciler.reconcile([alphaV2]);

    expect(importBundle).toHaveBeenCalledTimes(2);
    expect(registrations.components.get("alpha:Panel")).toBe(PanelV2);
    expect(events).toContain("unload:alpha:v1");
    expect(events.indexOf("unload:alpha:v1")).toBeLessThan(events.indexOf("unregister:alpha"));
    expect(events.indexOf("unload:alpha:v1")).toBeLessThan(events.indexOf("load:alpha:v2"));
    expect(events.indexOf("unregister:alpha")).toBeLessThan(events.indexOf("register:alpha"));
  });

  it("reloads when the declared version changes even if the asset URL is unchanged", async () => {
    const events: string[] = [];
    const sharedUrl = "/api/extensions/assets/alpha/ui.mjs?v=preserved-timestamp";
    const PanelV1 = () => null;
    const PanelV2 = () => null;
    const importBundle = vi
      .fn()
      .mockResolvedValueOnce({ default: { components: { Panel: PanelV1 } } })
      .mockResolvedValueOnce({ default: { components: { Panel: PanelV2 } } });
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle,
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([{ extensionId: "alpha", version: "1.0.0", jsBundleUrl: sharedUrl }]);
    await reconciler.reconcile([{ extensionId: "alpha", version: "2.0.0", jsBundleUrl: sharedUrl }]);

    expect(importBundle).toHaveBeenCalledTimes(2);
    expect(registrations.components.get("alpha:Panel")).toBe(PanelV2);
  });

  it("rolls back earlier changes when a later extension fails to load", async () => {
    const events: string[] = [];
    const alphaV1 = descriptor("alpha", 1);
    const alphaV2 = descriptor("alpha", 2);
    const broken = descriptor("broken", 1);
    const PanelV1 = () => null;
    const PanelV2 = () => null;
    const alphaV1Load = vi.fn();
    const alphaV1Unload = vi.fn();
    const alphaV2Unload = vi.fn();
    const brokenUnload = vi.fn();
    const bundles = new Map<string, TestBundleModule>([
      [
        alphaV1.jsBundleUrl,
        {
          default: {
            components: { Panel: PanelV1 },
            onLoad: alphaV1Load,
            onUnload: alphaV1Unload,
          },
        },
      ],
      [
        alphaV2.jsBundleUrl,
        {
          default: {
            components: { Panel: PanelV2 },
            onUnload: alphaV2Unload,
          },
        },
      ],
      [
        broken.jsBundleUrl,
        {
          default: {
            components: { Panel: () => null },
            onLoad: () => {
              throw new Error("broken onLoad");
            },
            onUnload: brokenUnload,
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle: createBundleImporter(bundles, events),
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alphaV1]);
    await expect(reconciler.reconcile([alphaV2, broken])).rejects.toThrow("broken onLoad");

    expect(registrations.components.get("alpha:Panel")).toBe(PanelV1);
    expect(registrations.components.has("broken:Panel")).toBe(false);
    expect(alphaV1Load).toHaveBeenCalledTimes(2);
    expect(alphaV1Unload).toHaveBeenCalledTimes(1);
    expect(alphaV2Unload).toHaveBeenCalledTimes(1);
    expect(brokenUnload).toHaveBeenCalledTimes(1);
  });

  it("does not commit a staged bundle after its manifest request becomes stale", async () => {
    const events: string[] = [];
    const alpha = descriptor("alpha", 1);
    const beta = descriptor("beta", 1);
    const AlphaPanel = () => null;
    let resolveBetaImport!: (module: TestBundleModule) => void;
    const betaImport = new Promise<TestBundleModule>((resolve) => {
      resolveBetaImport = resolve;
    });
    const importBundle = vi.fn(async (url: string) => {
      if (url === alpha.jsBundleUrl) {
        return { default: { components: { Panel: AlphaPanel } } };
      }
      return betaImport;
    });
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle,
      registrations: registrations.adapter,
    });
    await reconciler.reconcile([alpha]);

    let current = true;
    const pendingReconcile = reconciler.reconcile([beta], { isCurrent: () => current });
    current = false;
    resolveBetaImport({ default: { components: { Panel: () => null } } });

    await expect(pendingReconcile).resolves.toBe(false);
    expect(registrations.components.get("alpha:Panel")).toBe(AlphaPanel);
    expect(registrations.components.has("beta:Panel")).toBe(false);
  });

  it("removes only missing extensions, cleans their exports, and calls onUnload once", async () => {
    const events: string[] = [];
    const alpha = descriptor("alpha", 1);
    const beta = descriptor("beta", 1);
    const alphaUnload = vi.fn();
    const betaUnload = vi.fn();
    const bundles = new Map<string, TestBundleModule>([
      [
        alpha.jsBundleUrl,
        {
          default: {
            components: { Panel: () => null },
            actionHandlers: { run: () => "alpha" },
            onUnload: alphaUnload,
          },
        },
      ],
      [
        beta.jsBundleUrl,
        {
          default: {
            components: { Panel: () => null },
            actionHandlers: { run: () => "beta" },
            onUnload: betaUnload,
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle: createBundleImporter(bundles, events),
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alpha, beta]);
    await reconciler.reconcile([beta]);
    await reconciler.reconcile([beta]);

    expect(alphaUnload).toHaveBeenCalledTimes(1);
    expect(betaUnload).not.toHaveBeenCalled();
    expect(registrations.components.has("alpha:Panel")).toBe(false);
    expect(registrations.actionHandlers.has("alpha:run")).toBe(false);
    expect(registrations.components.has("beta:Panel")).toBe(true);
    expect(registrations.actionHandlers.has("beta:run")).toBe(true);
  });

  it("rolls back registrations and lifecycle state when onLoad fails", async () => {
    const events: string[] = [];
    const broken = descriptor("broken", 1);
    const onUnload = vi.fn(() => {
      events.push("unload:broken");
    });
    const bundles = new Map<string, TestBundleModule>([
      [
        broken.jsBundleUrl,
        {
          default: {
            components: { Panel: () => null },
            actionHandlers: { run: () => "broken" },
            onLoad: async () => {
              events.push("load:broken");
              throw new Error("onLoad failed");
            },
            onUnload,
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle: createBundleImporter(bundles, events),
      registrations: registrations.adapter,
    });

    await expect(reconciler.reconcile([broken])).rejects.toThrow("onLoad failed");

    expect(onUnload).toHaveBeenCalledTimes(1);
    expect(events.indexOf("load:broken")).toBeLessThan(events.indexOf("unload:broken"));
    expect(events.indexOf("unload:broken")).toBeLessThan(events.indexOf("unregister:broken"));
    expect(registrations.components.size).toBe(0);
    expect(registrations.actionHandlers.size).toBe(0);

    await reconciler.dispose();
    expect(onUnload).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["an empty target set (troubleshooting mode)", "empty"],
    ["dispose", "dispose"],
  ])("unloads active extensions once through %s", async (_label, mode) => {
    const events: string[] = [];
    const alpha = descriptor("alpha", 1);
    const onUnload = vi.fn();
    const bundles = new Map<string, TestBundleModule>([
      [
        alpha.jsBundleUrl,
        {
          default: {
            components: { Panel: () => null },
            actionHandlers: { run: () => "alpha" },
            onUnload,
          },
        },
      ],
    ]);
    const registrations = createRegistrationAdapter(events);
    const reconciler = createExtensionRuntimeReconciler({
      importBundle: createBundleImporter(bundles, events),
      registrations: registrations.adapter,
    });

    await reconciler.reconcile([alpha]);
    if (mode === "empty") {
      await reconciler.reconcile([]);
      await reconciler.reconcile([]);
    } else {
      await reconciler.dispose();
      await reconciler.dispose();
    }

    expect(onUnload).toHaveBeenCalledTimes(1);
    expect(registrations.components.size).toBe(0);
    expect(registrations.actionHandlers.size).toBe(0);
  });
});
