import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { performers, studios } from "../api/client";
import { BulkSelectionActions } from "../components/BulkSelectionActions";

const permissions = vi.hoisted(() => ({ denied: "" }));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: (permission: string) => permission !== permissions.denied }),
}));

vi.mock("../components/ExtensionSelectionActions", () => ({
  ExtensionSelectionActions: () => null,
}));

vi.mock("../components/VideoMergeEditor", () => ({
  VideoMergeEditor: ({ targetId, sourceIds }: { targetId: number; sourceIds: number[] }) => (
    <div role="dialog" aria-label="Review video merge">{`${targetId}:${sourceIds.join(",")}`}</div>
  ),
}));

describe("nested video selection actions", () => {
  beforeEach(() => {
    permissions.denied = "";
  });

  it("opens the same merge review used by the main video list", async () => {
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient()}>
        <BulkSelectionActions
          entityType="videos"
          selectedIds={new Set([4, 9])}
          videoItems={[
            { id: 4, title: "First", updatedAt: "", urls: [], files: [] },
            { id: 9, title: "Second", updatedAt: "", urls: [], files: [] },
          ]}
          onDone={vi.fn()}
        />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Merge" }));
    expect(screen.getByText("Merge Videos")).toBeInTheDocument();
    expect(screen.getAllByText("First").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Second").length).toBeGreaterThan(0);
    await user.click(screen.getByRole("button", { name: "Compare metadata" }));
    expect(screen.getByRole("dialog", { name: "Review video merge" })).toHaveTextContent("4:9");
  });

  it("requires delete permission because merging removes the source video", () => {
    permissions.denied = "videos.delete";
    render(
      <QueryClientProvider client={new QueryClient()}>
        <BulkSelectionActions entityType="videos" selectedIds={new Set([4, 9])} onDone={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(screen.queryByRole("button", { name: "Merge" })).not.toBeInTheDocument();
  });
});

describe.each([
  { entityType: "performers" as const, label: "Performers", resource: "performer", api: performers },
  { entityType: "studios" as const, label: "Studios", resource: "studio", api: studios },
])("$entityType selection actions", ({ entityType, label, resource, api }) => {
  afterEach(() => {
    vi.restoreAllMocks();
    permissions.denied = "";
  });

  it("offers merge and shows the selected entities in the destination picker", async () => {
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient()}>
        <BulkSelectionActions
          entityType={entityType}
          selectedIds={new Set([4, 9])}
          mergeItems={[
            { id: 4, name: "First" },
            { id: 9, name: "Second" },
          ]}
          onDone={vi.fn()}
        />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Merge" }));
    expect(screen.getByText(`Merge ${label}`)).toBeInTheDocument();
    expect(screen.getAllByText("First").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Second").length).toBeGreaterThan(0);
  });

  it("merges the chosen source and refreshes related lists", async () => {
    const user = userEvent.setup();
    const onDone = vi.fn();
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const merge = vi.spyOn(api, "merge").mockResolvedValue({} as never);
    render(
      <QueryClientProvider client={queryClient}>
        <BulkSelectionActions
          entityType={entityType}
          selectedIds={new Set([4, 9])}
          mergeItems={[
            { id: 4, name: "First" },
            { id: 9, name: "Second" },
          ]}
          onDone={onDone}
        />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Merge" }));
    await user.click(screen.getByRole("button", { name: `Merge & remove 1 ${resource}` }));
    await waitFor(() => expect(onDone).toHaveBeenCalledOnce());
    expect(merge).toHaveBeenCalledWith(4, [9]);
    expect(invalidate).toHaveBeenCalled();
  });

  it("hides merge without delete permission", () => {
    permissions.denied = `${entityType}.delete`;
    render(
      <QueryClientProvider client={new QueryClient()}>
        <BulkSelectionActions entityType={entityType} selectedIds={new Set([4, 9])} onDone={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(screen.queryByRole("button", { name: "Merge" })).not.toBeInTheDocument();
  });

  it("keeps selected entities whose names have not loaded in the destination picker", async () => {
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient()}>
        <BulkSelectionActions
          entityType={entityType}
          selectedIds={new Set([4, 9])}
          mergeItems={[{ id: 4, name: "First" }]}
          onDone={vi.fn()}
        />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Merge" }));
    expect(screen.getAllByText(`${resource.charAt(0).toUpperCase() + resource.slice(1)} 9`).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("radio")).toHaveLength(2);
  });
});
