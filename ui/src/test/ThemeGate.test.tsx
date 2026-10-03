import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({ themeReady: false }));
vi.mock("../extensions/ExtensionLoader", () => ({ useExtensions: () => ({ themeReady: state.themeReady }) }));

async function renderGate() {
  // The gate reads the painted state once, at module scope, so set the DOM up before importing.
  vi.resetModules();
  const { ThemeGate } = await import("../components/ThemeGate");
  return render(
    <ThemeGate>
      <div data-testid="app">app</div>
    </ThemeGate>,
  );
}

function paintBootedLook() {
  // What the boot script stamps on <html> when it applies a snapshot; a theme can legitimately carry
  // no colours, so ownership is the marker rather than the presence of a style element.
  document.documentElement.setAttribute("data-cove-boot-theme", "1");
}

describe("ThemeGate", () => {
  beforeEach(() => {
    state.themeReady = false;
    vi.useRealTimers();
  });

  afterEach(() => {
    cleanup();
    document.documentElement.removeAttribute("data-cove-boot-theme");
  });

  // First sign-in on a new browser: nothing cached, so the palette is genuinely unknown.
  it("holds the app back while the theme is still unknown", async () => {
    await renderGate();

    expect(screen.queryByTestId("app")).toBeNull();
  });

  it("releases the app once the extension manifest settles", async () => {
    state.themeReady = true;
    await renderGate();

    expect(screen.getByTestId("app")).toBeInTheDocument();
  });

  // A repeat visit is already painted, so waiting would delay the app for nothing.
  it("does not wait when the boot script already painted the user's theme", async () => {
    paintBootedLook();
    await renderGate();

    expect(screen.getByTestId("app")).toBeInTheDocument();
  });

  // The gate latches: re-closing it would unmount the app below and lose player and queue state.
  it("stays open once the theme has settled", async () => {
    state.themeReady = true;
    await renderGate();
    expect(screen.getByTestId("app")).toBeInTheDocument();

    state.themeReady = false;
    await act(async () => {
      window.dispatchEvent(new Event("resize"));
    });

    expect(screen.getByTestId("app")).toBeInTheDocument();
  });

  it("gives up waiting rather than holding the app hostage to a hung manifest", async () => {
    vi.useFakeTimers();
    try {
      await renderGate();
      expect(screen.queryByTestId("app")).toBeNull();

      await vi.advanceTimersByTimeAsync(2000);
    } finally {
      vi.useRealTimers();
    }

    await waitFor(() => expect(screen.getByTestId("app")).toBeInTheDocument());
  });
});
