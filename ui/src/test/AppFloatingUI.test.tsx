import { useLayoutEffect } from "react";
import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AppFloatingUI, APP_FLOATING_UI_SLOT } from "../components/AppFloatingUI";
import { RouteRegistryProvider, useRouteRegistry, type SlotEntry } from "../router/RouteRegistry";

function RegisteredSlots({ entries }: { entries: SlotEntry<Record<string, never>>[] }) {
  const { registerSlot } = useRouteRegistry();

  useLayoutEffect(() => {
    const unregister = entries.map(registerSlot);
    return () => unregister.forEach((dispose) => dispose());
  }, [entries, registerSlot]);

  return null;
}

function renderHost(entries: SlotEntry<Record<string, never>>[]) {
  return render(
    <RouteRegistryProvider>
      <RegisteredSlots entries={entries} />
      <AppFloatingUI />
    </RouteRegistryProvider>,
  );
}

afterEach(() => vi.restoreAllMocks());

describe("AppFloatingUI", () => {
  it("renders ordered contributions in a viewport layer without blocking the page", () => {
    renderHost([
      {
        id: "later",
        extensionId: "example.later",
        slot: APP_FLOATING_UI_SLOT,
        order: 20,
        render: () => <button type="button">Later control</button>,
      },
      {
        id: "first",
        extensionId: "example.first",
        slot: APP_FLOATING_UI_SLOT,
        order: 10,
        render: () => <button type="button">First control</button>,
      },
    ]);

    const layer = screen.getByTestId("app-floating-ui-layer");
    expect(layer).toHaveClass("fixed", "inset-0", "z-[70]", "pointer-events-none");
    expect(Array.from(layer.querySelectorAll("button")).map((button) => button.textContent)).toEqual([
      "First control",
      "Later control",
    ]);
    expect(screen.getByText("First control").parentElement).toHaveClass("contents", "pointer-events-auto");
  });

  it("contains a failed contribution without removing healthy floating UI", () => {
    vi.spyOn(console, "error").mockImplementation(() => {});

    renderHost([
      {
        id: "broken",
        extensionId: "example.broken",
        slot: APP_FLOATING_UI_SLOT,
        render: () => {
          throw new Error("broken floating UI");
        },
      },
      {
        id: "healthy",
        extensionId: "example.healthy",
        slot: APP_FLOATING_UI_SLOT,
        render: () => <button type="button">Healthy control</button>,
      },
    ]);

    expect(screen.getByText("Healthy control")).toBeInTheDocument();
    expect(screen.queryByText(/Extension error/)).not.toBeInTheDocument();
  });
});
