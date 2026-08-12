import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { TagNameConflictScan } from "../api/types";
import { TagNameConflictCleanupPanel } from "../features/tag-name-conflicts/TagNameConflictCleanupPanel";

const mocks = vi.hoisted(() => ({
  resolve: vi.fn(),
  resolveAll: vi.fn(),
  refetch: vi.fn(),
}));

const conflictScan: TagNameConflictScan = {
  unresolvedGroupCount: 1,
  scannedAtUtc: "2026-08-10T00:00:00Z",
  revision: "scan-revision-fixture",
  groups: [{
    key: "namespace-fixture",
    revision: "revision-fixture",
    normalizedName: "Shared",
    kinds: ["name-alias-collision"],
    requiresMerge: false,
    hasCrossTagClaims: true,
    recommendedSurvivorTagId: 4,
    recommendedMergeTagIds: [],
    recommendedRemoveAliasIds: [12],
    claims: [
      { tagId: 4, tagName: "Shared", claimType: "tag-name", aliasId: null, originalValue: " Shared ", normalizedValue: "Shared", recommendedAction: "keep", isRecommendedSurvivingClaim: true },
      { tagId: 9, tagName: "Other", claimType: "alias", aliasId: 12, originalValue: "shared", normalizedValue: "shared", recommendedAction: "remove-alias", isRecommendedSurvivingClaim: false },
    ],
    impacts: [
      { tagId: 4, tagName: "Shared", referenceCount: 11, taggedEntityCount: 2, segmentCount: 3, parentRelationshipCount: 1, childRelationshipCount: 0, ratingCount: 1, otherMetadataCount: 4, extensionMetadataCount: 0, externalReferences: [] },
      { tagId: 9, tagName: "Other", referenceCount: 20, taggedEntityCount: 7, segmentCount: 5, parentRelationshipCount: 0, childRelationshipCount: 2, ratingCount: 0, otherMetadataCount: 6, extensionMetadataCount: 0, externalReferences: [] },
    ],
  }],
};

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    tagNameConflicts: {
      ...actual.tagNameConflicts,
      resolve: mocks.resolve,
      resolveAll: mocks.resolveAll,
    },
  };
});

vi.mock("../features/tag-name-conflicts/useTagNameConflicts", () => ({
  tagNameConflictQueryKey: ["tag-name-conflicts"],
  tagNameConflictSummaryQueryKey: ["tag-name-conflicts", "summary"],
  useTagNameConflictScan: () => ({
    data: conflictScan,
    isLoading: false,
    isFetching: false,
    error: null,
    refetch: mocks.refetch,
  }),
}));

