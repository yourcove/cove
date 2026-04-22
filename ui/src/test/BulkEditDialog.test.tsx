import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { BulkEditDialog, GROUP_BULK_FIELDS, SCENE_BULK_FIELDS } from "../components/BulkEditDialog";

const mocks = vi.hoisted(() => ({
  tagsFind: vi.fn(),
  performersFind: vi.fn(),
  studiosFind: vi.fn(),
  studiosGet: vi.fn(),
  groupsFind: vi.fn(),
}));

vi.mock("../api/client", () => ({
  tags: { find: mocks.tagsFind },
  performers: { find: mocks.performersFind },
  studios: { find: mocks.studiosFind, get: mocks.studiosGet },
  groups: { find: mocks.groupsFind },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: {
      ui: {
        ratingSystemOptions: {
          type: "stars",
          starPrecision: "full",
        },
      },
    },
  }),
}));

function renderDialog(dialog: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return render(<QueryClientProvider client={queryClient}>{dialog}</QueryClientProvider>);
}

describe("BulkEditDialog", () => {
  beforeEach(() => {
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Tag One" }] });
    mocks.performersFind.mockResolvedValue({ items: [] });
    mocks.studiosFind.mockResolvedValue({ items: [{ id: 11, name: "Alpha Studio" }] });
    mocks.studiosGet.mockResolvedValue({ id: 11, name: "Alpha Studio" });
    mocks.groupsFind.mockResolvedValue({ items: [{ id: 5, name: "Series One" }] });
  });

  it("posts corrected mode keys and scene group payloads", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();

    renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Scenes"
        selectedCount={2}
        fields={SCENE_BULK_FIELDS}
        onApply={onApply}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Tags" }));
    await user.click(screen.getByRole("button", { name: "Overwrite" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Tag One/i })).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: /Tag One/i }));

    await user.click(screen.getByRole("checkbox", { name: "Groups" }));
    const overwriteButtons = screen.getAllByRole("button", { name: "Overwrite" });
    await user.click(overwriteButtons[1]);
    await waitFor(() => expect(screen.getByRole("button", { name: /Series One/i })).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: /Series One/i }));

    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      tagIds: [1],
      tagMode: "SET",
      groupIds: [{ groupId: 5, sceneIndex: 0 }],
      groupMode: "SET",
    });
  });

  it("loads studio search results and renders the shared rating widget", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    const { container } = renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Groups"
        selectedCount={1}
        fields={GROUP_BULK_FIELDS}
        onApply={onApply}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Rating" }));
    expect(container.querySelectorAll('button[title="Set rating"]').length).toBe(5);

    await user.click(screen.getByRole("checkbox", { name: "Studio" }));
    const searchInput = screen.getByPlaceholderText("Search studios...");
    await user.type(searchInput, "Alpha");
    await waitFor(() => expect(screen.getByRole("button", { name: /Alpha Studio/i })).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: /Alpha Studio/i }));

    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ studioId: 11 });
  });
});