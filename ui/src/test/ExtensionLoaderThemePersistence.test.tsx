import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExtensionManifest } from "../api/types";
import {
  THEME_BOOT_STORAGE_KEY,
  THEME_BOOT_VERSION,
  applyThemeBootSnapshot,
  type ThemeBootSnapshot,
} from "../theme/themeBoot";
import { ExtensionLoaderProvider, useExtensions } from "../extensions/ExtensionLoader";

const THEME_STORAGE_KEY = "cove-active-theme";
const SELECTED_THEME_ID = "cinema-dark";

const mocks = vi.hoisted(() => ({
  getManifest: vi.fn(),
  updateUiPreferences: vi.fn(),
  troubleshootingModeEnabled: false,
  user: {
    id: "1",
    username: "tester",
    kind: "user" as const,
    permissions: ["*"],
    // Inlined rather than referencing SELECTED_THEME_ID: vi.hoisted runs before module consts.
    uiPreferences: { theme: { activeThemeId: "cinema-dark" } },
  },
}));

vi.mock("../api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/client")>();
  return {
    ...original,
    extensions: { ...original.extensions, getManifest: mocks.getManifest },
  };
});

vi.mock("../utils/userUiPreferences", async (importOriginal) => {
  const original = await importOriginal<typeof import("../utils/userUiPreferences")>();
  return { ...original, updateAuthenticatedUserUiPreferences: mocks.updateUiPreferences };
});

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: mocks.user, hasPermission: () => true }),
}));

vi.mock("../router/RouteRegistry", () => ({
  useRouteRegistry: () => ({
    register: vi.fn(),
    registerSlot: vi.fn(),
    unregister: vi.fn(),
    unregisterSlot: vi.fn(),
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { troubleshootingModeEnabled: mocks.troubleshootingModeEnabled } } }),
}));

const OTHER_THEME_ID = "legacy";

// Fully typed on purpose: an `as unknown as ExtensionManifest` fixture would silently rot the day
// ExtensionManifest gains a required contribution array.
function buildManifest(): ExtensionManifest {
  return {
    extensionBundles: [],
    pages: [],
    slots: [],
    tabs: [],
    features: [],
    themes: [
      {
        id: SELECTED_THEME_ID,
        name: "Cinema Dark",
        colorScheme: "light",
        cssVariables: { "--color-background": "#c5cad4", "--color-foreground": "#111827" },
        cssUrl: "/extensions/cinema/theme.css",
      },
      { id: OTHER_THEME_ID, name: "Legacy" },
    ],
    componentStyles: [],
    layoutStyles: [],
    settingsTabs: [],
    settingsPanels: [],
    componentOverrides: [],
    pageOverrides: [],
    dialogOverrides: [],
    actions: [],
    listFilters: [],
    listSorts: [],
  };
}

function ThemeProbe() {
  const { activeThemeId, loaded, refreshManifest, setActiveTheme } = useExtensions();
  return (
    <>
      <div data-testid="active-theme">{activeThemeId ?? "(none)"}</div>
      <div data-testid="loaded">{String(loaded)}</div>
      <button onClick={() => void refreshManifest()}>Refresh</button>
      <button onClick={() => setActiveTheme(OTHER_THEME_ID)}>Pick other theme</button>
    </>
  );
}

function renderLoader() {
  return render(
    <ExtensionLoaderProvider>
      <ThemeProbe />
    </ExtensionLoaderProvider>,
  );
}

