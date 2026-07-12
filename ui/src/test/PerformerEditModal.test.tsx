import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { PerformerEditModal } from "../pages/PerformerEditModal";
import type { Performer } from "../api/types";

const mocks = vi.hoisted(() => ({
  performersUpdate: vi.fn(),
  tagsCreate: vi.fn(),
  tagsFind: vi.fn(),
  performerImageUrl: vi.fn(),
  uploadPerformerImage: vi.fn(),
  deletePerformerImage: vi.fn(),
}));

vi.mock("../api/client", () => ({
  performers: { update: mocks.performersUpdate },
  tags: { create: mocks.tagsCreate, find: mocks.tagsFind },
  entityImages: {
    performerImageUrl: mocks.performerImageUrl,
    uploadPerformerImage: mocks.uploadPerformerImage,
    deletePerformerImage: mocks.deletePerformerImage,
  },
}));

vi.mock("../components/ImageInput", () => ({
  ImageInput: ({ label }: { label: string }) => <div>{label}</div>,
}));

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => <div>Custom Fields Editor</div>,
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

function renderModal(performer: Performer) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <PerformerEditModal performer={performer} open onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("PerformerEditModal", () => {
  beforeEach(() => {
    mocks.performersUpdate.mockResolvedValue({});
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.performerImageUrl.mockReturnValue("/performers/1/image");
    mocks.uploadPerformerImage.mockResolvedValue(undefined);
    mocks.deletePerformerImage.mockResolvedValue(undefined);
  });

  it("uses the shared rating input and omits favorite from the payload", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: true,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    const { container } = renderModal(performer);

    expect(screen.getByText("Rating")).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Favorite" })).not.toBeInTheDocument();
    expect(container.querySelectorAll('button[title="Set rating"]').length).toBe(5);

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.any(Object)));
    expect(mocks.performersUpdate.mock.calls[0][1]).not.toHaveProperty("favorite");
  });

  it("searches tags remotely and adds selected tags to the payload", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    mocks.tagsFind.mockImplementation(async ({ q }: { q?: string }) => ({
      items: q === "sha"
        ? [
          { id: 7, name: "Shaved Pussy" },
          { id: 8, name: "Shared Video" },
        ]
        : [],
    }));

    renderModal(performer);

    await user.type(screen.getByPlaceholderText("Search tags..."), "sha");

    await waitFor(() => {
      expect(mocks.tagsFind).toHaveBeenLastCalledWith({
        q: "sha",
        perPage: 20,
        sort: "name",
        direction: "asc",
      });
    });

    await user.click(await screen.findByRole("option", { name: "Shaved Pussy" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ tagIds: [7] })));
    expect(screen.getByText("Shaved Pussy")).toBeInTheDocument();
  });

  it("creates and selects a metadata-rich tag with the shared keyboard interaction", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.tagsCreate.mockResolvedValue({
      id: 9,
      name: "Novel tag",
      color: "#123456",
      tagGroupName: "Qualities",
      tagGroupColor: "#654321",
    });

    renderModal(performer);

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "Novel tag");
    const createOption = await screen.findByRole("option", { name: "Create “Novel tag”" });
    expect(input).toHaveAttribute("aria-controls", createOption.parentElement?.id);
    await user.keyboard("{ArrowDown}{Enter}");

    await waitFor(() => expect(mocks.tagsCreate).toHaveBeenCalledWith({ name: "Novel tag" }));
    expect(await screen.findByText("Qualities")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ tagIds: [9] })));
  });

  it("saves aliases from separate list inputs", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: ["Old Alias", "Second Alias"],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    renderModal(performer);

    const aliasInputs = screen.getAllByPlaceholderText("Alias");
    expect(aliasInputs).toHaveLength(2);
    expect(aliasInputs[0]).toHaveValue("Old Alias");
    expect(aliasInputs[1]).toHaveValue("Second Alias");

    await user.clear(aliasInputs[0]);
    await user.type(aliasInputs[0], "New Alias");
    await user.click(screen.getByRole("button", { name: /Add Alias/i }));
    await user.type(screen.getAllByPlaceholderText("Alias")[2], "Third Alias");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ aliases: ["New Alias", "Second Alias", "Third Alias"] })));
  });
});
