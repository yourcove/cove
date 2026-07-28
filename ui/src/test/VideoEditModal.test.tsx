import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { VideoEditModal } from "../pages/VideoEditModal";
import type { Video } from "../api/types";

const mocks = vi.hoisted(() => ({
  videosUpdate: vi.fn(),
  tagApplicationsCreate: vi.fn(),
  tagApplicationsDelete: vi.fn(),
}));

vi.mock("../api/client", () => ({
  videos: { update: mocks.videosUpdate },
  tagApplications: {
    create: mocks.tagApplicationsCreate,
    delete: mocks.tagApplicationsDelete,
  },
}));

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => <div>Custom Fields Editor</div>,
}));

vi.mock("../components/StudioSelector", () => ({
  StudioSelector: () => <div>Studio Selector</div>,
}));

vi.mock("../components/RemoteIdsEditor", () => ({
  RemoteIdsEditor: () => <div>Remote IDs Editor</div>,
  normalizeRemoteIds: (remoteIds: unknown) => remoteIds,
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: ({ entityType }: { entityType: string }) => <div>{entityType} selector</div>,
  EntityReferenceValue: ({ value }: { value: number }) => <span>{value}</span>,
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

function renderModal(video: Video) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <VideoEditModal video={video} open onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("VideoEditModal", () => {
  beforeEach(() => {
    mocks.videosUpdate.mockResolvedValue({});
    mocks.tagApplicationsCreate.mockResolvedValue({});
    mocks.tagApplicationsDelete.mockResolvedValue(undefined);
  });

  it("includes edited captions in the save payload", async () => {
    const user = userEvent.setup();
    const video: Video = {
      id: 42,
      title: "Sample Video",
      code: "SCN-42",
      details: "Existing details",
      captions: "English",
      director: "Director",
      date: "2026-05-01",
      organized: true,
      urls: [],
      tags: [],
      performers: [],
      files: [],
      groups: [],
      galleries: [],
      remoteIds: [],
      createdAt: "2026-05-01T00:00:00Z",
      updatedAt: "2026-05-02T00:00:00Z",
    };

    renderModal(video);

    const captionsInput = screen.getByPlaceholderText("Subtitle languages or notes");
    await user.clear(captionsInput);
    await user.type(captionsInput, "English, French");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.videosUpdate).toHaveBeenCalledWith(42, expect.objectContaining({ captions: "English, French" })));
    expect(mocks.tagApplicationsCreate).not.toHaveBeenCalled();
    expect(mocks.tagApplicationsDelete).not.toHaveBeenCalled();
  });

  it("marks a cleared date for removal", async () => {
    const user = userEvent.setup();
    const video: Video = {
      id: 43,
      title: "Sample Video",
      date: "2026-05-01",
      studioId: 9,
      organized: true,
      urls: [],
      tags: [],
      performers: [],
      files: [],
      groups: [],
      galleries: [],
      remoteIds: [],
      createdAt: "2026-05-01T00:00:00Z",
      updatedAt: "2026-05-02T00:00:00Z",
    };

    renderModal(video);

    await user.clear(screen.getByDisplayValue("2026-05-01"));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.videosUpdate).toHaveBeenCalledWith(43, expect.objectContaining({
      clearFields: ["date"],
    })));
  });
});
