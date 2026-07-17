import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Eye, Film, Fingerprint, Images, Link2, Merge, MoreVertical, Pencil, Save, Search, Sparkles, Trash2, UserPlus } from "lucide-react";
import { faces, performers } from "../api/client";
import type { Face, FaceAppearance, FaceDeleteImpact, FaceSimilar, FaceSuggestion, FindFilter, PaginatedResponse, Performer } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { ListQueryState } from "../components/ListQueryState";
import { FaceSuggestionsPanel } from "../components/FaceSuggestionsPanel";
import { FaceCompareDialog, readReferenceLinkInfo } from "../components/FaceCompareDialog";
import { buildFaceCarouselSampleImageUrls, buildFaceHeroImageUrls } from "../components/faceComparisonImages";
import { faceDisplayName } from "../utils/faceDisplay";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { EditModal } from "../components/EditModal";
import { EntityHeroLayout, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { FaceAppearanceTile, FaceTile } from "../components/EntityCards";
import { FieldProvenanceHover, formatDate } from "../components/shared";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import type { DetailListDisplayMode } from "../components/DetailListToolbar";
import { getLoadError } from "../utils/queryLoadState";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type FaceTab = "overview" | "appearances" | "similar";
type FaceAppearanceListItem = FaceAppearance & { id: string | number };

const EMPTY_APPEARANCES_PAGE: PaginatedResponse<FaceAppearanceListItem> = { items: [], totalCount: 0, page: 1, perPage: 24 };
const EMPTY_SIMILAR_PAGE: PaginatedResponse<FaceSimilar> = { items: [], totalCount: 0, page: 1, perPage: 18 };
const APPEARANCE_SORT_OPTIONS = [
  { value: "last_seen", label: "Last Seen" },
  { value: "first_seen", label: "First Seen" },
  { value: "sample_count", label: "Frame Samples" },
  { value: "confidence", label: "Confidence" },
  { value: "host_type", label: "Host Type" },
  { value: "title", label: "Title" },
];
const SIMILAR_SORT_OPTIONS = [
  { value: "distance", label: "Closest Match" },
  { value: "appearance_count", label: "Most Appearances" },
  { value: "video_count", label: "Most Videos" },
  { value: "image_count", label: "Most Images" },
  { value: "updated_at", label: "Recently Updated" },
  { value: "label", label: "Name" },
];

function readSuggestionPerformerId(value: number | FaceSuggestion) {
  return typeof value === "number" ? value : value.performerId;
}

// A suggestion is "conflicting" when it shares a conflict group with another suggestion that points
// at a different performer — i.e. two reference (SAIE) packs disagree about who this face is. Only
// then should accepting open the compare dialog so the user can choose between / merge them. Every
// other accept just links directly.
function hasConflictingMatch(suggestion: FaceSuggestion, allSuggestions: readonly FaceSuggestion[]) {
  const groupId = suggestion.conflictGroupId;
  if (!groupId) return false;
  return allSuggestions.some((other) =>
    other !== suggestion
    && other.conflictGroupId === groupId
    && other.performerId !== suggestion.performerId);
}

export function FaceDetailPage({ id, onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { hasPermission, user } = useAuth();
  const canWriteFace = canWriteEntity("face", hasPermission);
  const canDeleteFace = canDeleteEntity("face", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canEngageFace = canReadEntity("face", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const { backLabel, goBack } = useBackNavigation({ page: "faces" }, onNavigate);
  const [appearanceFilter, setAppearanceFilter] = useState<FindFilter>({ page: 1, perPage: 24, sort: "last_seen", direction: "desc" });
  const [similarFilter, setSimilarFilter] = useState<FindFilter>({ page: 1, perPage: 18, sort: "distance", direction: "asc" });
  const [appearanceZoomLevel, setAppearanceZoomLevel] = useState(0);
  const [similarZoomLevel, setSimilarZoomLevel] = useState(0);
  const [appearanceDisplayMode, setAppearanceDisplayMode] = useState<"grid" | "list">("grid");
  const [similarDisplayMode, setSimilarDisplayMode] = useState<"grid" | "list">("grid");
  const [showActionsMenu, setShowActionsMenu] = useState(false);
  const actionsMenuRef = useRef<HTMLDivElement | null>(null);

  const { data: face, isLoading } = useQuery({
    queryKey: ["face", id],
    queryFn: () => faces.get(id),
  });

  const { data: similarFacesPage = EMPTY_SIMILAR_PAGE, isLoading: similarLoading, loadError: similarLoadError, retry: retrySimilar, infinitePageSize: similarInfinitePageSize, infiniteQuery: similarInfiniteQuery, loadMore: loadMoreSimilar } = useDetailListQuery<FaceSimilar>({
    queryKey: ["face", id, "similar"],
    filter: similarFilter,
    queryFn: (nextFilter) => faces.similar(id, {
      q: nextFilter.q?.trim() || undefined,
      sort: nextFilter.sort,
      direction: nextFilter.direction,
      page: nextFilter.page ?? 1,
      perPage: nextFilter.perPage ?? 18,
      k: 250,
    }),
  });

  const { data: faceAppearancesPage = EMPTY_APPEARANCES_PAGE, isLoading: appearancesLoading, loadError: appearancesLoadError, retry: retryAppearances, infinitePageSize: appearancesInfinitePageSize, infiniteQuery: appearancesInfiniteQuery, loadMore: loadMoreAppearances } = useDetailListQuery<FaceAppearanceListItem>({
    queryKey: ["face", id, "appearances"],
    filter: appearanceFilter,
    queryFn: async (nextFilter) => {
      const page = await faces.appearances(id, {
        q: nextFilter.q?.trim() || undefined,
        sort: nextFilter.sort,
        direction: nextFilter.direction,
        page: nextFilter.page ?? 1,
        perPage: nextFilter.perPage ?? 24,
      });

      return { ...page, items: page.items.map((appearance) => ({ ...appearance, id: appearance.appearanceId })) };
    },
  });

  const { data: deleteImpact, isLoading: deleteImpactLoading } = useQuery({
    queryKey: ["face", id, "delete-impact"],
    queryFn: () => faces.deleteImpact(id),
    enabled: canDeleteFace,
  });

  const { data: faceSuggestionsData, isLoading: suggestionsLoading, error: suggestionsError, refetch: retrySuggestions } = useQuery({
    queryKey: ["face", id, "suggestions"],
    queryFn: () => faces.suggestions(id),
    enabled: canWriteFace && face != null && face.performerId == null,
  });
  const suggestionsLoadError = getLoadError(faceSuggestionsData, suggestionsError);
  const faceSuggestions = faceSuggestionsData ?? [];
  const { data: faceDetectionsData, error: faceDetectionsError, refetch: retryFaceDetections } = useQuery({
    queryKey: ["face", id, "detections"],
    queryFn: () => faces.detections(id),
    enabled: face != null,
  });
  const faceDetectionsLoadError = getLoadError(faceDetectionsData, faceDetectionsError);
  const faceDetections = faceDetectionsData ?? [];

  const [label, setLabel] = useState("");
  const [performerSearch, setPerformerSearch] = useState("");
  const [mergeSearch, setMergeSearch] = useState("");
  const [activeTab, setActiveTab] = useState<FaceTab>("overview");
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [isMergeModalOpen, setIsMergeModalOpen] = useState(false);
  const [isCreatePerformerModalOpen, setIsCreatePerformerModalOpen] = useState(false);
  const [isDeleteDialogOpen, setIsDeleteDialogOpen] = useState(false);
  const [comparingSuggestion, setComparingSuggestion] = useState<FaceSuggestion | null>(null);
  const [newPerformerName, setNewPerformerName] = useState("");
  const [setNewPerformerImage, setSetNewPerformerImage] = useState(true);
  const labelInputRef = useRef<HTMLInputElement | null>(null);
  const mergeInputRef = useRef<HTMLInputElement | null>(null);

  const {
    favorite: faceFavorite,
    setFavorite: setFaceFavorite,
    favoritePending: faceFavoritePending,
  } = useEntityEngagement("face", id, {
    enabled: canEngageFace,
  });

  useEffect(() => {
    if (!face) {
      return;
    }

    setLabel(face.label ?? "");
  }, [face]);

  useEffect(() => {
    if (!isEditModalOpen) {
      return;
    }

    window.setTimeout(() => labelInputRef.current?.focus(), 0);
  }, [isEditModalOpen]);

  useEffect(() => {
    if (!isMergeModalOpen) {
      return;
    }

    window.setTimeout(() => mergeInputRef.current?.focus(), 0);
  }, [isMergeModalOpen]);

  useEffect(() => {
    if (!isCreatePerformerModalOpen) {
      return;
    }

    setNewPerformerName((current) => current.trim() || face?.label?.trim() || "");
  }, [face?.label, isCreatePerformerModalOpen]);

  useEffect(() => {
    if (!showActionsMenu) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (!actionsMenuRef.current?.contains(event.target as Node)) {
        setShowActionsMenu(false);
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [showActionsMenu]);

  const performerSearchTerm = performerSearch.trim();
  const mergeSearchTerm = mergeSearch.trim();
  const normalizedNewPerformerName = newPerformerName.trim();

  const performerMatchesQuery = useQuery({
    queryKey: ["face", id, "performer-search", performerSearchTerm],
    queryFn: () => performers.find({ q: performerSearchTerm, page: 1, perPage: 6 }),
    enabled: canWriteFace && performerSearchTerm.length >= 2,
  });

  const mergeMatchesQuery = useQuery({
    queryKey: ["face", id, "merge-search", mergeSearchTerm],
    queryFn: () => faces.list({ q: mergeSearchTerm, merged: false, page: 1, perPage: 6 }),
    enabled: canWriteFace && mergeSearchTerm.length >= 2,
  });

  const invalidateFace = (updated?: Face) => {
    queryClient.invalidateQueries({ queryKey: ["face", id] });
    queryClient.invalidateQueries({ queryKey: ["face", id, "appearances"] });
    queryClient.invalidateQueries({ queryKey: ["face", id, "suggestions"] });
    queryClient.invalidateQueries({ queryKey: ["face", id, "similar"] });
    queryClient.invalidateQueries({ queryKey: ["faces"] });
    if (updated?.performerId != null) {
      queryClient.invalidateQueries({ queryKey: ["performer", updated.performerId] });
    }
    if (face?.performerId != null) {
      queryClient.invalidateQueries({ queryKey: ["performer", face.performerId] });
    }
  };

  const updateMutation = useMutation({
    mutationFn: (data: { label?: string }) =>
      faces.update(id, {
        label: data.label,
        performerId: face?.performerId,
        primarySourceKey: face?.primarySourceKey,
        ignored: face?.ignored ?? false,
      }),
    onSuccess: (updated) => {
      setIsEditModalOpen(false);
      invalidateFace(updated);
    },
  });

  const linkMutation = useMutation({
    mutationFn: (performerId?: number) => faces.link(id, { performerId }),
    onSuccess: (updated) => {
      setPerformerSearch("");
      invalidateFace(updated);
    },
  });

  const mergeMutation = useMutation({
    mutationFn: (targetFaceId: number) => faces.mergeInto(id, { targetFaceId }),
    onSuccess: (updated) => {
      setIsMergeModalOpen(false);
      invalidateFace(updated);
      if (updated.mergedIntoFaceId != null) {
        onNavigate({ page: "face", id: updated.mergedIntoFaceId });
      }
    },
  });

  const deleteMutation = useMutation({
    mutationFn: () => faces.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["faces"] });
      goBack();
    },
  });

  const suggestionDecisionMutation = useMutation({
    mutationFn: (data: { performerId: number; decision: "accept" | "reject" | "merge"; setPerformerImage?: boolean; secondaryPerformerIds?: number[]; referenceEndpoint?: string; referenceExternalId?: string; referenceUpdateMetadata?: boolean }) => faces.recordSuggestionDecision(id, data),
    onSuccess: () => {
      invalidateFace();
    },
  });

  const createPerformerMutation = useMutation({
    mutationFn: () => faces.createPerformer(id, { name: normalizedNewPerformerName, setPerformerImage: setNewPerformerImage }),
    onSuccess: (updated) => {
      setIsCreatePerformerModalOpen(false);
      setNewPerformerName("");
      setSetNewPerformerImage(true);
      invalidateFace(updated);
      queryClient.invalidateQueries({ queryKey: ["performers"] });
      if (updated.performerId != null) {
        queryClient.invalidateQueries({ queryKey: ["performer", updated.performerId] });
      }
    },
  });

  const normalizedLabel = label.trim() || undefined;
  const hasMetadataChanges = useMemo(() => {
    if (!face) {
      return false;
    }

    return (face.label ?? "") !== (normalizedLabel ?? "");
  }, [face, normalizedLabel]);

  const mergeCandidates = (mergeMatchesQuery.data?.items ?? []).filter((candidate) => candidate.id !== id);
  const performerMatches = performerMatchesQuery.data?.items ?? [];
  const carouselSampleImageUrls = useMemo(
    () => buildFaceCarouselSampleImageUrls(face, faceDetections, faces.detectionCropUrl),
    [face, faceDetections],
  );
  const heroImageUrls = useMemo(
    () => buildFaceHeroImageUrls(face, carouselSampleImageUrls),
    [carouselSampleImageUrls, face],
  );
  const [heroImageIndex, setHeroImageIndex] = useState(0);
  const title = face ? faceDisplayName(face) : `Face #${id}`;
  const titleWithProvenance = face ? (
    <FieldProvenanceHover fieldProvenance={face.fieldProvenance} fieldKey={["label", "performer_id"]}>
      {title}
    </FieldProvenanceHover>
  ) : title;
  const tabs = useMemo(() => [
    { key: "overview", label: "Overview" },
    { key: "appearances", label: "Appears In", count: face?.appearanceCount || faceAppearancesPage.totalCount },
    { key: "similar", label: "Similar Faces", icon: <Sparkles className="h-4 w-4" />, count: similarFacesPage.totalCount },
  ], [face?.appearanceCount, faceAppearancesPage.totalCount, similarFacesPage.totalCount]);

  useDocumentTitle(face ? title : null);

  useEffect(() => {
    setHeroImageIndex((current) => Math.min(current, Math.max(0, heroImageUrls.length - 1)));
  }, [heroImageUrls.length]);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (!face) {
    return <div className="py-16 text-center text-secondary">Face not found</div>;
  }

  const deleteImpactSummary = deleteImpactLoading
    ? "Loading delete impact..."
    : deleteImpact
      ? describeFaceDeleteImpact(deleteImpact)
      : "Delete this face cluster and remove the AI artifacts it owns.";

  const overviewContent = (
    <div className="space-y-6">
      {faceDetectionsLoadError ? <ListLoadError error={faceDetectionsLoadError} onRetry={() => { void retryFaceDetections(); }} /> : null}
      <section className="space-y-6">
        <section className="rounded-2xl border border-border bg-card/70 p-5">
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Linked Performer</h2>
              <p className="mt-1 text-sm text-secondary">Keep the cluster linked to a performer and review the best candidate matches here.</p>
            </div>
            {face.performerId ? <StatusPill icon={<Link2 className="h-3 w-3" />} label="Linked" tone="accent" /> : null}
          </div>

          {face.performerId ? (
            <div className="mt-4 space-y-3">
              <button
                type="button"
                onClick={() => canReadPerformers && onNavigate({ page: "performer", id: face.performerId })}
                className={`text-left text-base font-medium ${canReadPerformers ? "text-accent hover:underline" : "text-foreground"}`}
              >
                <FieldProvenanceHover fieldProvenance={face.fieldProvenance} fieldKey="performer_id">
                  {face.performerName || `Performer #${face.performerId}`}
                </FieldProvenanceHover>
              </button>
              {canWriteFace ? (
                <button
                  type="button"
                  onClick={() => linkMutation.mutate(undefined)}
                  disabled={linkMutation.isPending}
                  className="rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {linkMutation.isPending ? "Saving..." : "Unlink performer"}
                </button>
              ) : null}
            </div>
          ) : (
            <div className="mt-4 flex flex-wrap items-center gap-3">
              <p className="text-sm text-secondary">No performer is linked to this face cluster yet.</p>
              {canWriteFace ? (
                <button
                  type="button"
                  onClick={() => setIsCreatePerformerModalOpen(true)}
                  className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                >
                  <UserPlus className="h-4 w-4" />
                  Create Performer
                </button>
              ) : null}
            </div>
          )}

          {canWriteFace ? (
            <div className="mt-5 space-y-3 border-t border-border pt-4">
              <label className="block text-xs font-semibold uppercase tracking-wide text-muted">Link to performer</label>
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                <input
                  type="text"
                  value={performerSearch}
                  onChange={(event) => setPerformerSearch(event.target.value)}
                  placeholder="Search performers"
                  className="w-full rounded-lg border border-border bg-input py-2 pl-9 pr-3 text-sm text-foreground outline-none focus:border-accent"
                />
              </div>
              {performerSearchTerm.length < 2 ? (
                <p className="text-xs text-secondary">Type at least two characters to search performers.</p>
              ) : performerMatchesQuery.isLoading ? (
                <p className="text-xs text-secondary">Searching performers...</p>
              ) : performerMatches.length === 0 ? (
                <p className="text-xs text-secondary">No performers matched that search.</p>
              ) : (
                <div className="space-y-2">
                  {performerMatches.map((performer) => (
                    <PerformerCandidateRow
                      key={performer.id}
                      performer={performer}
                      onSelect={() => linkMutation.mutate(performer.id)}
                      disabled={linkMutation.isPending}
                    />
                  ))}
                </div>
              )}
            </div>
          ) : null}
        </section>

        {!face.performerId && canWriteFace ? (
          <section className="space-y-4">
            <div className="flex items-start justify-between gap-3">
              <div>
                <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Suggested Matches</h2>
                <p className="mt-1 text-sm text-secondary">Review suggested performer links before accepting or rejecting them.</p>
              </div>
              <StatusPill icon={<Link2 className="h-3 w-3" />} label={suggestionsLoadError ? "Unavailable" : `${faceSuggestions.length} candidates`} tone="muted" />
            </div>
            <div>
              {suggestionsLoadError ? (
                <ListLoadError error={suggestionsLoadError} onRetry={() => { void retrySuggestions(); }} className="mb-4" />
              ) : <FaceSuggestionsPanel
                face={face}
                suggestions={faceSuggestions}
                isLoading={suggestionsLoading}
                disabled={suggestionDecisionMutation.isPending}
                canReadPerformers={canReadPerformers}
                onAccept={(value) => {
                  // Only divert to the compare dialog for a genuine cross-pack conflict; otherwise
                  // accept directly.
                  if (hasConflictingMatch(value, faceSuggestions)) {
                    setComparingSuggestion(value);
                    return;
                  }

                  suggestionDecisionMutation.mutate({ performerId: readSuggestionPerformerId(value), decision: "accept" });
                }}
                onReject={(value) => suggestionDecisionMutation.mutate({ performerId: readSuggestionPerformerId(value), decision: "reject" })}
                onCompare={(value) => setComparingSuggestion(value)}
                onNavigate={onNavigate}
              />}
            </div>
          </section>
        ) : null}
      </section>
    </div>
  );

  const appearancesContent = (
    <section className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Appears In</h2>
          <p className="mt-1 text-sm text-secondary">Videos and images where this face appears.</p>
        </div>
        <div className="text-xs text-muted">{appearancesLoadError ? "Unavailable" : `${faceAppearancesPage.totalCount} appearance${faceAppearancesPage.totalCount === 1 ? "" : "s"}`}</div>
      </div>

      <ListQueryState
        isLoading={appearancesLoading}
        loadError={appearancesLoadError}
        isEmpty={faceAppearancesPage.totalCount === 0}
        onRetry={() => { void retryAppearances(); }}
        loading={<div className="text-sm text-secondary">Loading appearances...</div>}
        empty={<div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-secondary">No appearances currently point to this face cluster.</div>}
      >
        <>
          <DetailListToolbar
            filter={appearanceFilter}
            onFilterChange={setAppearanceFilter}
            totalCount={faceAppearancesPage.totalCount}
            sortOptions={APPEARANCE_SORT_OPTIONS}
            zoomLevel={appearanceZoomLevel}
            onZoomChange={setAppearanceZoomLevel}
            cardSizeEntityType="faces"
            showSearch
            allowInfinitePageSize
            displayMode={appearanceDisplayMode}
            onDisplayModeChange={(mode: DetailListDisplayMode) => { if (mode === "grid" || mode === "list") setAppearanceDisplayMode(mode); }}
            availableDisplayModes={["grid", "list"]}
          />
          <FaceAppearancesGrid appearances={faceAppearancesPage.items} displayMode={appearanceDisplayMode} onNavigate={onNavigate} zoomLevel={appearanceZoomLevel} infinitePageSize={appearancesInfinitePageSize} hasNextPage={appearancesInfiniteQuery.hasNextPage} isFetchingNextPage={appearancesInfiniteQuery.isFetchingNextPage} loadMore={loadMoreAppearances} />
          <DetailListPagination filter={appearanceFilter} onFilterChange={setAppearanceFilter} totalCount={faceAppearancesPage.totalCount} allowInfinitePageSize />
        </>
      </ListQueryState>
    </section>
  );

  const similarFacesContent = (
    <section className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Similar Faces</h2>
          <p className="mt-1 text-sm text-secondary">Nearest neighbors from the face embedding index.</p>
        </div>
        <div className="text-xs text-muted">{similarLoadError ? "Unavailable" : `${similarFacesPage.totalCount} match${similarFacesPage.totalCount === 1 ? "" : "es"}`}</div>
      </div>

      <ListQueryState
        isLoading={similarLoading}
        loadError={similarLoadError}
        isEmpty={similarFacesPage.totalCount === 0}
        onRetry={() => { void retrySimilar(); }}
        loading={<div className="text-sm text-secondary">Loading similar faces...</div>}
        empty={<div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-secondary">No similar faces are available for this cluster yet.</div>}
      >
        <>
          <DetailListToolbar
            filter={similarFilter}
            onFilterChange={setSimilarFilter}
            totalCount={similarFacesPage.totalCount}
            sortOptions={SIMILAR_SORT_OPTIONS}
            zoomLevel={similarZoomLevel}
            onZoomChange={setSimilarZoomLevel}
            cardSizeEntityType="faces"
            showSearch
            allowInfinitePageSize
            displayMode={similarDisplayMode}
            onDisplayModeChange={(mode: DetailListDisplayMode) => { if (mode === "grid" || mode === "list") setSimilarDisplayMode(mode); }}
            availableDisplayModes={["grid", "list"]}
          />
          <SimilarFacesView faces={similarFacesPage.items} displayMode={similarDisplayMode} onNavigate={onNavigate} canReadPerformers={canReadPerformers} zoomLevel={similarZoomLevel} infinitePageSize={similarInfinitePageSize} hasNextPage={similarInfiniteQuery.hasNextPage} isFetchingNextPage={similarInfiniteQuery.isFetchingNextPage} loadMore={loadMoreSimilar} />
          <DetailListPagination filter={similarFilter} onFilterChange={setSimilarFilter} totalCount={similarFacesPage.totalCount} allowInfinitePageSize />
        </>
      </ListQueryState>
    </section>
  );

  const activeTabContent = activeTab === "overview"
    ? overviewContent
    : activeTab === "appearances"
      ? appearancesContent
      : similarFacesContent;

  const faceActions = (
    <>
      {canWriteFace ? (
        <button
          type="button"
          onClick={() => setIsEditModalOpen(true)}
          className={HERO_PRIMARY_ACTION_BUTTON_CLASS}
          title="Edit face"
        >
          <Pencil className="h-3.5 w-3.5" /> Edit
        </button>
      ) : null}
      {(canWriteFace || canDeleteFace) ? (
        <div className="relative" ref={actionsMenuRef}>
          <button
            type="button"
            onClick={() => setShowActionsMenu((current) => !current)}
            className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card text-secondary transition hover:border-accent hover:text-foreground"
            title="More actions"
          >
            <MoreVertical className="h-4 w-4" />
          </button>
          <FloatingActionMenu open={showActionsMenu} anchorRef={actionsMenuRef} onClose={() => setShowActionsMenu(false)} className="min-w-[180px] py-1">
              {canWriteFace ? (
                <button
                  type="button"
                  onClick={() => {
                    setIsMergeModalOpen(true);
                    setShowActionsMenu(false);
                  }}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                >
                  <Merge className="h-3.5 w-3.5" />
                  Merge
                </button>
              ) : null}
              {canWriteFace && !face.performerId ? (
                <button
                  type="button"
                  onClick={() => {
                    setIsCreatePerformerModalOpen(true);
                    setShowActionsMenu(false);
                  }}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                >
                  <UserPlus className="h-3.5 w-3.5" />
                  Create performer
                </button>
              ) : null}
              {canDeleteFace ? (
                <button
                  type="button"
                  onClick={() => {
                    setIsDeleteDialogOpen(true);
                    setShowActionsMenu(false);
                  }}
                  disabled={deleteMutation.isPending}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-200 transition-colors hover:bg-red-500/10 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                  {deleteMutation.isPending ? "Deleting..." : "Delete face"}
                </button>
              ) : null}
          </FloatingActionMenu>
        </div>
      ) : null}
    </>
  );

  return (
    <>
      <EntityHeroLayout
        backLabel={backLabel}
        onGoBack={goBack}
        imageUrl={face.coverImageUrl}
        imageCarouselUrls={heroImageUrls}
        imageCarouselIndex={heroImageIndex}
        onImageCarouselIndexChange={setHeroImageIndex}
        imageAlt={title}
        imageFallback={<Fingerprint className="h-14 w-14" />}
        title={titleWithProvenance}
        counts={[
          { key: "appearances", label: "Appearances", value: face.appearanceCount || faceAppearancesPage.totalCount, icon: <Eye className="h-4 w-4" /> },
          { key: "videos", label: "Videos", value: face.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "images", label: "Images", value: face.imageCount, icon: <Images className="h-4 w-4" /> },
        ]}
        metaRow={(
          <>
            <span>Created {formatDate(face.createdAt)}</span>
            <span>Updated {formatDate(face.updatedAt)}</span>
            <FieldProvenanceHover fieldProvenance={face.fieldProvenance} fieldKey="performer_id">
              <span>{face.performerName || "Unlinked"}</span>
            </FieldProvenanceHover>
          </>
        )}
        favorite={canEngageFace ? faceFavorite : undefined}
        favoritePending={faceFavoritePending}
        onFavoriteToggle={canEngageFace ? () => setFaceFavorite(!faceFavorite) : undefined}
        actions={faceActions}
      >
        <EntityDetailTabs tabs={tabs} activeTab={activeTab} onTabChange={(key) => setActiveTab(key as FaceTab)} className="mx-auto mb-4 max-w-7xl" />
        {activeTabContent}
      </EntityHeroLayout>

      <EditModal open={isEditModalOpen} onClose={() => setIsEditModalOpen(false)} title={`Edit ${title}`}>
        <div className="space-y-4 py-5">
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Title
            <input
              ref={labelInputRef}
              type="text"
              value={label}
              onChange={(event) => setLabel(event.target.value)}
              placeholder="Optional face title"
              className="mt-2 w-full rounded-lg border border-border bg-input px-3 py-2 text-sm font-normal text-foreground outline-none focus:border-accent"
            />
          </label>
          <div className="flex justify-end">
            <button
              type="button"
              onClick={() => updateMutation.mutate({ label: normalizedLabel })}
              disabled={!hasMetadataChanges || updateMutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Save className="h-4 w-4" />
              {updateMutation.isPending ? "Saving..." : "Save title"}
            </button>
          </div>
        </div>
      </EditModal>

      <EditModal open={isMergeModalOpen} onClose={() => setIsMergeModalOpen(false)} title={`Merge ${title}`}>
        <div className="space-y-4 py-5">
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">Merge into another face</label>
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
            <input
              ref={mergeInputRef}
              type="text"
              value={mergeSearch}
              onChange={(event) => setMergeSearch(event.target.value)}
              placeholder="Search by face label or linked performer"
              className="w-full rounded-lg border border-border bg-input py-2 pl-9 pr-3 text-sm text-foreground outline-none focus:border-accent"
            />
          </div>
          {mergeSearchTerm.length < 2 ? (
            <p className="text-sm text-secondary">Type at least two characters to search merge targets by face label or linked performer name.</p>
          ) : mergeMatchesQuery.isLoading ? (
            <p className="text-sm text-secondary">Searching faces...</p>
          ) : mergeCandidates.length === 0 ? (
            <p className="text-sm text-secondary">No merge targets matched that search.</p>
          ) : (
            <div className="space-y-2">
              {mergeCandidates.map((candidate) => (
                <FaceCandidateRow
                  key={candidate.id}
                  face={candidate}
                  onSelect={() => mergeMutation.mutate(candidate.id)}
                  disabled={mergeMutation.isPending}
                />
              ))}
            </div>
          )}
        </div>
      </EditModal>

      <EditModal open={isCreatePerformerModalOpen} onClose={() => setIsCreatePerformerModalOpen(false)} title={`Create performer from ${title}`}>
        <div className="space-y-4 py-5">
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Performer name
            <input
              type="text"
              value={newPerformerName}
              onChange={(event) => setNewPerformerName(event.target.value)}
              placeholder="Name"
              className="mt-2 w-full rounded-lg border border-border bg-input px-3 py-2 text-sm font-normal text-foreground outline-none focus:border-accent"
              autoFocus
            />
          </label>
          <label className="flex items-center gap-2 text-sm text-secondary">
            <input
              type="checkbox"
              checked={setNewPerformerImage}
              onChange={(event) => setSetNewPerformerImage(event.target.checked)}
              className="rounded border-border bg-surface accent-accent"
            />
            Use this face as the performer image
          </label>
          {createPerformerMutation.error ? (
            <p className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">{String(createPerformerMutation.error.message ?? createPerformerMutation.error)}</p>
          ) : null}
          <div className="flex justify-end">
            <button
              type="button"
              onClick={() => createPerformerMutation.mutate()}
              disabled={!normalizedNewPerformerName || createPerformerMutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              <UserPlus className="h-4 w-4" />
              {createPerformerMutation.isPending ? "Creating..." : "Create performer"}
            </button>
          </div>
        </div>
      </EditModal>

      <ConfirmDialog
        open={isDeleteDialogOpen}
        title={`Delete ${title}?`}
        message={`${deleteImpactSummary} This cannot be undone.`}
        confirmLabel="Delete face"
        onCancel={() => setIsDeleteDialogOpen(false)}
        onConfirm={() => {
          setIsDeleteDialogOpen(false);
          deleteMutation.mutate();
        }}
      />

      <FaceCompareDialog
        open={comparingSuggestion != null}
        face={face ?? null}
        suggestion={comparingSuggestion}
        faceImageUrls={carouselSampleImageUrls}
        disabled={suggestionDecisionMutation.isPending}
        canReadPerformers={canReadPerformers}
        siblingSuggestions={faceSuggestions}
        onClose={() => setComparingSuggestion(null)}
        onConfirm={(value, options) => {
          if ("performerId" in value) {
            suggestionDecisionMutation.mutate({ performerId: value.performerId, decision: "accept", setPerformerImage: options?.setPerformerImage, ...readReferenceLinkInfo(value) });
          }
          setComparingSuggestion(null);
        }}
        onReject={(value) => {
          if ("performerId" in value) {
            suggestionDecisionMutation.mutate({ performerId: value.performerId, decision: "reject" });
          }
          setComparingSuggestion(null);
        }}
        onMerge={(primaryPerformerId, secondaryPerformerIds) => {
          suggestionDecisionMutation.mutate({ performerId: primaryPerformerId, decision: "merge", secondaryPerformerIds });
          setComparingSuggestion(null);
        }}
        onNavigate={onNavigate}
      />
    </>
  );
}

function StatusPill({ icon, label, tone }: { icon: React.ReactNode; label: string; tone: "muted" | "accent" }) {
  const toneClassName = tone === "accent"
    ? "border-accent/30 bg-accent/10 text-accent"
    : "border-border bg-surface/70 text-secondary";

  return (
    <span className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 ${toneClassName}`}>
      {icon}
      {label}
    </span>
  );
}

function PerformerCandidateRow({ performer, onSelect, disabled }: { performer: Performer; onSelect: () => void; disabled: boolean }) {
  return (
    <button
      type="button"
      onClick={onSelect}
      disabled={disabled}
      className="flex w-full items-center justify-between gap-3 rounded-xl border border-border bg-surface/60 px-3 py-3 text-left transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
    >
      <div>
        <div className="text-sm font-medium text-foreground">{performer.name}</div>
        <div className="mt-1 text-xs text-secondary">{performer.videoCount ?? 0} videos</div>
      </div>
      <span className="text-xs text-accent">Link</span>
    </button>
  );
}

function FaceCandidateRow({ face, onSelect, disabled }: { face: Face; onSelect: () => void; disabled: boolean }) {
  const title = faceDisplayName(face);

  return (
    <button
      type="button"
      onClick={onSelect}
      disabled={disabled}
      className="flex w-full items-center gap-3 rounded-xl border border-border bg-surface/60 px-3 py-3 text-left transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
    >
      <div className="h-14 w-14 overflow-hidden rounded-xl bg-surface/90">
        {face.coverImageUrl ? (
          <img src={face.coverImageUrl} alt={title} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <Fingerprint className="h-5 w-5" />
          </div>
        )}
      </div>
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium text-foreground">{title}</div>
        <div className="mt-1 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-xs text-secondary">
          {face.performerId && face.performerName ? (
            <span className="inline-flex items-center gap-1 text-accent">
              <Link2 className="h-3 w-3" />
              {face.performerName}
            </span>
          ) : (
            <span className="text-muted">Unlinked</span>
          )}
          <span aria-hidden>·</span>
          <span>{face.appearanceCount} appearance{face.appearanceCount === 1 ? "" : "s"}</span>
          <span aria-hidden>·</span>
          <span>{face.detectionCount} detection{face.detectionCount === 1 ? "" : "s"}</span>
        </div>
      </div>
      <span className="shrink-0 text-xs text-accent">Merge</span>
    </button>
  );
}

function FaceAppearancesGrid({ appearances, displayMode, onNavigate, zoomLevel, infinitePageSize, hasNextPage, isFetchingNextPage, loadMore }: { appearances: FaceAppearanceListItem[]; displayMode: "grid" | "list"; onNavigate: (r: any) => void; zoomLevel: number; infinitePageSize: boolean; hasNextPage?: boolean; isFetchingNextPage?: boolean; loadMore: () => void }) {
  if (displayMode === "list") {
    return (
      <div className="overflow-hidden rounded-lg border border-border bg-card/60">
        <div className="divide-y divide-border/70">
          {appearances.map((appearance) => (
            <button key={appearance.appearanceId} type="button" onClick={() => onNavigate({ page: appearance.hostType, id: appearance.hostId })} className="flex w-full items-center gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-card-hover">
              <div className="h-12 w-16 shrink-0 overflow-hidden rounded bg-surface">
                {appearance.thumbnailUrl ? <img src={appearance.thumbnailUrl} alt="" className="h-full w-full object-cover" loading="lazy" /> : null}
              </div>
              <div className="min-w-0 flex-1">
                <div className="truncate font-medium text-accent">{appearance.title || `${appearance.hostType} #${appearance.hostId}`}</div>
                <div className="mt-0.5 truncate text-xs text-muted">{appearance.hostType} · {appearance.frameSampleCount} samples · {appearance.topConfidence != null ? `${Math.round(appearance.topConfidence * 100)}%` : "No confidence"}</div>
              </div>
            </button>
          ))}
        </div>
      </div>
    );
  }

  return (
    <VirtualizedEntityGrid items={appearances} getItemKey={(appearance) => appearance.appearanceId} minCardWidth={`${getEntityCardMinWidthPx("faces", zoomLevel)}px`} virtualMinColumnWidth={getEntityCardMinWidthPx("faces", zoomLevel)} estimateRowHeight={280} gap={16} gapClassName="gap-4" infinitePageSize={infinitePageSize} hasNextPage={hasNextPage} isFetchingNextPage={isFetchingNextPage} loadMore={loadMore} renderItem={(appearance) => (
      <FaceAppearanceTile appearance={appearance} onClick={() => onNavigate({ page: appearance.hostType, id: appearance.hostId })} />
    )} />
  );
}

function SimilarFacesView({ faces: faceItems, displayMode, onNavigate, canReadPerformers, zoomLevel, infinitePageSize, hasNextPage, isFetchingNextPage, loadMore }: { faces: FaceSimilar[]; displayMode: "grid" | "list"; onNavigate: (r: any) => void; canReadPerformers: boolean; zoomLevel: number; infinitePageSize: boolean; hasNextPage?: boolean; isFetchingNextPage?: boolean; loadMore: () => void }) {
  if (displayMode === "list") {
    return (
      <div className="overflow-hidden rounded-lg border border-border bg-card/60">
        <div className="divide-y divide-border/70">
          {faceItems.map((face) => {
            const title = face.label?.trim() || face.performerName || `Face #${face.id}`;
            return (
              <button key={face.id} type="button" onClick={() => onNavigate({ page: "face", id: face.id })} className="flex w-full items-center gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-card-hover">
                <div className="h-12 w-12 shrink-0 overflow-hidden rounded bg-surface">
                  {face.coverImageUrl ? <img src={face.coverImageUrl} alt="" className="h-full w-full object-cover" loading="lazy" /> : <div className="flex h-full w-full items-center justify-center text-muted"><Fingerprint className="h-4 w-4" /></div>}
                </div>
                <div className="min-w-0 flex-1">
                  <div className="truncate font-medium text-accent">{title}</div>
                  <div className="mt-0.5 truncate text-xs text-muted">Distance {face.distance.toFixed(3)} · {face.appearanceCount} appearances</div>
                </div>
                {face.performerId ? <span className={`shrink-0 text-xs ${canReadPerformers ? "text-accent" : "text-muted"}`}>{face.performerName || `Performer #${face.performerId}`}</span> : null}
              </button>
            );
          })}
        </div>
      </div>
    );
  }

  return (
    <VirtualizedEntityGrid items={faceItems} getItemKey={(candidate) => candidate.id} minCardWidth={`${getEntityCardMinWidthPx("faces", zoomLevel)}px`} virtualMinColumnWidth={getEntityCardMinWidthPx("faces", zoomLevel)} estimateRowHeight={360} gap={16} gapClassName="gap-4" infinitePageSize={infinitePageSize} hasNextPage={hasNextPage} isFetchingNextPage={isFetchingNextPage} loadMore={loadMore} renderItem={(candidate) => (
      <SimilarFaceTile face={candidate} onNavigate={onNavigate} canReadPerformers={canReadPerformers} />
    )} />
  );
}

function describeFaceDeleteImpact(deleteImpact: FaceDeleteImpact) {
  const coverImageSummary = deleteImpact.hasCoverImage ? "1 cover image" : "no cover image";
  return `Deletes ${formatCount(deleteImpact.detectionCount, "detection")}, ${formatCount(deleteImpact.embeddingCount, "embedding")}, ${formatCount(deleteImpact.segmentCount, "timeline segment")}, and ${coverImageSummary}.`;
}

function formatCount(count: number, singular: string, plural = `${singular}s`) {
  return `${count} ${count === 1 ? singular : plural}`;
}

function SimilarFaceTile({ face, onNavigate, canReadPerformers }: { face: FaceSimilar; onNavigate: (r: any) => void; canReadPerformers: boolean }) {
  return (
    <FaceTile face={face} onClick={() => onNavigate({ page: "face", id: face.id })}>
      <div className="space-y-1 text-xs text-secondary">
          <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">Closest match</div>
          <div className="mt-1 text-sm font-medium text-foreground">Distance {face.distance.toFixed(3)}</div>
          {face.performerId ? (
            <button
              type="button"
              onClick={() => canReadPerformers && onNavigate({ page: "performer", id: face.performerId })}
              className={`mt-2 text-left text-xs ${canReadPerformers ? "text-accent hover:underline" : "text-secondary"}`}
            >
              {face.performerName || `Performer #${face.performerId}`}
            </button>
          ) : null}
      </div>
    </FaceTile>
  );
}
