import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { PerformerEditModal } from "../pages/PerformerEditModal";
import type { Performer } from "../api/types";

const mocks = vi.hoisted(() => ({
  performersUpdate: vi.fn(),
  tagsFind: vi.fn(),
  performerImageUrl: vi.fn(),
  uploadPerformerImage: vi.fn(),
  deletePerformerImage: vi.fn(),
}));

vi.mock("../api/client", () => ({
  performers: { update: mocks.performersUpdate },
  tags: { find: mocks.tagsFind },
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
      ignoreAutoTag: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      sceneCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
      rating: 80,
    };

    const { container } = renderModal(performer);

    expect(screen.getByText("Rating")).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Favorite" })).not.toBeInTheDocument();
    expect(container.querySelectorAll('button[title="Set rating"]').length).toBe(5);

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.any(Object)));
    expect(mocks.performersUpdate.mock.calls[0][1]).not.toHaveProperty("favorite");
  });
});