import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Eye,
  Film,
  Fingerprint,
  Images,
  Link2,
  Merge,
  MoreVertical,
  Pencil,
  Save,
  Scissors,
  Search,
  Sparkles,
  Trash2,
  UserPlus,
  UserX,
} from "lucide-react";
import { faces, performers } from "../api/client";
import type {
  BoolCriterion,
  Face,
  FaceAppearance,
  FaceBatchLinkRequest,
  FaceBatchLinkResult,
  FaceDeleteImpact,
  FaceSimilar,
  FaceSuggestion,
  FindFilter,
  PaginatedResponse,
  Performer,
  StringCriterion,
} from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { ListQueryState } from "../components/ListQueryState";
import { FaceSuggestionsPanel } from "../components/FaceSuggestionsPanel";
import { hasConflictingMatch } from "../utils/faceSuggestions";
import { FaceSplitDialog } from "../components/FaceSplitDialog";
import { FACE_PATH_CRITERION, FACE_PATH_MODIFIERS } from "../components/faceFilterCriteria";
import type { CriterionDefinition } from "../components/filterCriteriaTypes";
import { useFaceCapabilities } from "../hooks/useFaceCapabilities";
import { FaceCompareDialog, readReferenceLinkInfo } from "../components/FaceCompareDialog";
import { buildFaceCarouselSampleImageUrls, buildFaceHeroImageUrls } from "../components/faceComparisonImages";
import {
  FaceAppearanceBreakdown,
  type FaceMoveOutcome,
  MoveOutcomeList,
  useFaceImageSources,
} from "../components/FaceAppearanceBreakdown";
import { faceDisplayName } from "../utils/faceDisplay";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { EditModal } from "../components/EditModal";
import { EntityHeroLayout, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { FaceAppearanceTile, FaceTile } from "../components/EntityCards";
import { FieldProvenanceHover, formatDate } from "../components/shared";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useDetailTabUrlState } from "../hooks/useDetailListUrlState";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import type { DetailListDisplayMode } from "../components/DetailListToolbar";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { LoadedImage } from "../components/LoadedImage";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

const FACE_TABS = ["overview", "appearances", "similar"] as const;
type FaceTab = (typeof FACE_TABS)[number];

function isFaceTab(value: string): value is FaceTab {
  return (FACE_TABS as readonly string[]).includes(value);
}

/**
 * An in-progress review of a face's similar faces and the outcome of the last batch. `choices` holds what the
 * reviewer decided for a face (true links it). It stores the decision itself rather than a change from the
 * default, so a face whose saved reject appears or goes away during the review keeps what the reviewer chose.
 */
interface SimilarReviewState {
  faceId: number;
  choices: ReadonlyMap<number, boolean>;
  result: FaceBatchLinkResult | null;
}

function emptySimilarReview(faceId: number): SimilarReviewState {
  return { faceId, choices: new Map(), result: null };
}

/** Without a choice, a new candidate is linked and a face already marked "not this performer" stays so. */
function linksByDefault(face: FaceSimilar) {
  return !face.rejectedForSourcePerformer;
}

/** Leaves out the choices for the given faces, keeping those made elsewhere in the review. */
function withoutChoices(choices: ReadonlyMap<number, boolean>, faceIds: Iterable<number>) {
  const next = new Map(choices);
  for (const faceId of faceIds) next.delete(faceId);
  return next;
}

/** An unlinked similar face the review can decide on, including one already turned down for the performer. */
function isSimilarReviewCandidate(face: FaceSimilar) {
  return face.performerId == null && face.mergedIntoFaceId == null && !face.ignored;
}

/** Whether the batch links this candidate: the reviewer's choice, or the default without one. */
function isLinkedByReview(face: FaceSimilar, choices: ReadonlyMap<number, boolean>) {
  return choices.get(face.id) ?? linksByDefault(face);
}

/** A similar face opens on its own Similar Faces tab, so walking a chain of look-alikes stays one click per face. */
function similarFaceRoute(faceId: number) {
  return { page: "face", id: faceId, detailTab: "similar" satisfies FaceTab };
}

type FaceAppearanceListItem = FaceAppearance & { id: string | number };

const EMPTY_APPEARANCES_PAGE: PaginatedResponse<FaceAppearanceListItem> = {
  items: [],
  totalCount: 0,
  page: 1,
  perPage: 24,
};
const EMPTY_SIMILAR_PAGE: PaginatedResponse<FaceSimilar> = { items: [], totalCount: 0, page: 1, perPage: 18 };
const APPEARANCE_SORT_OPTIONS = [
  { value: "random", label: "Random" },
  { value: "last_seen", label: "Last Seen" },
  { value: "first_seen", label: "First Seen" },
  { value: "sample_count", label: "Frame Samples" },
  { value: "confidence", label: "Confidence" },
  { value: "host_type", label: "Host Type" },
  { value: "title", label: "Title" },
];
const SIMILAR_SORT_OPTIONS = [
  { value: "random", label: "Random" },
  { value: "distance", label: "Closest Match" },
  { value: "appearance_count", label: "Most Appearances" },
  { value: "video_count", label: "Most Videos" },
  { value: "image_count", label: "Most Images" },
  { value: "updated_at", label: "Recently Updated" },
  { value: "label", label: "Name" },
];

const SIMILAR_FACE_CRITERIA: CriterionDefinition[] = [
  { id: "linked", label: "Linked", type: "bool", filterKey: "linkedCriterion" },
  FACE_PATH_CRITERION,
];

/** The similar-faces endpoint takes flat params; keep only the criteria it understands. */
function readSimilarFaceFilters(objectFilter: Record<string, unknown>) {
  const linked = objectFilter.linkedCriterion as Partial<BoolCriterion> | undefined;
  const path = objectFilter.pathCriterion as Partial<StringCriterion> | undefined;
  const pathValue = typeof path?.value === "string" ? path.value.trim() : "";
  return {
    linked: typeof linked?.value === "boolean" ? linked.value : undefined,
    path: pathValue || undefined,
    pathModifier:
      pathValue && path?.modifier && FACE_PATH_MODIFIERS.includes(path.modifier) ? path.modifier : undefined,
  };
}

