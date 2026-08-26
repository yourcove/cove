import { expect, test } from "bun:test";
import { fetchThemeAccent, themeAccent } from "../src/theme";

test("resolves the active manifest theme accent and Cove fallback", () => {
  const me = { user: { uiPreferences: { theme: { activeThemeId: "dark-emerald" } } } };
  const manifest = { themes: [{ id: "dark-emerald", cssVariables: { "--color-accent": "#3bbd83" } }] };
  expect(themeAccent(me, manifest)).toBe("#3bbd83");
  expect(themeAccent({ user: {} }, manifest)).toBe("#4f8ff7");
  expect(themeAccent({ user: {} }, { themes: [{ id: "default", cssVariables: { "--color-accent": "#123456" } }] })).toBe("#123456");
  expect(themeAccent({ user: { uiPreferences: { theme: { activeThemeId: "missing" } } } }, manifest)).toBe("#4f8ff7");
});

test("uses a custom RGB accent without requiring a manifest", () => {
  expect(themeAccent({
    user: { uiPreferences: { theme: { activeThemeId: "custom", customThemeColors: { "--color-accent": "rgb(59, 189, 131)" } } } },
  })).toBe("rgb(59, 189, 131)");
});

test("theme discovery is best-effort and requests user and manifest data concurrently", async () => {
  const requests: string[] = [];
  const client = {
    async get<T>(path: string): Promise<T> {
      requests.push(path);
      if (path === "auth/me") return { user: { uiPreferences: { theme: { activeThemeId: "dark-emerald" } } } } as T;
      return { themes: [{ id: "dark-emerald", cssVariables: { "--color-accent": "#3bbd83" } }] } as T;
    },
  };
  expect(await fetchThemeAccent(client)).toBe("#3bbd83");
  expect(requests).toEqual(["auth/me", "extensions/manifest"]);

  const unavailable = { get: async () => { throw new Error("offline"); } };
  expect(await fetchThemeAccent(unavailable)).toBe("#4f8ff7");
});
