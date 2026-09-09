import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Group } from "../api/types";
import { GroupEditModal } from "../pages/GroupEditModal";

const { mockGroups } = vi.hoisted(() => ({
  mockGroups: {
    dynamicSources: vi.fn(),
    containingGroups: vi.fn(),
    update: vi.fn(),
    addSubGroup: vi.fn(),
    removeSubGroup: vi.fn(),
  },
}));

vi.mock("../api/client", () => ({ groups: mockGroups }));

function buildGroup(): Group {
  return {
    id: 12,
    name: "Verification group",
    aliases: "",
    director: undefined,
    date: undefined,
    studioId: undefined,
    description: undefined,
    urls: [],
    tags: [],
    kind: "static",
    querySourceKey: undefined,
    queryJson: undefined,
    showInVideoLists: true,
    customFields: {},
    fieldProvenance: [],
    videoCount: 0,
    subGroupCount: 0,
    containingGroupCount: 0,
    createdAt: "2026-08-24T00:00:00Z",
    updatedAt: "2026-08-24T00:00:00Z",
  } as Group;
}

describe("GroupEditModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGroups.containingGroups.mockResolvedValue([]);
  });

  afterEach(() => vi.restoreAllMocks());

  it("does not resynchronize form state while the modal is closed", async () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <GroupEditModal group={buildGroup()} open={false} onClose={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(mockGroups.dynamicSources).not.toHaveBeenCalled());
    expect(consoleError.mock.calls.some(([message]) => String(message).includes("Maximum update depth"))).toBe(false);
  });

  it("preserves form edits when dynamic sources finish loading", async () => {
    let resolveSources!: (sources: Array<{ key: string; displayName: string }>) => void;
    mockGroups.dynamicSources.mockReturnValue(
      new Promise((resolve) => {
        resolveSources = resolve;
      }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <GroupEditModal group={buildGroup()} open onClose={vi.fn()} />
      </QueryClientProvider>,
    );

    const nameInput = screen.getByPlaceholderText("Group name");
    fireEvent.change(nameInput, { target: { value: "Unsaved draft name" } });
    expect(nameInput).toHaveValue("Unsaved draft name");

    await act(async () => {
      resolveSources([{ key: "extension-source", displayName: "Extension source" }]);
      await Promise.resolve();
    });

    await waitFor(() => expect(mockGroups.dynamicSources).toHaveBeenCalledOnce());
    expect(nameInput).toHaveValue("Unsaved draft name");
  });
});
