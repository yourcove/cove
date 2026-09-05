import { fireEvent, render, screen } from "@testing-library/react";
import { useMemo } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  selectExtensionKeyboardBindings,
  scopeExtensionKeyboardRegistrations,
  useExtensionKeyboardBindings,
  useRegisterExtensionKeyboardActions,
} from "../hooks/useRegisterKeyboardActionHandler";
import { KeyboardShortcutProvider, type KeyboardActionInvocation } from "../keyboard/KeyboardShortcutProvider";

const action = vi.fn<(context: KeyboardActionInvocation) => void>();

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null, hasPermission: () => true }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    manifest: {
      keyboardActions: [
        {
          id: "extension:sample:run",
          extensionId: "sample",
          label: "Run",
          group: "Sample",
          defaultBindings: ["r"],
          scopes: [{ surface: "local", page: "sample" }],
          repeatable: true,
        },
        {
          id: "extension:sample:unscoped",
          extensionId: "sample",
          label: "Unscoped",
          group: "Sample",
          defaultBindings: ["u"],
          scopes: [],
        },
      ],
      keyboardShortcutPresets: [],
    },
  }),
}));

vi.mock("../utils/userUiPreferences", () => ({
  updateAuthenticatedUserUiPreferences: vi.fn(),
}));

vi.mock("../utils/overlayState", () => ({ isOverlayOpen: () => false }));

function ExtensionHarness({
  enabled = true,
  handler = action,
  id = "run",
  surface = "local",
}: {
  enabled?: boolean;
  handler?: (context: KeyboardActionInvocation) => void;
  id?: string;
  surface?: "local" | "overlay";
}) {
  const registrations = useMemo(() => [{ id, action: handler, enabled, surface }], [enabled, handler, id, surface]);
  useRegisterExtensionKeyboardActions("sample", registrations);
  const bindings = useExtensionKeyboardBindings("sample");
  return (
    <button type="button" data-bindings={JSON.stringify(bindings)}>
      Target
    </button>
  );
}

describe("extension keyboard runtime", () => {
  beforeEach(() => action.mockClear());

  it("namespaces local registrations and rejects invalid batches", () => {
    const handler = () => undefined;
    const registration = {
      id: " run ",
      action: handler,
      bindings: ["q"],
      canHandle: () => true,
      mode: "global",
    };
    expect(scopeExtensionKeyboardRegistrations(" sample ", [registration])).toEqual([
      {
        id: "extension:sample:run",
        action: handler,
        enabled: undefined,
        surface: undefined,
      },
    ]);
    expect(() => scopeExtensionKeyboardRegistrations("", [])).toThrow(/extension id/i);
    expect(() =>
      scopeExtensionKeyboardRegistrations("sample", [
        { id: "run", action: handler },
        { id: "run", action: handler },
      ]),
    ).toThrow(/duplicate/i);
  });

  it("returns copied extension-local resolved bindings including unbound actions", () => {
    const source = {
      "extension:sample:run": ["Ctrl+r"],
      "extension:sample:disabled": [],
      "extension:other:run": ["o"],
    };
    const selected = selectExtensionKeyboardBindings("sample", source);
    expect(selected).toEqual({ run: ["Ctrl+r"], disabled: [] });
    expect(selected.run).not.toBe(source["extension:sample:run"]);
  });

  it("dispatches resolved bindings with narrow final-stroke context", () => {
    render(
      <KeyboardShortcutProvider>
        <ExtensionHarness />
      </KeyboardShortcutProvider>,
    );
    const target = screen.getByRole("button", { name: "Target" });

    const event = new KeyboardEvent("keydown", { key: "r", repeat: true, bubbles: true, cancelable: true });
    target.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(action).toHaveBeenCalledWith({ sequence: "r", target, repeat: true });
    expect(target).toHaveAttribute("data-bindings", JSON.stringify({ run: ["r"], unscoped: ["u"] }));
  });

  it("reacts to enablement and unregisters on unmount", () => {
    const view = render(
      <KeyboardShortcutProvider>
        <ExtensionHarness enabled={false} />
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button"), { key: "r" });
    expect(action).not.toHaveBeenCalled();

    view.rerender(
      <KeyboardShortcutProvider>
        <ExtensionHarness enabled />
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button"), { key: "r" });
    expect(action).toHaveBeenCalledTimes(1);

    view.unmount();
    fireEvent.keyDown(window, { key: "r" });
    expect(action).toHaveBeenCalledTimes(1);
  });

  it("rejects a registration surface not declared by the manifest action", () => {
    render(
      <KeyboardShortcutProvider>
        <ExtensionHarness surface="overlay" />
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button"), { key: "r" });
    expect(action).not.toHaveBeenCalled();
  });

  it("rejects a requested surface when an extension action declares no scopes", () => {
    render(
      <KeyboardShortcutProvider>
        <ExtensionHarness id="unscoped" surface="overlay" />
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button"), { key: "u" });
    expect(action).not.toHaveBeenCalled();
  });

  it("suppresses local extension actions behind extension-owned ARIA dialogs", () => {
    render(
      <KeyboardShortcutProvider>
        <ExtensionHarness />
        <section role="dialog" aria-modal="true">
          <button type="button">Dialog action</button>
        </section>
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button", { name: "Dialog action" }), { key: "r" });
    expect(action).not.toHaveBeenCalled();
  });

  it("does not treat an unrelated inline listbox as a document overlay", () => {
    render(
      <KeyboardShortcutProvider>
        <ExtensionHarness />
        <div role="listbox">
          <button type="button" role="option">
            Inline option
          </button>
        </div>
      </KeyboardShortcutProvider>,
    );
    fireEvent.keyDown(screen.getByRole("button", { name: "Target" }), { key: "r" });
    expect(action).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(screen.getByRole("option", { name: "Inline option" }), { key: "r" });
    expect(action).toHaveBeenCalledTimes(1);
  });

  it("dispatches the latest callback without re-registering the action", () => {
    const first = vi.fn();
    const second = vi.fn();
    const view = render(
      <KeyboardShortcutProvider>
        <ExtensionHarness handler={first} />
      </KeyboardShortcutProvider>,
    );
    view.rerender(
      <KeyboardShortcutProvider>
        <ExtensionHarness handler={second} />
      </KeyboardShortcutProvider>,
    );

    fireEvent.keyDown(screen.getByRole("button"), { key: "r" });
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
  });
});
