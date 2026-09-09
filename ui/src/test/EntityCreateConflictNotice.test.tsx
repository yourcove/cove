import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MutationFailureNotice } from "../components/MutationFailureNotice";
import { createAppQueryClient } from "../queryClient";
import { resetMutationFailureForTests } from "../state/mutationFailure";
import { PerformerCreateModal } from "../pages/PerformersPage";
import { StudioCreateModal } from "../pages/StudiosPage";
import { StudioEditModal } from "../pages/StudioEditModal";
import type { Studio } from "../api/types";

const apiMocks = vi.hoisted(() => ({
  createPerformer: vi.fn(),
  createStudio: vi.fn(),
  updateStudio: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    performers: { ...actual.performers, create: apiMocks.createPerformer },
    studios: { ...actual.studios, create: apiMocks.createStudio, update: apiMocks.updateStudio },
  };
});

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => null,
}));
vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceSelector: () => null,
  EntityReferenceMultiSelector: () => null,
}));
vi.mock("../components/RemoteIdsEditor", () => ({
  RemoteIdsEditor: () => null,
  normalizeRemoteIds: () => [],
}));

function renderModal(kind: "performer" | "studio") {
  const queryClient = createAppQueryClient();
  render(
    <QueryClientProvider client={queryClient}>
      <MutationFailureNotice />
      {kind === "performer" ? (
        <PerformerCreateModal open onClose={vi.fn()} onCreated={vi.fn()} />
      ) : (
        <StudioCreateModal open onClose={vi.fn()} onCreated={vi.fn()} />
      )}
    </QueryClientProvider>,
  );
}

describe("entity create conflict feedback", () => {
  beforeEach(() => {
    apiMocks.createPerformer.mockReset();
    apiMocks.createStudio.mockReset();
    apiMocks.updateStudio.mockReset();
  });

  afterEach(() => resetMutationFailureForTests());

  it.each([
    [
      "performer",
      "Existing performer",
      apiMocks.createPerformer,
      'A performer with name "Existing performer" and no disambiguation already exists.',
    ],
    ["studio", "Existing studio", apiMocks.createStudio, 'A studio with name "Existing studio" already exists.'],
  ] as const)("shows the %s conflict inline without the generic global notice", async (kind, name, create, detail) => {
    create.mockRejectedValueOnce(new Error(`API Error 409: ${JSON.stringify({ message: detail })}`));
    renderModal(kind);
    const user = userEvent.setup();

    const nameInput =
      kind === "performer" ? screen.getByPlaceholderText("Performer name") : screen.getAllByRole("textbox")[0];
    await user.type(nameInput, name);
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(detail);
    expect(screen.queryByText("Couldn’t complete the action")).not.toBeInTheDocument();
  });

  it("shows a studio rename conflict inline without the generic global notice", async () => {
    const detail = 'A studio with name "Existing studio" already exists.';
    apiMocks.updateStudio.mockRejectedValueOnce(new Error(`API Error 409: ${JSON.stringify({ message: detail })}`));
    const studio = {
      id: 1,
      name: "Studio being edited",
      favorite: false,
      organized: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      performerCount: 0,
      childStudioCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    } satisfies Studio;
    const queryClient = createAppQueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <MutationFailureNotice />
        <StudioEditModal studio={studio} open onClose={vi.fn()} />
      </QueryClientProvider>,
    );
    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(detail);
    expect(screen.queryByText("Couldn’t complete the action")).not.toBeInTheDocument();
  });
});
