import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { KeyboardShortcutsDialog } from "../components/KeyboardShortcutsDialog";

vi.mock("../keyboard/KeyboardShortcutProvider", () => ({
  useKeyboardShortcuts: () => ({
    actions: [{ id: "global.shortcuts", group: "Global", label: "Keyboard shortcuts" }],
    effectiveBindings: { "global.shortcuts": ["?"] },
    activeActionIds: new Set(["global.shortcuts"]),
  }),
}));

describe("KeyboardShortcutsDialog", () => {
  beforeEach(() => vi.clearAllMocks());

  it("closes when Escape is pressed", () => {
    const onClose = vi.fn();
    render(<KeyboardShortcutsDialog open onClose={onClose} />);

    expect(screen.getByRole("dialog", { name: "Keyboard Shortcuts" })).toBeInTheDocument();
    fireEvent.keyDown(window, { key: "Escape" });

    expect(onClose).toHaveBeenCalledOnce();
  });
});
