import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { TagsPage } from "../pages/TagsPage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    tagGroups: { ...actual.tagGroups, list: vi.fn().mockResolvedValue([]) },
  };
});

vi.mock("../components/ListPage", () => ({
  ListPage: ({ onNew }: { onNew?: () => void }) => <button onClick={onNew}>+ New</button>,
}));

vi.mock("../hooks/useListUrlState", () => ({
  useListUrlState: () => ({
    filter: { page: 1, perPage: 40, q: "  New tag  " },
    setFilter: vi.fn(),
    objectFilter: {},
    setObjectFilter: vi.fn(),
    displayMode: "grid",
    setDisplayMode: vi.fn(),
  }),
}));

vi.mock("../hooks/useInfiniteListData", () => ({
  useInfiniteListData: () => ({
    items: [],
    totalCount: 0,
    isLoading: false,
    loadError: null,
    refetch: vi.fn(),
    infiniteFilterKey: "tags",
    infinitePageSize: false,
    fetchAllIds: vi.fn(),
    infiniteQuery: { hasNextPage: false, isFetchingNextPage: false },
    infiniteScroll: undefined,
    loadMore: vi.fn(),
  }),
}));

vi.mock("../hooks/useMultiSelect", () => ({
  toggleOptionsFromEvent: () => ({}),
  useMultiSelect: () => ({
    selectedIds: new Set(),
    toggle: vi.fn(),
    selectAll: vi.fn(),
    selectIds: vi.fn(),
    selectNone: vi.fn(),
    invertSelection: vi.fn(),
  }),
}));

vi.mock("../hooks/useEntityEngagementBatch", () => ({
  useEntityEngagementBatch: () => ({ engagementById: new Map() }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: vi.fn() }),
}));

vi.mock("../auth/visibility", () => ({
  canDeleteEntity: () => true,
  canWriteEntity: () => true,
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: () => null,
}));

vi.mock("../components/shared", () => ({
  CustomFieldsEditor: () => null,
}));

describe("TagsPage", () => {
  it("prefills the create form from the trimmed active search", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={queryClient}>
        <TagsPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "+ New" }));

    expect(screen.getAllByRole("textbox")[0]).toHaveValue("New tag");
  });
});