describe("TagNameConflictCleanupPanel", () => {
  beforeEach(() => {
    mocks.resolve.mockReset().mockResolvedValue({
      unresolvedGroupCount: 0,
      scannedAtUtc: "2026-08-10T00:01:00Z",
      revision: "empty-scan-revision",
      groups: [],
    });
    mocks.resolveAll.mockReset();
    mocks.refetch.mockReset();
  });

  it("shows claim kinds and impact counts and submits a non-recommended survivor", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Tag name and alias")).toBeInTheDocument();
    expect(screen.getByText("Tag name")).toBeInTheDocument();
    expect(screen.getByText("Alias")).toBeInTheDocument();
    expect(screen.getByText(/only canonical-name owner/i)).toBeInTheDocument();
    expect(screen.getByText(/recommendation score is 11 transferable references/i)).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Transferable references" })).toBeInTheDocument();
    expect(screen.getByText("7")).toBeInTheDocument();
    expect(screen.getByText("6")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Keep tag Shared" })).toBeChecked();

    await user.click(screen.getByRole("radio", { name: "Keep tag Other" }));
    await user.click(screen.getByRole("button", { name: "Resolve group" }));
    await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);

    await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith("namespace-fixture", "revision-fixture", 9, [
      { tagId: 4, aliasId: null, action: "merge-tag" },
    ], []));
    expect(queryClient.getQueryData(["tag-name-conflicts"])).toEqual(expect.objectContaining({ unresolvedGroupCount: 0 }));
  });

  it("links tag owners from both claims and the impact table", () => {
    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    const sharedLinks = screen.getAllByRole("link", { name: "Open tag Shared (#4) in new tab" });
    const otherLinks = screen.getAllByRole("link", { name: "Open tag Other (#9) in new tab" });
    expect(sharedLinks).toHaveLength(2);
    expect(otherLinks).toHaveLength(2);
    for (const [links, href] of [[sharedLinks, "/tag/4"], [otherLinks, "/tag/9"]] as const) {
      for (const link of links) expect(link).toHaveAttribute("href", href);
    }
    for (const link of [...sharedLinks, ...otherLinks]) {
      expect(link).toHaveAttribute("target", "_blank");
      expect(link).toHaveAttribute("rel", "noreferrer");
      expect(link).toHaveAttribute("title", "Open tag in new tab");
    }
  });

  it("adopts a refreshed recommendation until the administrator selects an override", () => {
    const queryClient = new QueryClient();
    const view = render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    expect(screen.getByRole("radio", { name: "Keep tag Shared" })).toBeChecked();
    conflictScan.groups[0].recommendedSurvivorTagId = 9;

    try {
      view.rerender(
        <QueryClientProvider client={queryClient}>
          <TagNameConflictCleanupPanel />
        </QueryClientProvider>,
      );
      expect(screen.getByRole("radio", { name: "Keep tag Other" })).toBeChecked();
    } finally {
      conflictScan.groups[0].recommendedSurvivorTagId = 4;
    }
  });

  it("keeps every claim on a source tag synchronized when that whole tag will merge", async () => {
    const user = userEvent.setup();
    const group = conflictScan.groups[0];
    const original = {
      claims: group.claims,
      requiresMerge: group.requiresMerge,
      recommendedMergeTagIds: group.recommendedMergeTagIds,
      recommendedRemoveAliasIds: group.recommendedRemoveAliasIds,
    };
    group.claims = [
      original.claims[0],
      { tagId: 9, tagName: "Other", claimType: "tag-name", aliasId: null, originalValue: "shared", normalizedValue: "shared", recommendedAction: "merge-tag", isRecommendedSurvivingClaim: false },
      { ...original.claims[1], recommendedAction: "merge-tag" },
    ];
    group.requiresMerge = true;
    group.recommendedMergeTagIds = [9];
    group.recommendedRemoveAliasIds = [];

    try {
      const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
      render(
        <QueryClientProvider client={queryClient}>
          <TagNameConflictCleanupPanel />
        </QueryClientProvider>,
      );

      const canonicalAction = screen.getByRole("combobox", { name: "Resolution for tag-name shared on tag 9" });
      const aliasAction = screen.getByRole("combobox", { name: "Resolution for alias shared on tag 9" });
      expect(canonicalAction).toHaveValue("merge-tag");
      expect(aliasAction).toHaveValue("merge-tag");

      await user.selectOptions(aliasAction, "remove-alias");
      expect(aliasAction).toHaveValue("remove-alias");
      expect(canonicalAction).toHaveValue("rename");

      await user.selectOptions(aliasAction, "merge-tag");
      expect(aliasAction).toHaveValue("merge-tag");
      expect(canonicalAction).toHaveValue("merge-tag");

      await user.click(screen.getByRole("button", { name: "Resolve group" }));
      await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);
      await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith("namespace-fixture", "revision-fixture", 4, [
        { tagId: 9, aliasId: null, action: "merge-tag" },
        { tagId: 9, aliasId: 12, action: "merge-tag" },
      ], []));
    } finally {
      group.claims = original.claims;
      group.requiresMerge = original.requiresMerge;
      group.recommendedMergeTagIds = original.recommendedMergeTagIds;
      group.recommendedRemoveAliasIds = original.recommendedRemoveAliasIds;
    }
  });

  it("shows each non-core table and requires an explicit database action before an optional merge", async () => {
    const user = userEvent.setup();
    conflictScan.groups[0].impacts[1].extensionMetadataCount = 3;
    conflictScan.groups[0].impacts[1].externalReferences = [{
      tagId: 9,
      referenceKey: "foreign-key-fixture",
      schemaName: "public",
      tableName: "extension_segment_links",
      columnName: "tag_id",
      deleteBehavior: "restrict",
      rowCount: 3,
      accessLimitation: null,
    }];
    try {
      const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
      render(
        <QueryClientProvider client={queryClient}>
          <TagNameConflictCleanupPanel />
        </QueryClientProvider>,
      );

      expect(screen.getByRole("button", { name: "Resolve group" })).toBeEnabled();
      expect(screen.getByRole("button", { name: "Apply all recommended fixes" })).toBeEnabled();

      await user.selectOptions(
        screen.getByRole("combobox", { name: "Resolution for alias shared on tag 9" }),
        "merge-tag",
      );

      expect(screen.getByText(/choose whether to update or delete all 3 non-core references/i)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Resolve group" })).toBeDisabled();
      expect(screen.getByText("public.extension_segment_links")).toBeInTheDocument();
      expect(screen.getByText("tag_id")).toBeInTheDocument();
      expect(screen.getByText("restrict")).toBeInTheDocument();
      const otherTagLinks = screen.getAllByRole("link", { name: "Open tag Other (#9) in new tab" });
      expect(otherTagLinks).toHaveLength(3);
      for (const link of otherTagLinks) expect(link).toHaveAttribute("href", "/tag/9");

      await user.selectOptions(
        screen.getByRole("combobox", { name: "Database action for public.extension_segment_links.tag_id on tag 9" }),
        "update-to-survivor",
      );
      expect(screen.getByRole("button", { name: "Resolve group" })).toBeEnabled();
      await user.click(screen.getByRole("button", { name: "Resolve group" }));
      expect(screen.getByText(/update 3 non-core row references to the survivor/i)).toBeInTheDocument();
      await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);

      await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith("namespace-fixture", "revision-fixture", 4, [
        { tagId: 9, aliasId: 12, action: "merge-tag" },
      ], [{
        tagId: 9,
        referenceKey: "foreign-key-fixture",
        action: "update-to-survivor",
      }]));
    } finally {
      conflictScan.groups[0].impacts[1].extensionMetadataCount = 0;
      conflictScan.groups[0].impacts[1].externalReferences = [];
    }
  });

  it("blocks a merge when database access prevents an exact non-core inventory", async () => {
    const user = userEvent.setup();
    conflictScan.groups[0].impacts[1].externalReferences = [{
      tagId: 9,
      referenceKey: "foreign-key-restricted-fixture",
      schemaName: "private_extension",
      tableName: "tag_links",
      columnName: "tag_id",
      deleteBehavior: "cascade",
      rowCount: null,
      accessLimitation: "row-level-security",
    }];
    try {
      const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
      render(
        <QueryClientProvider client={queryClient}>
          <TagNameConflictCleanupPanel />
        </QueryClientProvider>,
      );

      await user.selectOptions(
        screen.getByRole("combobox", { name: "Resolution for alias shared on tag 9" }),
        "merge-tag",
      );

      expect(screen.getByText(/cannot verify or change those rows/i)).toBeInTheDocument();
      expect(screen.getAllByText("Unknown").length).toBeGreaterThan(0);
      expect(screen.getByText("Use extension or database administrator")).toBeInTheDocument();
      expect(screen.queryByRole("combobox", { name: /Database action for private_extension/ })).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Resolve group" })).toBeDisabled();
    } finally {
      conflictScan.groups[0].impacts[1].externalReferences = [];
    }
  });

  it("submits alias removal as the recommended non-merge fix", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    expect(screen.getByRole("combobox", { name: "Resolution for alias shared on tag 9" })).toHaveValue("remove-alias");
    await user.click(screen.getByRole("button", { name: "Resolve group" }));
    await user.click(screen.getAllByRole("button", { name: "Resolve group" })[1]);

    await waitFor(() => expect(mocks.resolve).toHaveBeenCalledWith("namespace-fixture", "revision-fixture", 4, [
      { tagId: 9, aliasId: 12, action: "remove-alias" },
    ], []));
  });

  it("submits the confirmed scan revision when applying all recommended fixes", async () => {
    const user = userEvent.setup();
    mocks.resolveAll.mockResolvedValue({
      unresolvedGroupCount: 0,
      scannedAtUtc: "2026-08-10T00:01:00Z",
      revision: "empty-scan-revision",
      groups: [],
    });
    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Apply all recommended fixes" }));
    await user.click(screen.getByRole("button", { name: "Apply all" }));

    await waitFor(() => expect(mocks.resolveAll).toHaveBeenCalledWith("scan-revision-fixture"));
  });

  it("does not let Apply all discard a manually selected survivor", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("radio", { name: "Keep tag Other" }));

    expect(screen.getByRole("button", { name: "Apply all recommended fixes" })).toBeDisabled();
    expect(screen.getByText(/Apply all uses Cove's recommendations, not choices edited/i)).toBeInTheDocument();
    expect(mocks.resolveAll).not.toHaveBeenCalled();
  });

  it("clears stale per-claim choices before manually refreshing the scan", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <TagNameConflictCleanupPanel />
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("radio", { name: "Keep tag Other" }));
    await user.selectOptions(screen.getByRole("combobox", { name: "Resolution for tag-name Shared on tag 4" }), "rename");
    expect(screen.getByRole("button", { name: "Apply all recommended fixes" })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Refresh scan" }));

    expect(mocks.refetch).toHaveBeenCalledOnce();
    expect(screen.getByRole("button", { name: "Apply all recommended fixes" })).toBeEnabled();
  });
});
