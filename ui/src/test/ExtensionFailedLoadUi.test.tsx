import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ExtensionInfo } from "../api/types";

const mocks = vi.hoisted(() => ({
  registryUninstall: vi.fn(),
}));

function extensionInfo(overrides: Partial<ExtensionInfo>): ExtensionInfo {
  return {
    id: "com.example.extension",
    name: "Example",
    version: "1.0.0",
    enabled: true,
    hasUI: false,
    hasApi: false,
    hasState: false,
    hasJobs: false,
    hasEvents: false,
    hasData: false,
    hasMiddleware: false,
    hasActions: false,
    categories: [],
    dependencies: {},
    externalDependencies: [],
    settings: [],
    kind: "extension",
    source: "registry",
    jobs: [],
    ...overrides,
  };
}

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    extensions: {
      ...actual.extensions,
      list: vi.fn().mockResolvedValue([
        extensionInfo({ id: "com.example.healthy", name: "Healthy Extension" }),
        extensionInfo({
          id: "com.example.broken",
          name: "Broken Extension",
          enabled: false,
          failureReason: "discover: TypeLoadException: Could not load type 'Contract.Widget'.",
        }),
      ]),
      registryCheckUpdates: vi.fn().mockResolvedValue([]),
      registryUninstall: mocks.registryUninstall,
    },
    plugins: { ...actual.plugins, list: vi.fn().mockResolvedValue([]) },
  };
});

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    loadFailures: [],
    retryFailedExtensions: vi.fn(),
    getSettingsPanelsForTab: () => [],
    resolveComponent: () => null,
    manifest: { tutorialTopics: [] },
    refreshManifest: vi.fn().mockResolvedValue({ tutorialTopics: [] }),
  }),
}));

import { ExtensionsPanel } from "../pages/SettingsPage";

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ExtensionsPanel mode="installed" />
    </QueryClientProvider>,
  );
}

function rowFor(name: string) {
  return screen.getByText(name).closest(".overflow-hidden") as HTMLElement;
}

describe("installed extensions that failed to load", () => {
  it("lists the failure reason with an uninstall action instead of an enable toggle", async () => {
    mocks.registryUninstall.mockResolvedValue({
      requiresDependents: false,
      uninstalledExtensions: ["com.example.broken"],
    });
    const user = userEvent.setup();
    renderPanel();

    await screen.findByText("Broken Extension");
    const broken = rowFor("Broken Extension");
    expect(within(broken).getByText("Failed to load")).toBeInTheDocument();
    expect(within(broken).getByText(/TypeLoadException: Could not load type/)).toBeInTheDocument();
    expect(within(broken).queryByRole("button", { name: /^(Enabled|Disabled|Disable)$/ })).not.toBeInTheDocument();
    expect(within(rowFor("Healthy Extension")).getByRole("button", { name: "Enabled" })).toBeInTheDocument();

    await user.click(within(broken).getByTitle("Uninstall extension"));
    const dialog = screen.getByRole("dialog", { name: "Uninstall Extension" });
    await user.click(within(dialog).getByRole("button", { name: "Uninstall" }));

    expect(mocks.registryUninstall).toHaveBeenCalledWith("com.example.broken", false);
  });
});
