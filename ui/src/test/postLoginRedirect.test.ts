import { describe, expect, it } from "vitest";
import { claimServerPageRedirect, getPostLoginRedirectUrl, isServerPage } from "../auth/postLoginRedirect";

const origin = "https://cove.example";

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => [...values.keys()][index] ?? null,
    removeItem: (key) => void values.delete(key),
    setItem: (key, value) => void values.set(key, value),
  };
}

describe("getPostLoginRedirectUrl", () => {
  it("returns a same-origin path with its query and hash", () => {
    expect(getPostLoginRedirectUrl("?redirect=%2Fswagger%2Findex.html%3Fa%3D1%23op", origin)).toBe(
      "/swagger/index.html?a=1#op",
    );
  });

  it.each([
    "",
    "?redirect=https%3A%2F%2Fevil.example%2F",
    "?redirect=%2F%2Fevil.example",
    "?redirect=%2Flogin",
    "?redirect=%2F%5Cevil.example",
    "?redirect=%2Fswagger%0A",
  ])("falls back to the home page for %s", (search) => {
    expect(getPostLoginRedirectUrl(search, origin)).toBe("/");
  });
});

describe("isServerPage", () => {
  it.each(["/swagger", "/swagger/index.html", "/SWAGGER/index.html?x=1", "/openapi/v1.json", "/swagger#top"])(
    "treats %s as served by the API",
    (path) => {
      expect(isServerPage(path)).toBe(true);
    },
  );

  it.each(["/", "/videos", "/swaggerish", "/settings/swagger", "/openapi-notes"])(
    "treats %s as an app page",
    (path) => {
      expect(isServerPage(path)).toBe(false);
    },
  );
});

describe("claimServerPageRedirect", () => {
  it("allows one redirect per target within the window", () => {
    const storage = memoryStorage();

    expect(claimServerPageRedirect("/swagger/index.html", 1_000, storage)).toBe(true);
    expect(claimServerPageRedirect("/swagger/index.html", 5_000, storage)).toBe(false);
    expect(claimServerPageRedirect("/swagger/index.html", 20_000, storage)).toBe(true);
  });

  it("does not hold back a different target", () => {
    const storage = memoryStorage();

    expect(claimServerPageRedirect("/swagger/index.html", 1_000, storage)).toBe(true);
    expect(claimServerPageRedirect("/openapi/v1.json", 2_000, storage)).toBe(true);
  });

  it("redirects when storage is unavailable or holds junk", () => {
    const storage = memoryStorage();
    storage.setItem("cove_server_page_redirect", "not json");

    expect(claimServerPageRedirect("/swagger", 1_000, undefined)).toBe(true);
    expect(claimServerPageRedirect("/swagger", 1_000, storage)).toBe(true);
  });
});
