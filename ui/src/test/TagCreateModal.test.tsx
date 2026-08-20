import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TagCreateModal } from "../pages/TagsPage";

const mocks = vi.hoisted(() => ({
  tagsCreate: vi.fn(),
  tagGroupsList: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    tags: { ...actual.tags, create: mocks.tagsCreate },
    tagGroups: { ...actual.tagGroups, list: mocks.tagGroupsList },
  };
});

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: () => <div>Parent tag selector</div>,
}));

vi.mock("../components/shared", () => ({
  CustomFieldsEditor: () => <div>Custom Fields Editor</div>,
}));

function renderModal() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <TagCreateModal open onClose={vi.fn()} onCreated={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("TagCreateModal", () => {
  beforeEach(() => {
    mocks.tagsCreate.mockReset();
    mocks.tagGroupsList.mockResolvedValue([]);
  });

  it("shows a server error in the dialog when tag creation fails", async () => {
    const user = userEvent.setup();
    mocks.tagsCreate.mockRejectedValue(new Error("API Error 409: {\"message\":\"Tag 'Existing' already exists\"}"));

    renderModal();

    await user.type(screen.getAllByRole("textbox")[0], "Existing");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Tag 'Existing' already exists");
    expect(screen.getByRole("alert")).not.toHaveTextContent("API Error 409");
    expect(screen.getByRole("alert")).not.toHaveTextContent('{"message"');
    expect(screen.getByRole("heading", { name: "Create Tag" })).toBeInTheDocument();
  });
});
