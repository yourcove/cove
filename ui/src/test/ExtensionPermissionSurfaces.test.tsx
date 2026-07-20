import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AppRoutes } from "../App";
import { ExtensionLoaderProvider, useExtensions } from "../extensions/ExtensionLoader";

const mocks = vi.hoisted(() => ({
  register: vi.fn(() => vi.fn()),
  registerSlot: vi.fn(() => vi.fn()),
  unregister: vi.fn(),
  unregisterSlot: vi.fn(),
  allowedPermissions: new Set(["catalog.view", "catalog.audit"]),
  user: { id: "1", username: "reader", kind: "user" as const, permissions: ["catalog.view", "catalog.audit"] },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: mocks.user,
    hasPermission: (permission: string) => mocks.allowedPermissions.has(permission),
  }),
}));

vi.mock("../router/RouteRegistry", () => ({
  useRouteRegistry: () => ({
    routes: [],
    register: mocks.register,
    registerSlot: mocks.registerSlot,
    unregister: mocks.unregister,
    unregisterSlot: mocks.unregisterSlot,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { troubleshootingModeEnabled: false } } }),
}));

vi.mock("../api/client", () => ({
  extensions: {
    getManifest: vi.fn().mockResolvedValue({
      pages: [
        { route: "allowed-catalog", label: "Allowed", showInNav: true, navOrder: 10, requiredPermissions: ["catalog.view", "catalog.audit"], requiredPermissionMode: "all" },
        { route: "any-catalog", label: "Any", showInNav: true, navOrder: 15, requiredPermissions: ["catalog.admin", "catalog.view"], requiredPermissionMode: "any" },
        { route: "legacy-catalog", label: "Legacy", showInNav: true, navOrder: 18, requiredPermission: "catalog.view" },
        { route: "denied-catalog", label: "Denied", showInNav: true, navOrder: 20, requiredPermissions: ["catalog.view", "catalog.admin"], requiredPermissionMode: "all", extensionId: "catalog", componentName: "DeniedPage" },
      ],
      slots: [],
      tabs: [
        { key: "allowed", label: "Allowed", pageType: "performer", extensionId: "catalog", componentName: "AllowedTab", order: 10, requiredPermissions: ["catalog.view", "catalog.audit"], requiredPermissionMode: "all" },
        { key: "any", label: "Any", pageType: "performer", extensionId: "catalog", componentName: "AnyTab", order: 15, requiredPermissions: ["catalog.admin", "catalog.view"], requiredPermissionMode: "any" },
        { key: "legacy", label: "Legacy", pageType: "performer", extensionId: "catalog", componentName: "LegacyTab", order: 18, requiredPermission: "catalog.view" },
        { key: "denied", label: "Denied", pageType: "performer", extensionId: "catalog", componentName: "DeniedTab", order: 20, requiredPermissions: ["catalog.view", "catalog.admin"], requiredPermissionMode: "all" },
      ],
      features: [],
      themes: [],
      componentStyles: [],
      layoutStyles: [],
      settingsTabs: [],
      settingsPanels: [],
      pageOverrides: [],
      dialogOverrides: [],
      actions: [],
      listFilters: [],
      listSorts: [],
      extensionBundles: [
        { extensionId: "catalog", version: "1.0.0", jsBundleUrl: "/catalog.mjs?v=1" },
      ],
    }),
  },
}));

function TabProbe() {
  const { getTabsForPage } = useExtensions();
  return <div>{getTabsForPage("performer").map((tab) => tab.key).join(",")}</div>;
}

afterEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});

describe("extension permission surfaces", () => {
  it("registers and returns only pages and tabs the user may access", async () => {
    const importBundle = vi.fn().mockResolvedValue({
      default: { components: { DeniedPage: () => null } },
    });
    render(
      <ExtensionLoaderProvider importBundle={importBundle}>
        <TabProbe />
      </ExtensionLoaderProvider>,
    );

    await waitFor(() => expect(mocks.register).toHaveBeenCalled());

    expect(mocks.register).toHaveBeenCalledTimes(3);
    expect(mocks.register).toHaveBeenCalledWith(expect.objectContaining({ page: "allowed-catalog" }));
    expect(mocks.register).toHaveBeenCalledWith(expect.objectContaining({ page: "any-catalog" }));
    expect(mocks.register).toHaveBeenCalledWith(expect.objectContaining({ page: "legacy-catalog" }));
    expect(screen.getByText("allowed,any,legacy")).toBeInTheDocument();
  });

  it("blocks a direct route before rendering its contributed component", async () => {
    const deniedPage = vi.fn(() => <div>Denied component rendered</div>);
    const importBundle = vi.fn().mockResolvedValue({
      default: { components: { DeniedPage: deniedPage } },
    });

    render(
      <ExtensionLoaderProvider importBundle={importBundle}>
        <AppRoutes route={{ page: "denied-catalog" } as never} navigate={vi.fn()} />
      </ExtensionLoaderProvider>,
    );

    expect(await screen.findByRole("heading", { name: "Access denied" })).toBeInTheDocument();
    expect(importBundle).toHaveBeenCalledWith("/catalog.mjs?v=1");
    expect(deniedPage).not.toHaveBeenCalled();
  });
});
