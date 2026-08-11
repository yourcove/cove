import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { EntityNameConflictScan } from "../api/types";
import { EntityNameConflictCleanupPanel } from "../features/tag-name-conflicts/EntityNameConflictCleanupPanel";

const mocks = vi.hoisted(() => ({
  resolve: vi.fn(),
  resolveAll: vi.fn(),
  refetch: vi.fn(),
  scanError: null as Error | null,
}));

const conflictScan: EntityNameConflictScan = {
  entityType: "performer",
  unresolvedGroupCount: 1,
  scannedAtUtc: "2026-08-11T00:00:00Z",
  revision: "performer-scan-revision",
  groups: [{
    entityType: "performer",
    key: "performer-identity-fixture",
    revision: "performer-group-revision",
    normalizedName: "alex",
    normalizedDisambiguation: "example",
    recommendedSurvivorEntityId: 7,
    recommendedMergeEntityIds: [3],
    candidates: [
      { entityId: 3, name: " Alex ", disambiguation: " Example ", normalizedName: "Alex", normalizedDisambiguation: "Example", recommendedAction: "merge-entity", isRecommendedSurvivor: false },
      { entityId: 7, name: "alex", disambiguation: "example", normalizedName: "alex", normalizedDisambiguation: "example", recommendedAction: "keep", isRecommendedSurvivor: true },
    ],
    impacts: [
      { entityId: 3, name: " Alex ", disambiguation: " Example ", linkedEntityCount: 2, groupCount: 0, hierarchyCount: 0, faceCount: 1, ratingCount: 0, otherMetadataCount: 1, extensionMetadataCount: 0, externalReferences: [], referenceCount: 4 },
      { entityId: 7, name: "alex", disambiguation: "example", linkedEntityCount: 8, groupCount: 1, hierarchyCount: 0, faceCount: 3, ratingCount: 1, otherMetadataCount: 2, extensionMetadataCount: 0, externalReferences: [], referenceCount: 15 },
    ],
  }],
};

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    entityNameConflicts: {
      ...actual.entityNameConflicts,
      resolve: mocks.resolve,
      resolveAll: mocks.resolveAll,
    },
  };
});

vi.mock("../features/tag-name-conflicts/useTagNameConflicts", () => ({
  entityNameConflictQueryKey: (entityType: string) => ["entity-name-conflicts", entityType],
  tagNameConflictSummaryQueryKey: ["tag-name-conflicts", "summary"],
  useEntityNameConflictScan: () => ({
    data: conflictScan,
    isLoading: false,
    isFetching: false,
    error: mocks.scanError,
    refetch: mocks.refetch,
  }),
}));

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <EntityNameConflictCleanupPanel entityType="performer" />
    </QueryClientProvider>,
  );
  return queryClient;
}

describe("EntityNameConflictCleanupPanel", () => {
  beforeEach(() => {
    mocks.resolve.mockReset().mockResolvedValue({ ...conflictScan, unresolvedGroupCount: 0, revision: "empty", groups: [] });
    mocks.resolveAll.mockReset().mockResolvedValue({ ...conflictScan, unresolvedGroupCount: 0, revision: "empty", groups: [] });
    mocks.refetch.mockReset();
    mocks.scanError = null;
    conflictScan.groups[0].impacts[0].externalReferences = [];
    conflictScan.groups[0].impacts[0].extensionMetadataCount = 0;
  });

  it("allows a failed scan to be retried in place", async () => {
    const user = userEvent.setup();
    mocks.scanError = new Error("Temporary scan failure");
    renderPanel();

    expect(screen.getByText(/temporary scan failure/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /retry scan/i }));

    expect(mocks.refetch).toHaveBeenCalledOnce();
  });

  it("recommends the most-referenced performer and submits a selected alternative", async () => {
    const user = userEvent.setup();
    const queryClient = renderPanel();

    expect(screen.getByText(/name plus disambiguation pair unique/i)).toBeInTheDocument();
    expect(screen.getByText(/most transferred references/i)).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Keep alex" })).toBeChecked();
    expect(screen.getByRole("columnheader", { name: "Faces" })).toBeInTheDocument();

    await user.click(screen.getByRole("radio", { name: "Keep Alex" }));
    await user.click(screen.getByRole("button", { name: "Resolve group" }));
    await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);

    await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith(
      "performer",
      "performer-identity-fixture",
      "performer-group-revision",
      3,
      [
        { entityId: 3, action: "keep" },
        { entityId: 7, action: "merge-entity" },
      ],
      [],
    ));
    expect(queryClient.getQueryData(["entity-name-conflicts", "performer"])).toEqual(expect.objectContaining({ unresolvedGroupCount: 0 }));
  });

  it("supports keeping both performers by changing name and disambiguation", async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.selectOptions(screen.getByRole("combobox", { name: "Action for Alex" }), "rename");
    await user.type(screen.getByRole("textbox", { name: "New name for Alex" }), "Alex Alternate");
    await user.type(screen.getByRole("textbox", { name: "New disambiguation for Alex" }), "Second identity");
    await user.click(screen.getByRole("button", { name: "Resolve group" }));
    await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);

    await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith(
      "performer",
      "performer-identity-fixture",
      "performer-group-revision",
      7,
      [
        { entityId: 3, action: "rename", newName: "Alex Alternate", newDisambiguation: "Second identity" },
        { entityId: 7, action: "keep" },
      ],
      [],
    ));
  });

  it("requires an explicit update-or-delete choice for extension references", async () => {
    const user = userEvent.setup();
    conflictScan.groups[0].impacts[0].extensionMetadataCount = 2;
    conflictScan.groups[0].impacts[0].externalReferences = [{
      entityId: 3,
      referenceKey: "extension-performer-fk",
      schemaName: "public",
      tableName: "extension_faces",
      columnName: "performer_id",
      deleteBehavior: "cascade",
      rowCount: 2,
      accessLimitation: null,
    }];
    renderPanel();

    expect(screen.getByRole("button", { name: "Apply all recommended merges" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Resolve group" })).toBeDisabled();
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Database action for public.extension_faces.performer_id" }),
      "update-to-survivor",
    );
    expect(screen.getByRole("button", { name: "Resolve group" })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: "Resolve group" }));
    expect(screen.getByText(/update 2 extension row references/i)).toBeInTheDocument();
  });
});
