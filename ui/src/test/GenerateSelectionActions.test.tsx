import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { ImageSelectionActions } from "../components/ImageSelectionActions";
import { VideoSelectionActions } from "../components/VideoSelectionActions";

const mocks = vi.hoisted(() => ({
  generate: vi.fn(),
  estimateGenerate: vi.fn(),
  denied: new Set<string>(),
}));

vi.mock("../api/client", () => ({
  metadata: { generate: mocks.generate, estimateGenerate: mocks.estimateGenerate },
  videos: { bulkUpdate: vi.fn(), bulkDelete: vi.fn() },
  images: { bulkUpdate: vi.fn(), bulkDelete: vi.fn() },
  audios: { bulkUpdate: vi.fn(), bulkDelete: vi.fn() },
  texts: { bulkUpdate: vi.fn(), bulkDelete: vi.fn() },
  galleries: {},
  performers: {},
  groups: {},
  studios: {},
  tags: { lookup: vi.fn().mockResolvedValue([]) },
  entityImages: {},
  customFields: { list: vi.fn().mockResolvedValue([]) },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: (permission: string) => !mocks.denied.has(permission) }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { continuePlaylistDefault: false } } }),
  useOptionalAppConfig: () => ({ config: { ui: { continuePlaylistDefault: false } } }),
}));

vi.mock("../state/VideoQueueContext", () => ({
  useVideoQueue: () => ({ setQueue: vi.fn() }),
}));

vi.mock("../components/ExtensionSelectionActions", () => ({
  ExtensionSelectionActions: () => null,
}));

function renderWithClient(ui: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

async function startGenerate(dialogTitle: string) {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Generate" }));
  expect(await screen.findByText(dialogTitle)).toBeInTheDocument();
  // The dialog's submit button shares the action's label; it is the last one rendered.
  const buttons = screen.getAllByRole("button", { name: "Generate" });
  await user.click(buttons[buttons.length - 1]);
  await waitFor(() => expect(mocks.generate).toHaveBeenCalledOnce());
  return mocks.generate.mock.calls[0][0];
}

describe("Generate for selected items", () => {
  beforeEach(() => {
    mocks.generate.mockReset();
    mocks.generate.mockResolvedValue({ jobId: "generate-job" });
    mocks.estimateGenerate.mockReset();
    mocks.estimateGenerate.mockResolvedValue({});
    mocks.denied.clear();
  });

  it("generates for exactly the selected videos from the videos list", async () => {
    renderWithClient(
      <VideoSelectionActions items={[]} selectedIds={new Set([4, 9])} onSelectNone={vi.fn()} onNavigate={vi.fn()} />,
    );

    const request = await startGenerate("Generate for 2 videos");
    expect(request.videoIds).toEqual([4, 9]);
    expect(request.imageIds).toBeUndefined();
    expect(request.thumbnails).toBe(true);
    expect(await screen.findByText("Job started")).toBeInTheDocument();
  });

  it("generates for exactly the selected images from the images list", async () => {
    renderWithClient(<ImageSelectionActions selectedIds={new Set([3, 7, 11])} onSelectNone={vi.fn()} />);

    const request = await startGenerate("Generate for 3 images");
    expect(request.imageIds).toEqual([3, 7, 11]);
    expect(request.videoIds).toBeUndefined();
    expect(request.imageThumbnails).toBe(true);
  });

  it.each([
    ["audios", "audioIds", "Generate for 2 audios"],
    ["texts", "textIds", "Generate for 2 texts"],
    ["videos", "videoIds", "Generate for 2 videos"],
    ["images", "imageIds", "Generate for 2 images"],
  ] as const)("scopes the shared %s selection actions to the selected ids", async (entityType, key, title) => {
    renderWithClient(<BulkSelectionActions entityType={entityType} selectedIds={new Set([5, 8])} onDone={vi.fn()} />);

    const request = await startGenerate(title);
    expect(request[key]).toEqual([5, 8]);
    for (const other of ["videoIds", "imageIds", "audioIds", "textIds"].filter((name) => name !== key)) {
      expect(request[other]).toBeUndefined();
    }
  });

  it("hides Generate without permission to run jobs, as on the detail pages", () => {
    mocks.denied.add("jobs.run");
    renderWithClient(
      <>
        <VideoSelectionActions items={[]} selectedIds={new Set([4])} onSelectNone={vi.fn()} onNavigate={vi.fn()} />
        <ImageSelectionActions selectedIds={new Set([3])} onSelectNone={vi.fn()} />
        <BulkSelectionActions entityType="audios" selectedIds={new Set([5])} onDone={vi.fn()} />
      </>,
    );

    expect(screen.queryByRole("button", { name: "Generate" })).not.toBeInTheDocument();
  });

  it("is not offered for entities Generate cannot target", () => {
    renderWithClient(<BulkSelectionActions entityType="galleries" selectedIds={new Set([5])} onDone={vi.fn()} />);

    expect(screen.queryByRole("button", { name: "Generate" })).not.toBeInTheDocument();
  });
});
