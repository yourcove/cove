import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { KeyboardShortcutSettings } from "../components/KeyboardShortcutSettings";

const keyboardShortcutMocks = vi.hoisted(() => ({
  updatePersonalPreset: vi.fn(),
}));

vi.mock("@tanstack/react-query", () => ({
  useQuery: () => ({
    data: [
      { id: "sample", name: "Shared Tools" },
      { id: "other-tools", name: "Shared Tools" },
    ],
  }),
}));

vi.mock("../keyboard/KeyboardShortcutProvider", () => ({
  useKeyboardShortcuts: () => ({
    actions: [
      {
        id: "global.help",
        group: "Global",
        label: "Help and tutorials",
        defaultBindings: ["?"],
      },
      {
        id: "extension:sample:play",
        group: "Playback",
        label: "Play sample",
        defaultBindings: ["p"],
        source: "extension",
        extensionId: "sample",
      },
      {
        id: "extension:other-tools:pause",
        group: "Playback",
        label: "Pause other tool",
        defaultBindings: ["o"],
        source: "extension",
        extensionId: "other-tools",
      },
    ],
    presets: [
      {
        schemaVersion: 1,
        id: "personal:copy",
        name: "Cove Native copy",
        description: "A personal keyboard shortcut preset.",
        unmappedActions: "action-defaults",
        bindings: {},
        provenance: { source: "personal" },
      },
    ],
    activePresetId: "personal:copy",
    effectivePresetId: "personal:copy",
    effectiveBindings: {
      "global.help": ["?"],
      "extension:sample:play": ["p"],
      "extension:other-tools:pause": ["o"],
    },
    selectPreset: vi.fn(),
    clonePreset: vi.fn(),
    updatePersonalPreset: keyboardShortcutMocks.updatePersonalPreset,
    deletePersonalPreset: vi.fn(),
    importPreset: vi.fn(),
    exportPreset: vi.fn(),
    setDispatchSuspended: vi.fn(),
    showChordHints: true,
    setShowChordHints: vi.fn(),
  }),
}));

describe("KeyboardShortcutSettings", () => {
  it("shows Cove and each extension's shortcuts in separate tabs", () => {
    render(<KeyboardShortcutSettings />);

    const coveTab = screen.getByRole("tab", { name: "Cove" });
    expect(coveTab).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Shared Tools (sample)" })).toHaveAttribute("aria-selected", "false");
    const otherToolsTab = screen.getByRole("tab", { name: "Shared Tools (other-tools)" });
    expect(otherToolsTab).toHaveAttribute("aria-selected", "false");

    const covePanel = screen.getByRole("tabpanel", { name: "Cove" });
    expect(within(covePanel).getByRole("heading", { name: "Global" })).toBeInTheDocument();
    expect(within(covePanel).getByText("Help and tutorials")).toBeInTheDocument();
    expect(within(covePanel).queryByText("Play sample")).not.toBeInTheDocument();

    fireEvent.keyDown(coveTab, { key: "ArrowRight" });
    expect(otherToolsTab).toHaveAttribute("aria-selected", "true");

    fireEvent.click(screen.getByRole("tab", { name: "Shared Tools (sample)" }));
    const samplePanel = screen.getByRole("tabpanel", { name: "Shared Tools (sample)" });
    expect(within(samplePanel).getByRole("heading", { name: "Playback" })).toBeInTheDocument();
    expect(within(samplePanel).getByText("Play sample")).toBeInTheDocument();
    expect(within(samplePanel).queryByText("Pause other tool")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Shared Tools (other-tools)" }));
    const otherPanel = screen.getByRole("tabpanel", { name: "Shared Tools (other-tools)" });
    expect(within(otherPanel).getByText("Pause other tool")).toBeInTheDocument();
    expect(within(otherPanel).queryByText("Play sample")).not.toBeInTheDocument();
  });

  it("announces per-tab search result counts", () => {
    render(<KeyboardShortcutSettings />);

    fireEvent.change(screen.getByPlaceholderText("Search shortcuts"), { target: { value: "Play sample" } });

    expect(screen.getByRole("tab", { name: "Cove, 0 matches" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Shared Tools (sample), 1 match" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Shared Tools (other-tools), 0 matches" })).toBeInTheDocument();
  });

  it("renames an editable preset", () => {
    render(<KeyboardShortcutSettings />);

    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    const dialog = screen.getByRole("dialog", { name: "Rename keyboard shortcut preset" });
    const nameInput = within(dialog).getByRole("textbox", { name: "Preset name" });
    expect(within(dialog).getByRole("button", { name: "Save" })).toBeDisabled();
    fireEvent.change(nameInput, { target: { value: "  My shortcuts  " } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    expect(keyboardShortcutMocks.updatePersonalPreset).toHaveBeenCalledWith(
      expect.objectContaining({ id: "personal:copy", name: "My shortcuts" }),
    );
    expect(screen.queryByRole("dialog", { name: "Rename keyboard shortcut preset" })).not.toBeInTheDocument();
  });

  it("keeps focus within the rename dialog and restores it on Escape", () => {
    render(<KeyboardShortcutSettings />);

    const renameButton = screen.getByRole("button", { name: "Rename" });
    fireEvent.click(renameButton);
    const dialog = screen.getByRole("dialog", { name: "Rename keyboard shortcut preset" });
    const nameInput = within(dialog).getByRole("textbox", { name: "Preset name" });
    const cancelButton = within(dialog).getByRole("button", { name: "Cancel" });
    expect(nameInput).toHaveFocus();

    fireEvent.keyDown(nameInput, { key: "Tab", shiftKey: true });
    expect(cancelButton).toHaveFocus();
    fireEvent.keyDown(cancelButton, { key: "Tab" });
    expect(nameInput).toHaveFocus();

    fireEvent.keyDown(nameInput, { key: "Escape" });
    expect(screen.queryByRole("dialog", { name: "Rename keyboard shortcut preset" })).not.toBeInTheDocument();
    expect(renameButton).toHaveFocus();
  });
});