describe("ExtensionLoaderProvider theme persistence", () => {
  beforeEach(() => {
    mocks.getManifest.mockReset();
    mocks.updateUiPreferences.mockReset();
    mocks.troubleshootingModeEnabled = false;
    localStorage.clear();
    localStorage.setItem(THEME_STORAGE_KEY, SELECTED_THEME_ID);
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    document.documentElement.removeAttribute("data-theme");
    document.documentElement.removeAttribute("data-color-scheme");
    document.getElementById("cove-theme-override")?.remove();
    document.getElementById("cove-theme-css")?.remove();
  });

  it("keeps the selected theme when the manifest request fails", async () => {
    mocks.getManifest.mockRejectedValue(new Error("API Error 500: manifest unavailable"));

    renderLoader();

    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
    expect(mocks.updateUiPreferences).not.toHaveBeenCalled();
    expect(screen.getByTestId("active-theme")).toHaveTextContent(SELECTED_THEME_ID);
  });

  it("reapplies the selected theme once a later manifest request succeeds", async () => {
    mocks.getManifest.mockRejectedValueOnce(new Error("API Error 500: manifest unavailable"));
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();

    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));
    expect(document.documentElement).not.toHaveAttribute("data-theme");

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));

    await waitFor(() => expect(document.documentElement).toHaveAttribute("data-theme", SELECTED_THEME_ID));
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
    expect(mocks.updateUiPreferences).not.toHaveBeenCalled();
  });

  it("keeps the selected theme while troubleshooting mode withdraws every extension", async () => {
    mocks.troubleshootingModeEnabled = true;
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();

    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
    expect(mocks.updateUiPreferences).not.toHaveBeenCalled();
    expect(screen.getByTestId("active-theme")).toHaveTextContent(SELECTED_THEME_ID);
  });

  // This is the genuine-uninstall shape, not an outage, and it pins a deliberate trade-off: a theme
  // id whose extension is really gone is kept in storage forever rather than rewritten, because the
  // client cannot tell an uninstall from a transient absence. A dangling id costs nothing — every
  // reader of it is null-tolerant — so this is not a bug to "fix" by reinstating the reset.
  it("keeps the selected theme when the manifest contributes no themes at all", async () => {
    const manifest = buildManifest();
    manifest.themes = [];
    mocks.getManifest.mockResolvedValue(manifest);

    renderLoader();

    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
    expect(mocks.updateUiPreferences).not.toHaveBeenCalled();
    expect(screen.getByTestId("active-theme")).toHaveTextContent(SELECTED_THEME_ID);
  });

  // The refresh that superseded the initial load: `applyManifest` drops the stale response, so the
  // provider finishes loading with no manifest at all and every contributed theme withdrawn.
  it("keeps the selected theme when a failed refresh supersedes an in-flight initial load", async () => {
    let resolveInitialLoad: (manifest: ExtensionManifest) => void = () => {};
    const initialLoad = new Promise<ExtensionManifest>((resolve) => {
      resolveInitialLoad = resolve;
    });
    mocks.getManifest.mockReturnValueOnce(initialLoad);
    mocks.getManifest.mockRejectedValueOnce(new Error("API Error 500: manifest unavailable"));

    renderLoader();
    expect(screen.getByTestId("loaded")).toHaveTextContent("false");

    // Bumps the request generation while the first request is still outstanding.
    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(mocks.getManifest).toHaveBeenCalledTimes(2));

    await act(async () => {
      resolveInitialLoad(buildManifest());
      await initialLoad;
    });

    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
    expect(mocks.updateUiPreferences).not.toHaveBeenCalled();
    expect(screen.getByTestId("active-theme")).toHaveTextContent(SELECTED_THEME_ID);
  });

  // Themes are declarative, so the app need not sit behind every extension module import to know its
  // look. The boot cache still waits for the reconciled manifest, which may withdraw the theme.
  it("paints the theme from the fetched manifest while extension modules are still importing", async () => {
    const manifest = buildManifest();
    manifest.extensionBundles = [{ extensionId: "ext.slow", version: "1.0.0", jsBundleUrl: "/ext/slow.mjs" }];
    mocks.getManifest.mockResolvedValue(manifest);
    let finishImport: () => void = () => {};
    const importBundle = vi.fn(
      () =>
        new Promise<{ default: object }>((resolve) => {
          finishImport = () => resolve({ default: {} });
        }),
    );
    function ReadinessProbe() {
      const { loaded, themeReady } = useExtensions();
      return <div data-testid="readiness">{`${themeReady}/${loaded}`}</div>;
    }

    render(
      <ExtensionLoaderProvider importBundle={importBundle}>
        <ReadinessProbe />
      </ExtensionLoaderProvider>,
    );

    await waitFor(() => expect(screen.getByTestId("readiness")).toHaveTextContent("true/false"));
    expect(importBundle).toHaveBeenCalled();
    await waitFor(() => expect(document.documentElement).toHaveAttribute("data-theme", SELECTED_THEME_ID));
    expect(localStorage.getItem(THEME_BOOT_STORAGE_KEY)).toBeNull();
    const themeStylesheet = document.getElementById("cove-theme-css");
    expect(themeStylesheet).not.toBeNull();

    await act(async () => finishImport());

    await waitFor(() => expect(screen.getByTestId("readiness")).toHaveTextContent("true/true"));
    expect(document.documentElement).toHaveAttribute("data-theme", SELECTED_THEME_ID);
    // The reconciled manifest carries the same theme; its stylesheet must not be dropped and re-added.
    expect(document.getElementById("cove-theme-css")).toBe(themeStylesheet);
    expect(localStorage.getItem(THEME_BOOT_STORAGE_KEY)).not.toBeNull();
  });

  // Guards the other half of the contract: these tests assert that nothing writes the preference on
  // the user's behalf, which a no-op `setActiveTheme` would also satisfy. A real selection must
  // still persist.
  it("still persists a theme the user actually selects", async () => {
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();
    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    fireEvent.click(screen.getByRole("button", { name: "Pick other theme" }));

    await waitFor(() => expect(screen.getByTestId("active-theme")).toHaveTextContent(OTHER_THEME_ID));
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(OTHER_THEME_ID);
    expect(mocks.updateUiPreferences).toHaveBeenCalledTimes(1);
  });
});

