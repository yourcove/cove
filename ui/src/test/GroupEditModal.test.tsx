import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Group } from "../api/types";
import { GroupEditModal } from "../pages/GroupEditModal";

const { mockGroups } = vi.hoisted(() => ({
  mockGroups: {
    dynamicSources: vi.fn(),
    containingGroups: vi.fn(),
    get: vi.fn(),
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

  it("sends only the fields the user changed", async () => {
    mockGroups.dynamicSources.mockResolvedValue([{ key: "extension-source", displayName: "Extension source" }]);
    mockGroups.update.mockResolvedValue(buildGroup());
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const group = { ...buildGroup(), tags: [{ id: 4, name: "Kept" }], urls: ["https://example.com/group"] } as Group;

    render(
      <QueryClientProvider client={queryClient}>
        <GroupEditModal group={group} open onClose={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(mockGroups.dynamicSources).toHaveBeenCalledOnce());
    fireEvent.change(screen.getByPlaceholderText("Group name"), { target: { value: "Renamed group" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGroups.update).toHaveBeenCalledWith(12, { name: "Renamed group" }));
  });

  it("starts a dynamic group without a source on the first offered source when the filter source is not offered", async () => {
    mockGroups.dynamicSources.mockResolvedValue([{ key: "extension-source", displayName: "Extension source" }]);
    mockGroups.update.mockResolvedValue(buildGroup());
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const group = { ...buildGroup(), kind: "dynamic" } as Group;

    render(
      <QueryClientProvider client={queryClient}>
        <GroupEditModal group={group} open onClose={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("option", { name: "Extension source" })).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGroups.update).toHaveBeenCalledOnce());
    expect(mockGroups.update.mock.calls[0][1]).toMatchObject({ querySourceKey: "extension-source" });
  });

  it("fills parent groups each time the dialog opens", async () => {
    const parent = { ...buildGroup(), id: 30, name: "Parent group" } as Group;
    mockGroups.dynamicSources.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([parent]);
    mockGroups.get.mockResolvedValue(parent);
    mockGroups.update.mockResolvedValue(buildGroup());
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const group = buildGroup();
    const renderModal = (open: boolean) => (
      <QueryClientProvider client={queryClient}>
        <GroupEditModal group={group} open={open} onClose={vi.fn()} />
      </QueryClientProvider>
    );

    const { rerender } = render(renderModal(true));
    await waitFor(() => expect(mockGroups.containingGroups).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByText("Parent group")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: /Remove Parent group/i }));
    await waitFor(() => expect(screen.queryByText("Parent group")).not.toBeInTheDocument());

    rerender(renderModal(false));
    rerender(renderModal(true));

    await waitFor(() => expect(screen.getByText("Parent group")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mockGroups.update).toHaveBeenCalledOnce());
    expect(mockGroups.removeSubGroup).not.toHaveBeenCalled();
  });
});
