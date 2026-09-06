import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { KeyboardShortcutSettings } from "../components/KeyboardShortcutSettings";

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
        id: "cove:native",
        name: "Cove Native",
        description: "Cove's native keyboard shortcuts.",
        unmappedActions: "action-defaults",
        bindings: {},
        provenance: { source: "cove" },
      },
    ],
    activePresetId: "cove:native",
    effectivePresetId: "cove:native",
    effectiveBindings: {
      "global.help": ["?"],
      "extension:sample:play": ["p"],
      "extension:other-tools:pause": ["o"],
    },
    selectPreset: vi.fn(),
    clonePreset: vi.fn(),
    updatePersonalPreset: vi.fn(),
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
});