type FaceAppearanceHost = Pick<FaceAppearance, "hostType" | "hostId" | "title">;

function readSuggestionPerformerId(value: number | FaceSuggestion) {
  return typeof value === "number" ? value : value.performerId;
}

export function FaceDetailPage({ id, onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { hasPermission, user } = useAuth();
  const canWriteFace = canWriteEntity("face", hasPermission);
  const canDeleteFace = canDeleteEntity("face", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canEngageFace = canReadEntity("face", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const { backLabel, goBack, backAlternative, goToOrigin } = useBackNavigation({ page: "faces" }, onNavigate);
  const [appearanceFilter, setAppearanceFilter] = useState<FindFilter>({
    page: 1,
    perPage: 24,
    sort: "last_seen",
    direction: "desc",
  });
  const [similarFilter, setSimilarFilter] = useState<FindFilter>({
    page: 1,
    perPage: 18,
    sort: "distance",
    direction: "asc",
  });
  const [similarObjectFilter, setSimilarObjectFilter] = useState<Record<string, unknown>>({});
  const similarFaceFilters = useMemo(() => readSimilarFaceFilters(similarObjectFilter), [similarObjectFilter]);
  const [appearanceZoomLevel, setAppearanceZoomLevel] = useState(0);
  const [similarZoomLevel, setSimilarZoomLevel] = useState(0);
  const [appearanceDisplayMode, setAppearanceDisplayMode] = useState<"grid" | "list">("list");
  const [similarDisplayMode, setSimilarDisplayMode] = useState<"grid" | "list">("grid");
  const [showActionsMenu, setShowActionsMenu] = useState(false);
  const actionsMenuRef = useRef<HTMLDivElement | null>(null);

  const {
    data: face,
    isLoading,
    error: faceError,
    refetch: retryFace,
  } = useQuery({
    queryKey: ["face", id],
    queryFn: () => faces.get(id),
  });
  const faceLoadError = getLoadError(face, faceError);

  const {
    data: similarFacesPage = EMPTY_SIMILAR_PAGE,
    isLoading: similarLoading,
    showsPreviousResults: similarShowsPreviousResults,
    loadError: similarLoadError,
    retry: retrySimilar,
    infinitePageSize: similarInfinitePageSize,
    infiniteQuery: similarInfiniteQuery,
    loadMore: loadMoreSimilar,
  } = useDetailListQuery<FaceSimilar>({
    queryKey: ["face", id, "similar", similarFaceFilters],
    filter: similarFilter,
    queryFn: (nextFilter) =>
      faces.similar(id, {
        q: nextFilter.q?.trim() || undefined,
        sort: nextFilter.sort,
        direction: nextFilter.direction,
        seed: nextFilter.seed,
        ...similarFaceFilters,
        page: nextFilter.page ?? 1,
        perPage: nextFilter.perPage ?? 18,
        k: 250,
      }),
  });

  const {
    data: faceAppearancesPage = EMPTY_APPEARANCES_PAGE,
    isLoading: appearancesLoading,
    loadError: appearancesLoadError,
    retry: retryAppearances,
    infinitePageSize: appearancesInfinitePageSize,
    infiniteQuery: appearancesInfiniteQuery,
    loadMore: loadMoreAppearances,
  } = useDetailListQuery<FaceAppearanceListItem>({
    queryKey: ["face", id, "appearances"],
    filter: appearanceFilter,
    queryFn: async (nextFilter) => {
      const page = await faces.appearances(id, {
        q: nextFilter.q?.trim() || undefined,
        sort: nextFilter.sort,
        direction: nextFilter.direction,
        seed: nextFilter.seed,
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

  const {
    data: faceSuggestionsData,
    isLoading: suggestionsLoading,
    error: suggestionsError,
    refetch: retrySuggestions,
  } = useQuery({
    queryKey: ["face", id, "suggestions"],
    queryFn: () => faces.suggestions(id),
    enabled: canWriteFace && face != null && face.performerId == null,
  });
  const suggestionsLoadError = getLoadError(faceSuggestionsData, suggestionsError);
  const faceSuggestions = faceSuggestionsData ?? [];
  const {
    data: faceDetectionsData,
    error: faceDetectionsError,
    refetch: retryFaceDetections,
  } = useQuery({
    queryKey: ["face", id, "detections"],
    queryFn: () => faces.detections(id),
    enabled: face != null,
  });
  const faceDetectionsLoadError = getLoadError(faceDetectionsData, faceDetectionsError);
  const faceDetections = useMemo(() => faceDetectionsData ?? [], [faceDetectionsData]);

  const [label, setLabel] = useState("");
  const [performerSearch, setPerformerSearch] = useState("");
  const [mergeSearch, setMergeSearch] = useState("");
  // The tab lives in the URL ("?tab=similar"), so a link, a reload or Back opens the same tab.
  const { activeTab: urlTab, setActiveTab } = useDetailTabUrlState<string>("overview");
  const activeTab: FaceTab = isFaceTab(urlTab) ? urlTab : "overview";
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

  // Starts undefined so a face that is already cached on mount still fills the label.
  const [prevFace, setPrevFace] = useState<typeof face>(undefined);
  if (prevFace !== face) {
    setPrevFace(face);
    if (face) setLabel(face.label ?? "");
  }

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

  const [prevCreatePerformerSource, setPrevCreatePerformerSource] = useState({
    open: isCreatePerformerModalOpen,
    faceLabel: face?.label,
  });
  if (
    prevCreatePerformerSource.open !== isCreatePerformerModalOpen ||
    prevCreatePerformerSource.faceLabel !== face?.label
  ) {
    setPrevCreatePerformerSource({ open: isCreatePerformerModalOpen, faceLabel: face?.label });
    if (isCreatePerformerModalOpen) {
      setNewPerformerName((current) => current.trim() || face?.label?.trim() || "");
    }
  }

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

  // Similar faces are usually the same person, so the unlinked ones on the page are reviewed as one batch for
  // this face's performer: every candidate is linked unless the reviewer skips it, and a skip is remembered
  // as "not this performer" so the face stops being offered for them.
  // App reuses this page across faces, so a review belongs to the face it was started on and reads as fresh
  // on any other face.
  const [storedSimilarReview, setSimilarReview] = useState<SimilarReviewState>(() => emptySimilarReview(id));
  const similarReview = storedSimilarReview.faceId === id ? storedSimilarReview : emptySimilarReview(id);
  const toggleSimilarChoice = (face: FaceSimilar) => {
    setSimilarReview((current) => {
      const choices = new Map(current.faceId === id ? current.choices : []);
      choices.set(face.id, !isLinkedByReview(face, choices));
      return { faceId: id, choices, result: null };
    });
  };
  const batchLinkSimilarMutation = useMutation({
    // The face the review was made on travels with the request: a pending mutation takes the newest
    // options, so reading `id` in onSuccess would credit a face the reviewer moved on to.
    mutationFn: ({ sourceFaceId: _sourceFaceId, ...request }: FaceBatchLinkRequest & { sourceFaceId: number }) =>
      faces.batchLink(request),
    meta: { suppressGlobalError: true },
    onSuccess: async (result, { sourceFaceId, performerId, faceIds, rejectFaceIds = [] }) => {
      for (const faceId of [...result.linked, ...result.rejected]) {
        void queryClient.invalidateQueries({ queryKey: ["face", faceId] });
      }
      void queryClient.invalidateQueries({ queryKey: ["faces"] });
      void queryClient.invalidateQueries({ queryKey: ["performer", performerId] });
      // Stay pending until the list shows the outcome, so the old page cannot be submitted a second time
      // and link a face the reviewer just skipped. The skips are released only then, so the old page never
      // shows a just-skipped face as "Will link" beside an outcome that says otherwise.
      await queryClient.invalidateQueries({ queryKey: ["face", sourceFaceId, "similar"] });
      // A face that failed keeps the reviewer's choice, so the next batch tries it again.
      const failed = new Set(result.failed.map((item) => item.faceId));
      const settled = [...faceIds, ...rejectFaceIds].filter((faceId) => !failed.has(faceId));
      setSimilarReview((current) => ({
        faceId: sourceFaceId,
        // Choices on pages that were not part of this batch still stand.
        choices: current.faceId === sourceFaceId ? withoutChoices(current.choices, settled) : new Map(),
        result,
      }));
    },
  });
  const similarReviewIsOwn = batchLinkSimilarMutation.variables?.sourceFaceId === id;
  const similarReviewError = similarReviewIsOwn ? batchLinkSimilarMutation.error : null;
  const similarReviewPending = similarReviewIsOwn && batchLinkSimilarMutation.isPending;
  // While the list still shows an earlier query (another face, filter or page), its faces and their
  // "not this performer" marks are not the ones being reviewed, so nothing can be chosen or sent.
  const similarReviewLocked = similarReviewPending || similarShowsPreviousResults;

  // Correcting a cluster that swallowed someone else's appearances. Both actions re-home occurrences
  // onto other faces, so refresh every face query (this one's counts, appearances and similar list).
  const { canEditOccurrences } = useFaceCapabilities(canWriteFace);
  const canCorrectAppearances = canWriteFace && canEditOccurrences;
  const [splitAppearance, setSplitAppearance] = useState<FaceAppearanceHost | null>(null);
  const refreshAfterAppearanceEdit = (hostType: FaceAppearance["hostType"]) => {
    queryClient.invalidateQueries({ queryKey: ["face"] });
    queryClient.invalidateQueries({ queryKey: ["faces"] });
    queryClient.invalidateQueries({ queryKey: [hostType] });
  };
  const markNotPresentMutation = useMutation({
    mutationFn: (host: FaceAppearanceHost) =>
      faces.markNotPresent(id, { hostType: host.hostType, hostId: host.hostId }),
    onSuccess: (_result, host) => refreshAfterAppearanceEdit(host.hostType),
  });
  const confirmMarkNotPresent = (host: FaceAppearanceHost) => {
    const hostNoun = host.hostType === "image" ? "image" : "video";
    const hostTitle = host.title || `${hostNoun} #${host.hostId}`;
    if (
      window.confirm(
        `Mark "${face ? faceDisplayName(face) : `Face #${id}`}" as NOT present in "${hostTitle}"?\n\nIts occurrences there (and in other ${hostNoun}s that match them) will be split off into the correct face.`,
      )
    ) {
      markNotPresentMutation.mutate(host);
    }
  };

  const deleteMutation = useMutation({
    mutationFn: () => faces.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["faces"] });
      goToOrigin();
    },
  });

  const suggestionDecisionMutation = useMutation({
    mutationFn: (data: {
      performerId: number;
      decision: "accept" | "reject" | "merge";
      setPerformerImage?: boolean;
      secondaryPerformerIds?: number[];
      referenceEndpoint?: string;
      referenceExternalId?: string;
      referenceUpdateMetadata?: boolean;
    }) => faces.recordSuggestionDecision(id, data),
    onSuccess: () => {
      invalidateFace();
    },
  });

  const createPerformerMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () =>
      faces.createPerformer(id, { name: normalizedNewPerformerName, setPerformerImage: setNewPerformerImage }),
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
  // Shares its request with the Appears In tab. A cover whose detection is known replaces that detection in the
  // carousel, instead of the best one.
  const { imageSources: faceImageSources } = useFaceImageSources(id, face != null);
  // Kept here and not in the tab's list: a move can leave the list empty or replaced, and its result must stay.
  const [moveResult, setMoveResult] = useState<{ faceId: number; outcomes: FaceMoveOutcome[] } | null>(null);
  const coverDetectionId = faceImageSources?.cover.sourceOnFace ? faceImageSources.cover.detectionId : null;
  const carouselSampleImageUrls = useMemo(
    () => buildFaceCarouselSampleImageUrls(face, faceDetections, faces.detectionCropUrl, coverDetectionId),
    [coverDetectionId, face, faceDetections],
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
  ) : (
    title
  );
  const tabs = useMemo(
    () => [
      { key: "overview", label: "Overview" },
      { key: "appearances", label: "Appears In", count: face?.appearanceCount || faceAppearancesPage.totalCount },
      {
        key: "similar",
        label: "Similar Faces",
        icon: <Sparkles className="h-4 w-4" />,
        count: similarFacesPage.totalCount,
      },
    ],
    [face?.appearanceCount, faceAppearancesPage.totalCount, similarFacesPage.totalCount],
  );

  useDocumentTitle(face ? title : null);

  const [prevHeroImageCount, setPrevHeroImageCount] = useState(heroImageUrls.length);
  if (prevHeroImageCount !== heroImageUrls.length) {
    setPrevHeroImageCount(heroImageUrls.length);
    setHeroImageIndex((current) => Math.min(current, Math.max(0, heroImageUrls.length - 1)));
  }

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(faceLoadError)) {
    return <div className="py-16 text-center text-secondary">Face not found</div>;
  }

  if (faceLoadError) {
    return (
      <ListLoadError
        error={faceLoadError}
        onRetry={() => {
          void retryFace();
        }}
        title="Could not load face"
        className="mx-0 mt-0"
      />
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
      {faceDetectionsLoadError ? (
        <ListLoadError
          error={faceDetectionsLoadError}
          onRetry={() => {
            void retryFaceDetections();
          }}
        />
      ) : null}
      <section className="space-y-6">
        <section className="rounded-2xl border border-border bg-card/70 p-5">
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Linked Performer</h2>
              <p className="mt-1 text-sm text-secondary">
                Keep the cluster linked to a performer and review the best candidate matches here.
              </p>
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
              <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
                Link to performer
              </label>
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
                <p className="mt-1 text-sm text-secondary">
                  Review suggested performer links before accepting or rejecting them.
                </p>
              </div>
              <StatusPill
                icon={<Link2 className="h-3 w-3" />}
                label={suggestionsLoadError ? "Unavailable" : `${faceSuggestions.length} candidates`}
                tone="muted"
              />
            </div>
            <div>
              {suggestionsLoadError ? (
                <ListLoadError
                  error={suggestionsLoadError}
                  onRetry={() => {
                    void retrySuggestions();
                  }}
                  className="mb-4"
                />
              ) : (
                <FaceSuggestionsPanel
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

                    suggestionDecisionMutation.mutate({
                      performerId: readSuggestionPerformerId(value),
                      decision: "accept",
                    });
                  }}
                  onReject={(value) =>
                    suggestionDecisionMutation.mutate({
                      performerId: readSuggestionPerformerId(value),
                      decision: "reject",
                    })
                  }
                  onCompare={(value) => setComparingSuggestion(value)}
                  onNavigate={onNavigate}
                />
              )}
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
          <p className="mt-1 text-sm text-secondary">
            Every video and image this face appears in, with the face as it looks in each appearance there.
          </p>
        </div>
        <div className="text-xs text-muted">
          {appearancesLoadError
            ? "Unavailable"
            : `${faceAppearancesPage.totalCount} appearance${faceAppearancesPage.totalCount === 1 ? "" : "s"}`}
        </div>
      </div>

      {moveResult?.faceId === id ? (
        <MoveOutcomeList outcomes={moveResult.outcomes} onNavigate={onNavigate} onDismiss={() => setMoveResult(null)} />
      ) : null}

      <ListQueryState
        header={
          <DetailListToolbar
            filter={appearanceFilter}
            onFilterChange={setAppearanceFilter}
            totalCount={faceAppearancesPage.totalCount}
            sortOptions={APPEARANCE_SORT_OPTIONS}
            zoomLevel={appearanceZoomLevel}
            onZoomChange={setAppearanceZoomLevel}
            cardSizeEntityType="faces"
            showSearch
            allowInfinitePageSize={appearanceDisplayMode === "grid"}
            displayMode={appearanceDisplayMode}
            onDisplayModeChange={(mode: DetailListDisplayMode) => {
              if (mode === "grid" || mode === "list") setAppearanceDisplayMode(mode);
            }}
            availableDisplayModes={["grid", "list"]}
          />
        }
        isLoading={appearancesLoading}
        loadError={appearancesLoadError}
        isEmpty={faceAppearancesPage.totalCount === 0}
        onRetry={() => {
          void retryAppearances();
        }}
        loading={<div className="text-sm text-secondary">Loading appearances...</div>}
        empty={
          <div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-secondary">
            No appearances currently point to this face cluster.
          </div>
        }
      >
        <>
          {appearanceDisplayMode === "list" ? (
            <FaceAppearanceBreakdown
              // A selection belongs to one face; another face must never inherit it.
              key={face.id}
              face={face}
              faceTitle={title}
              appearances={faceAppearancesPage.items}
              detections={faceDetections}
              canWrite={canWriteFace}
              canEditOccurrences={canEditOccurrences}
              onNavigate={onNavigate}
              onMoved={(outcomes) => setMoveResult({ faceId: id, outcomes })}
              onChanged={(hostTypes) => {
                invalidateFace();
                hostTypes.forEach(refreshAfterAppearanceEdit);
              }}
            />
          ) : (
            <FaceAppearancesGrid
              appearances={faceAppearancesPage.items}
              onNavigate={onNavigate}
              onSplit={canCorrectAppearances ? setSplitAppearance : undefined}
              onMarkNotPresent={canCorrectAppearances ? confirmMarkNotPresent : undefined}
              markingNotPresent={markNotPresentMutation.isPending ? markNotPresentMutation.variables : undefined}
              zoomLevel={appearanceZoomLevel}
              infinitePageSize={appearancesInfinitePageSize}
              hasNextPage={appearancesInfiniteQuery.hasNextPage}
              isFetchingNextPage={appearancesInfiniteQuery.isFetchingNextPage}
              loadMore={loadMoreAppearances}
            />
          )}
          <DetailListPagination
            filter={appearanceFilter}
            onFilterChange={setAppearanceFilter}
            totalCount={faceAppearancesPage.totalCount}
            allowInfinitePageSize={appearanceDisplayMode === "grid"}
          />
        </>
      </ListQueryState>
    </section>
  );

  const similarReviewTarget: SimilarFaceLinkTarget | undefined =
    canWriteFace && face.performerId
      ? { performerId: face.performerId, performerName: face.performerName || `Performer #${face.performerId}` }
      : undefined;
  // The batch covers the faces on the page being looked at, so nothing is linked without being seen.
  const similarReviewCandidates = similarFacesPage.items.filter(isSimilarReviewCandidate);
  const similarReviewLinkIds = similarReviewCandidates
    .filter((candidate) => isLinkedByReview(candidate, similarReview.choices))
    .map((candidate) => candidate.id);
  // A face already marked "not this performer" needs no new reject, so only fresh skips are sent.
  const similarReviewSkipIds = similarReviewCandidates
    .filter((candidate) => !candidate.rejectedForSourcePerformer && !isLinkedByReview(candidate, similarReview.choices))
    .map((candidate) => candidate.id);
  const similarReviewChangedIds = similarReviewCandidates
    .filter((candidate) => isLinkedByReview(candidate, similarReview.choices) !== linksByDefault(candidate))
    .map((candidate) => candidate.id);

  const similarFacesContent = (
    <section className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Similar Faces</h2>
          <p className="mt-1 text-sm text-secondary">Nearest neighbors from the face embedding index.</p>
        </div>
        <div className="text-xs text-muted">
          {similarLoadError
            ? "Unavailable"
            : `${similarFacesPage.totalCount} match${similarFacesPage.totalCount === 1 ? "" : "es"}`}
        </div>
      </div>

      <ListQueryState
        header={
          <DetailListToolbar
            filter={similarFilter}
            onFilterChange={setSimilarFilter}
            totalCount={similarFacesPage.totalCount}
            sortOptions={SIMILAR_SORT_OPTIONS}
            criteriaDefinitions={SIMILAR_FACE_CRITERIA}
            objectFilter={similarObjectFilter}
            onObjectFilterChange={setSimilarObjectFilter}
            zoomLevel={similarZoomLevel}
            onZoomChange={setSimilarZoomLevel}
            cardSizeEntityType="faces"
            showSearch
            allowInfinitePageSize
            displayMode={similarDisplayMode}
            onDisplayModeChange={(mode: DetailListDisplayMode) => {
              if (mode === "grid" || mode === "list") setSimilarDisplayMode(mode);
            }}
            availableDisplayModes={["grid", "list"]}
          />
        }
        isLoading={similarLoading}
        loadError={similarLoadError}
        isEmpty={similarFacesPage.totalCount === 0}
        onRetry={() => {
          void retrySimilar();
        }}
        loading={<div className="text-sm text-secondary">Loading similar faces...</div>}
        empty={
          <div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-secondary">
            {similarFaceFilters.linked !== undefined || similarFaceFilters.path
              ? "No similar faces match the current filters."
              : "No similar faces are available for this cluster yet."}
          </div>
        }
      >
        <>
          {similarReviewTarget ? (
            <SimilarReviewBar
              target={similarReviewTarget}
              linkCount={similarReviewLinkIds.length}
              skipCount={similarInfinitePageSize ? 0 : similarReviewSkipIds.length}
              pending={similarReviewPending}
              locked={similarReviewLocked}
              error={similarReviewError}
              infinitePageSize={similarInfinitePageSize}
              result={similarReview.result}
              onSubmit={() =>
                batchLinkSimilarMutation.mutate({
                  sourceFaceId: id,
                  performerId: similarReviewTarget.performerId,
                  faceIds: similarReviewLinkIds,
                  rejectFaceIds: similarReviewSkipIds,
                })
              }
              changedCount={similarInfinitePageSize ? 0 : similarReviewChangedIds.length}
              onResetChoices={() => {
                const shown = new Set(similarReviewChangedIds);
                setSimilarReview((current) => ({
                  faceId: id,
                  choices: current.faceId === id ? withoutChoices(current.choices, shown) : new Map(),
                  result: null,
                }));
              }}
              onShowUnlinkedOnly={
                similarFaceFilters.linked === false
                  ? undefined
                  : () => {
                      setSimilarObjectFilter((current) => ({ ...current, linkedCriterion: { value: false } }));
                      setSimilarFilter((current) => ({ ...current, page: 1 }));
                    }
              }
            />
          ) : null}
          <SimilarFacesView
            faces={similarFacesPage.items}
            displayMode={similarDisplayMode}
            onNavigate={onNavigate}
            canReadPerformers={canReadPerformers}
            reviewTarget={similarInfinitePageSize ? undefined : similarReviewTarget}
            choices={similarReview.choices}
            onToggleChoice={toggleSimilarChoice}
            reviewDisabled={similarReviewLocked}
            zoomLevel={similarZoomLevel}
            infinitePageSize={similarInfinitePageSize}
            hasNextPage={similarInfiniteQuery.hasNextPage}
            isFetchingNextPage={similarInfiniteQuery.isFetchingNextPage}
            loadMore={loadMoreSimilar}
          />
          <DetailListPagination
            filter={similarFilter}
            onFilterChange={setSimilarFilter}
            totalCount={similarFacesPage.totalCount}
            allowInfinitePageSize
          />
        </>
      </ListQueryState>
    </section>
  );

  const activeTabContent =
    activeTab === "overview" ? overviewContent : activeTab === "appearances" ? appearancesContent : similarFacesContent;

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
      {canWriteFace || canDeleteFace ? (
        <div className="relative" ref={actionsMenuRef}>
          <button
            type="button"
            onClick={() => setShowActionsMenu((current) => !current)}
            className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card text-secondary transition hover:border-accent hover:text-foreground"
            title="More actions"
          >
            <MoreVertical className="h-4 w-4" />
          </button>
          <FloatingActionMenu
            open={showActionsMenu}
            anchorRef={actionsMenuRef}
            onClose={() => setShowActionsMenu(false)}
            className="min-w-[180px] py-1"
          >
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
        entityType="face"
        entityId={face.id}
        backLabel={backLabel}
        onGoBack={goBack}
        backAlternative={backAlternative}
        imageUrl={face.coverImageUrl}
        imageCarouselUrls={heroImageUrls}
        imageCarouselIndex={heroImageIndex}
        onImageCarouselIndexChange={setHeroImageIndex}
        imageAlt={title}
        imageFallback={<Fingerprint className="h-14 w-14" />}
        title={titleWithProvenance}
        counts={[
          {
            key: "appearances",
            label: "Appearances",
            value: face.appearanceCount || faceAppearancesPage.totalCount,
            icon: <Eye className="h-4 w-4" />,
          },
          { key: "videos", label: "Videos", value: face.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "images", label: "Images", value: face.imageCount, icon: <Images className="h-4 w-4" /> },
        ]}
        metaRow={
          <>
            <span>Created {formatDate(face.createdAt)}</span>
            <span>Updated {formatDate(face.updatedAt)}</span>
            <FieldProvenanceHover fieldProvenance={face.fieldProvenance} fieldKey="performer_id">
              <span>{face.performerName || "Unlinked"}</span>
            </FieldProvenanceHover>
          </>
        }
        favorite={canEngageFace ? faceFavorite : undefined}
        favoritePending={faceFavoritePending}
        onFavoriteToggle={canEngageFace ? () => setFaceFavorite(!faceFavorite) : undefined}
        actions={faceActions}
      >
        <EntityDetailTabs tabs={tabs} activeTab={activeTab} onTabChange={setActiveTab} className="mb-4" />
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
          {face.performerId ? (
            <p className="text-xs text-secondary">
              While linked, this face is shown as {face.performerName || `Performer #${face.performerId}`}. The title is
              only displayed for unlinked faces, so it may still hold an earlier automatic match.
            </p>
          ) : null}
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
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Merge into another face
          </label>
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
            <p className="text-sm text-secondary">
              Type at least two characters to search merge targets by face label or linked performer name.
            </p>
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

      <EditModal
        open={isCreatePerformerModalOpen}
        onClose={() => setIsCreatePerformerModalOpen(false)}
        title={`Create performer from ${title}`}
      >
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
            <p className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">
              {String(createPerformerMutation.error.message ?? createPerformerMutation.error)}
            </p>
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

      <FaceSplitDialog
        open={splitAppearance != null}
        faceId={splitAppearance ? id : null}
        faceTitle={title}
        hostType={splitAppearance?.hostType ?? "video"}
        hostId={splitAppearance?.hostId ?? 0}
        onClose={() => setSplitAppearance(null)}
        onSplit={() => refreshAfterAppearanceEdit(splitAppearance?.hostType ?? "video")}
        onMarkNotPresent={splitAppearance ? () => confirmMarkNotPresent(splitAppearance) : undefined}
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
            suggestionDecisionMutation.mutate({
              performerId: value.performerId,
              decision: "accept",
              setPerformerImage: options?.setPerformerImage,
              ...readReferenceLinkInfo(value),
            });
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
          suggestionDecisionMutation.mutate({
            performerId: primaryPerformerId,
            decision: "merge",
            secondaryPerformerIds,
          });
          setComparingSuggestion(null);
        }}
        onNavigate={onNavigate}
      />
    </>
  );
}

function StatusPill({ icon, label, tone }: { icon: React.ReactNode; label: string; tone: "muted" | "accent" }) {
  const toneClassName =
    tone === "accent" ? "border-accent/30 bg-accent/10 text-accent" : "border-border bg-surface/70 text-secondary";

  return (
    <span className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 ${toneClassName}`}>
      {icon}
      {label}
    </span>
  );
}

function PerformerCandidateRow({
  performer,
  onSelect,
  disabled,
}: {
  performer: Performer;
  onSelect: () => void;
  disabled: boolean;
}) {
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
          <LoadedImage src={face.coverImageUrl} alt={title} className="h-full w-full object-cover" loading="lazy" />
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
          <span>
            {face.appearanceCount} appearance{face.appearanceCount === 1 ? "" : "s"}
          </span>
          <span aria-hidden>·</span>
          <span>
            {face.detectionCount} detection{face.detectionCount === 1 ? "" : "s"}
          </span>
        </div>
      </div>
      <span className="shrink-0 text-xs text-accent">Merge</span>
    </button>
  );
}

function FaceAppearancesGrid({
  appearances,
  onNavigate,
  onSplit,
  onMarkNotPresent,
  markingNotPresent,
  zoomLevel,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
}: {
  appearances: FaceAppearanceListItem[];
  onNavigate: (r: any) => void;
  onSplit?: (host: FaceAppearanceHost) => void;
  onMarkNotPresent?: (host: FaceAppearanceHost) => void;
  markingNotPresent?: FaceAppearanceHost;
  zoomLevel: number;
  infinitePageSize: boolean;
  hasNextPage?: boolean;
  isFetchingNextPage?: boolean;
  loadMore: () => void;
}) {
  return (
    <VirtualizedEntityGrid
      items={appearances}
      getItemKey={(appearance) => appearance.appearanceId}
      minCardWidth={`${getEntityCardMinWidthPx("faces", zoomLevel)}px`}
      virtualMinColumnWidth={getEntityCardMinWidthPx("faces", zoomLevel)}
      estimateRowHeight={280}
      gap={16}
      gapClassName="gap-4"
      infinitePageSize={infinitePageSize}
      hasNextPage={hasNextPage}
      isFetchingNextPage={isFetchingNextPage}
      loadMore={loadMore}
      renderItem={(appearance) => (
        <FaceAppearanceTile
          appearance={appearance}
          onClick={() => onNavigate({ page: appearance.hostType, id: appearance.hostId })}
        >
          {onSplit || onMarkNotPresent ? (
            <AppearanceCorrectionActions
              appearance={appearance}
              onSplit={onSplit}
              onMarkNotPresent={onMarkNotPresent}
              markingNotPresent={markingNotPresent}
              className="justify-center"
            />
          ) : null}
        </FaceAppearanceTile>
      )}
    />
  );
}

/**
 * Fixes for a cluster that picked up someone else in one video or image, offered where the wrong
 * appearance is noticed rather than only from that host's own page.
 */
function AppearanceCorrectionActions({
  appearance,
  onSplit,
  onMarkNotPresent,
  markingNotPresent,
  className = "",
}: {
  appearance: FaceAppearance;
  onSplit?: (host: FaceAppearanceHost) => void;
  onMarkNotPresent?: (host: FaceAppearanceHost) => void;
  markingNotPresent?: FaceAppearanceHost;
  className?: string;
}) {
  if (!onSplit && !onMarkNotPresent) return null;

  const hostNoun = appearance.hostType === "image" ? "image" : "video";
  const isMarking =
    markingNotPresent?.hostType === appearance.hostType && markingNotPresent.hostId === appearance.hostId;
  const buttonClassName =
    "inline-flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs text-secondary transition-colors disabled:cursor-not-allowed disabled:opacity-50";

  return (
    <div className={`flex shrink-0 flex-wrap items-center gap-1.5 ${className}`}>
      {onSplit ? (
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onSplit(appearance);
          }}
          title={`Separate a different person out of this face in this ${hostNoun}`}
          className={`${buttonClassName} hover:border-accent hover:text-accent`}
        >
          <Scissors className="h-3.5 w-3.5" />
          Separate
        </button>
      ) : null}
      {onMarkNotPresent ? (
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onMarkNotPresent(appearance);
          }}
          disabled={markingNotPresent != null}
          title={`This face is not actually in this ${hostNoun}`}
          className={`${buttonClassName} hover:border-red-400/60 hover:text-red-300`}
        >
          <UserX className="h-3.5 w-3.5" />
          {isMarking ? "Removing..." : "Not present"}
        </button>
      ) : null}
    </div>
  );
}

interface SimilarFaceLinkTarget {
  performerId: number;
  performerName: string;
}

function SimilarFacesView({
  faces: faceItems,
  displayMode,
  onNavigate,
  canReadPerformers,
  reviewTarget,
  choices,
  onToggleChoice,
  reviewDisabled,
  zoomLevel,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
}: {
  faces: FaceSimilar[];
  displayMode: "grid" | "list";
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  /** The performer linked to the face being viewed; its unlinked similar faces are reviewed for it. */
  reviewTarget?: SimilarFaceLinkTarget;
  choices: ReadonlyMap<number, boolean>;
  onToggleChoice: (face: FaceSimilar) => void;
  reviewDisabled: boolean;
  zoomLevel: number;
  infinitePageSize: boolean;
  hasNextPage?: boolean;
  isFetchingNextPage?: boolean;
  loadMore: () => void;
}) {
  // A candidate the batch leaves out (skipped now, or still marked as someone else) is dimmed.
  const isSkipped = (face: FaceSimilar) =>
    reviewTarget != null && isSimilarReviewCandidate(face) && !isLinkedByReview(face, choices);
  const reviewToggle = (face: FaceSimilar, className: string) => (
    <SimilarReviewToggle
      face={face}
      target={reviewTarget}
      linked={!isSkipped(face)}
      onToggle={onToggleChoice}
      disabled={reviewDisabled}
      className={className}
    />
  );

  if (displayMode === "list") {
    return (
      <div className="overflow-hidden rounded-lg border border-border bg-card/60">
        <div className="divide-y divide-border/70">
          {faceItems.map((face) => {
            const title = faceDisplayName(face);
            return (
              <div
                key={face.id}
                className={`flex items-center gap-3 px-3 py-2 text-sm transition-colors hover:bg-card-hover ${isSkipped(face) ? "opacity-60" : ""}`}
              >
                <button
                  type="button"
                  onClick={() => onNavigate(similarFaceRoute(face.id))}
                  className="flex min-w-0 flex-1 items-center gap-3 text-left"
                >
                  <div className="h-12 w-12 shrink-0 overflow-hidden rounded bg-surface">
                    {face.coverImageUrl ? (
                      <LoadedImage
                        src={face.coverImageUrl}
                        alt=""
                        className="h-full w-full object-cover"
                        loading="lazy"
                      />
                    ) : (
                      <div className="flex h-full w-full items-center justify-center text-muted">
                        <Fingerprint className="h-4 w-4" />
                      </div>
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-medium text-accent">{title}</div>
                    <div className="mt-0.5 truncate text-xs text-muted">
                      Distance {face.distance.toFixed(3)} · {face.appearanceCount} appearances
                    </div>
                  </div>
                  {face.performerId ? (
                    <span className={`shrink-0 text-xs ${canReadPerformers ? "text-accent" : "text-muted"}`}>
                      {face.performerName || `Performer #${face.performerId}`}
                    </span>
                  ) : null}
                </button>
                {reviewToggle(face, "w-32 shrink-0")}
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  return (
    <VirtualizedEntityGrid
      items={faceItems}
      getItemKey={(candidate) => candidate.id}
      minCardWidth={`${getEntityCardMinWidthPx("faces", zoomLevel)}px`}
      virtualMinColumnWidth={getEntityCardMinWidthPx("faces", zoomLevel)}
      estimateRowHeight={360}
      gap={16}
      gapClassName="gap-4"
      infinitePageSize={infinitePageSize}
      hasNextPage={hasNextPage}
      isFetchingNextPage={isFetchingNextPage}
      loadMore={loadMore}
      renderItem={(candidate) => (
        <div className={isSkipped(candidate) ? "opacity-60" : undefined}>
          <SimilarFaceTile face={candidate} onNavigate={onNavigate} canReadPerformers={canReadPerformers}>
            {reviewToggle(candidate, "mt-2 w-full")}
          </SimilarFaceTile>
        </div>
      )}
    />
  );
}

function describeFaceDeleteImpact(deleteImpact: FaceDeleteImpact) {
  const coverImageSummary = deleteImpact.hasCoverImage ? "1 cover image" : "no cover image";
  return `Deletes ${formatCount(deleteImpact.detectionCount, "detection")}, ${formatCount(deleteImpact.embeddingCount, "embedding")}, ${formatCount(deleteImpact.segmentCount, "timeline segment")}, and ${coverImageSummary}.`;
}

function formatCount(count: number, singular: string, plural = `${singular}s`) {
  return `${count} ${count === 1 ? singular : plural}`;
}

/**
 * The review state of one similar face for the viewed face's performer. A new candidate is linked with the
 * batch until it is skipped. A face already turned down for that performer stays "Not <performer>" until the
 * reviewer chooses to link it after all, which the batch then does, dropping the earlier reject.
 */
function SimilarReviewToggle({
  face,
  target,
  linked,
  onToggle,
  disabled,
  className = "",
}: {
  face: FaceSimilar;
  target?: SimilarFaceLinkTarget;
  linked: boolean;
  onToggle: (face: FaceSimilar) => void;
  disabled: boolean;
  className?: string;
}) {
  if (!target || !isSimilarReviewCandidate(face)) return null;

  const title = faceDisplayName(face);
  const name = target.performerName;
  const saved = Boolean(face.rejectedForSourcePerformer);
  const label = linked ? "Will link" : saved ? `Not ${name}` : "Skipped";
  return (
    <button
      type="button"
      onClick={(event) => {
        event.preventDefault();
        event.stopPropagation();
        onToggle(face);
      }}
      disabled={disabled}
      // The name leads with the visible text, so a voice command naming what is on screen reaches it.
      aria-label={linked ? `${label} ${title}, skip it` : `${label} ${title}, link it after all`}
      title={
        linked
          ? `Will be linked to ${name}. Click to skip it if it is someone else.`
          : saved
            ? `You marked this face as not ${name}. Click to link it to ${name} after all.`
            : `Skipped: will be remembered as not ${name}. Click to link it after all.`
      }
      className={`inline-flex items-center justify-center gap-1.5 rounded-lg border px-2 py-1 text-xs font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${
        linked
          ? "border-emerald-500/50 bg-emerald-500/10 text-emerald-300 hover:bg-emerald-500/20"
          : "border-border text-secondary hover:border-accent"
      } ${className}`}
    >
      {linked ? <Link2 className="h-3.5 w-3.5 shrink-0" /> : <UserX className="h-3.5 w-3.5 shrink-0" />}
      <span className="truncate">{label}</span>
    </button>
  );
}

/**
 * One fixed place to act on the review: how many faces on the page will be linked and skipped, and the
 * button that links them all, so the reviewer never chases a button that moves from tile to tile.
 */
function SimilarReviewBar({
  target,
  linkCount,
  skipCount,
  changedCount,
  pending,
  locked,
  error,
  result,
  onSubmit,
  onResetChoices,
  onShowUnlinkedOnly,
  infinitePageSize,
}: {
  target: SimilarFaceLinkTarget;
  linkCount: number;
  skipCount: number;
  /** Faces on this page whose default the reviewer changed, which "Reset choices" puts back. */
  changedCount: number;
  pending: boolean;
  /** Saving, or the list is still showing an earlier query: the controls wait. */
  locked: boolean;
  error: unknown;
  result: FaceBatchLinkResult | null;
  onSubmit: () => void;
  onResetChoices: () => void;
  /** Narrows the list to unlinked faces, so faces already reviewed drop out and the next batch moves up. */
  onShowUnlinkedOnly?: () => void;
  /** Endless scrolling has no page to review, so the batch waits for a page size to be picked. */
  infinitePageSize: boolean;
}) {
  const hasCandidates = !infinitePageSize && linkCount + skipCount > 0;
  const name = target.performerName;
  const actionLabel = pending
    ? "Saving..."
    : linkCount > 0
      ? `Link ${formatCount(linkCount, "face")} to ${name}`
      : `Mark ${formatCount(skipCount, "face")} as not ${name}`;

  return (
    <div
      role="region"
      aria-label={`Review similar faces for ${name}`}
      className="sticky top-14 z-30 flex flex-wrap items-center gap-3 rounded-xl border border-emerald-500/30 bg-card/95 px-4 py-3 shadow-lg backdrop-blur"
    >
      <div className="min-w-0 flex-1 text-sm">
        {infinitePageSize ? (
          <span className="text-secondary">Pick a page size to review similar faces for {name} in batches.</span>
        ) : !hasCandidates ? (
          <span className="text-secondary">No faces on this page will be linked to {name}.</span>
        ) : (
          <span className="text-foreground">
            <span className="font-medium">{formatCount(linkCount, "face")}</span> on this page will be linked to {name}
            {skipCount > 0 ? (
              <>
                , <span className="font-medium">{skipCount}</span> skipped as someone else
              </>
            ) : null}
            . Skip any face that is not {name}.
          </span>
        )}
        {/* Mounted before it has anything to say, so assistive technology announces the outcome. */}
        <div role="status" className="text-xs text-secondary">
          {result ? <SimilarReviewOutcome result={result} performerName={name} /> : null}
        </div>
        {error ? (
          <div role="alert" className="mt-1 text-xs text-red-400">
            {error instanceof Error ? error.message : "Could not save the review."}
          </div>
        ) : null}
      </div>
      {onShowUnlinkedOnly ? (
        <button
          type="button"
          onClick={onShowUnlinkedOnly}
          disabled={locked}
          className="rounded-lg border border-border px-3 py-1.5 text-xs text-secondary transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
        >
          Show unlinked only
        </button>
      ) : null}
      {changedCount > 0 ? (
        <button
          type="button"
          onClick={onResetChoices}
          disabled={locked}
          className="rounded-lg border border-border px-3 py-1.5 text-xs text-secondary transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
        >
          Reset choices
        </button>
      ) : null}
      {hasCandidates ? (
        <button
          type="button"
          onClick={onSubmit}
          disabled={locked}
          className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <Link2 className="h-4 w-4" />
          {actionLabel}
        </button>
      ) : null}
    </div>
  );
}

function SimilarReviewOutcome({ result, performerName }: { result: FaceBatchLinkResult; performerName: string }) {
  const parts = [
    result.linked.length > 0 ? `Linked ${formatCount(result.linked.length, "face")} to ${performerName}.` : null,
    result.rejected.length > 0
      ? `Remembered ${formatCount(result.rejected.length, "face")} as not ${performerName}.`
      : null,
    result.skipped.length > 0
      ? `Left ${formatCount(result.skipped.length, "face")} unchanged: ${[...new Set(result.skipped.map((item) => item.reason))].join(" ")}`
      : null,
    result.failed.length > 0 ? `${formatCount(result.failed.length, "face")} could not be linked.` : null,
  ].filter(Boolean);
  return <p className="mt-1">{parts.join(" ")}</p>;
}

function SimilarFaceTile({
  face,
  onNavigate,
  canReadPerformers,
  children,
}: {
  face: FaceSimilar;
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  children?: React.ReactNode;
}) {
  return (
    <FaceTile face={face} detailTab="similar" onClick={() => onNavigate(similarFaceRoute(face.id))}>
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
        {children}
      </div>
    </FaceTile>
  );
}