// Both directions of the boot cache: what the loader writes, and when it withdraws what the inline
// boot script already painted.
describe("ExtensionLoaderProvider theme boot cache", () => {
  function bootSnapshot(overrides: Partial<ThemeBootSnapshot> = {}): ThemeBootSnapshot {
    return {
      v: THEME_BOOT_VERSION,
      u: "1",
      themeId: SELECTED_THEME_ID,
      colorScheme: "light",
      bgAnimation: null,
      vars: { "--color-background": "#c5cad4", "--color-foreground": "#111827" },
      cssUrl: "/extensions/cinema/theme.css",
      componentStyle: "default",
      layoutStyle: "default",
      styleOptions: {},
      ...overrides,
    };
  }

  const readSnapshot = () => JSON.parse(localStorage.getItem(THEME_BOOT_STORAGE_KEY) ?? "null");

  beforeEach(() => {
    mocks.getManifest.mockReset();
    mocks.updateUiPreferences.mockReset();
    mocks.troubleshootingModeEnabled = false;
    mocks.user.id = "1";
    localStorage.clear();
    localStorage.setItem(THEME_STORAGE_KEY, SELECTED_THEME_ID);
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    mocks.user.id = "1";
    document.documentElement.removeAttribute("data-theme");
    document.documentElement.removeAttribute("data-color-scheme");
    document.getElementById("cove-theme-override")?.remove();
    document.getElementById("cove-theme-css")?.remove();
  });

  it("caches the look it just painted", async () => {
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();
    await waitFor(() => expect(document.documentElement).toHaveAttribute("data-theme", SELECTED_THEME_ID));

    await waitFor(() =>
      expect(readSnapshot()).toMatchObject({
        v: THEME_BOOT_VERSION,
        u: "1",
        themeId: SELECTED_THEME_ID,
        colorScheme: "light",
        vars: { "--color-background": "#c5cad4" },
        cssUrl: "/extensions/cinema/theme.css",
      }),
    );
  });

  // Counterpart of "keeps the selected theme when the manifest contributes no themes": the selection
  // survives, the cached appearance must not.
  it("drops the cached look but keeps the selection when the theme is gone", async () => {
    localStorage.setItem(THEME_BOOT_STORAGE_KEY, JSON.stringify(bootSnapshot()));
    const manifest = buildManifest();
    manifest.themes = [];
    mocks.getManifest.mockResolvedValue(manifest);

    renderLoader();
    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    await waitFor(() => expect(localStorage.getItem(THEME_BOOT_STORAGE_KEY)).toBeNull());
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
  });

  it("takes over the elements the boot script painted, leaving one override behind", async () => {
    applyThemeBootSnapshot(document, bootSnapshot());
    expect(document.querySelectorAll("[data-cove-boot]")).toHaveLength(2);
    const bootLink = document.getElementById("cove-theme-css");
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();
    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    await waitFor(() => expect(document.querySelectorAll("#cove-theme-override")).toHaveLength(1));
    expect(document.querySelectorAll("[data-cove-boot]")).toHaveLength(0);
    expect(document.getElementById("cove-theme-override")).not.toHaveAttribute("data-cove-boot");
    // Same stylesheet URL: the booted link is adopted, not removed and re-added.
    expect(document.querySelectorAll("#cove-theme-css")).toHaveLength(1);
    expect(document.getElementById("cove-theme-css")).toBe(bootLink);
  });

  // A manifest that does not load says nothing about what the user chose.
  it("leaves the booted look alone when the manifest request fails", async () => {
    localStorage.setItem(THEME_BOOT_STORAGE_KEY, JSON.stringify(bootSnapshot()));
    applyThemeBootSnapshot(document, bootSnapshot());
    mocks.getManifest.mockRejectedValue(new Error("API Error 500: manifest unavailable"));

    renderLoader();
    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(document.getElementById("cove-theme-override")).toHaveAttribute("data-cove-boot");
    expect(document.documentElement).toHaveAttribute("data-color-scheme", "light");
    expect(readSnapshot()).toMatchObject({ themeId: SELECTED_THEME_ID });
  });

  it("strips the booted look in troubleshooting mode", async () => {
    localStorage.setItem(THEME_BOOT_STORAGE_KEY, JSON.stringify(bootSnapshot()));
    applyThemeBootSnapshot(document, bootSnapshot());
    mocks.troubleshootingModeEnabled = true;
    mocks.getManifest.mockResolvedValue(buildManifest());

    renderLoader();
    await waitFor(() => expect(screen.getByTestId("loaded")).toHaveTextContent("true"));

    expect(document.getElementById("cove-theme-override")).toBeNull();
    expect(document.getElementById("cove-theme-css")).toBeNull();
    expect(document.documentElement).not.toHaveAttribute("data-theme");
    expect(localStorage.getItem(THEME_BOOT_STORAGE_KEY)).toBeNull();
    // Troubleshooting mode withdraws the appearance, not the preference.
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(SELECTED_THEME_ID);
  });

  // A shared browser: /me resolves to someone other than the snapshot's user.
  it("strips a booted look belonging to another user before the manifest resolves", async () => {
    localStorage.setItem(THEME_BOOT_STORAGE_KEY, JSON.stringify(bootSnapshot({ u: "1" })));
    applyThemeBootSnapshot(document, bootSnapshot({ u: "1" }));
    mocks.user.id = "2";
    mocks.getManifest.mockReturnValue(new Promise(() => {}));

    renderLoader();

    await waitFor(() => expect(document.getElementById("cove-theme-override")).toBeNull());
    expect(document.documentElement).not.toHaveAttribute("data-theme");
    expect(localStorage.getItem(THEME_BOOT_STORAGE_KEY)).toBeNull();
  });
});
