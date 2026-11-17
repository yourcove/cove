import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Camera,
  ChevronLeft,
  ChevronRight,
  Clapperboard,
  Clock,
  ExternalLink,
  Film,
  Image,
  MoreVertical,
  Sparkles,
  Trash2,
} from "lucide-react";
import { entityImages, videos, segmentLibrary } from "../api/client";
import type { Video, SegmentRecord } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { saveButtonTone } from "../components/EditModal";
import {
  VideoPlayer,
  type FaceOverlayInfo,
  type VideoPlayerPlaybackControls,
  type VideoPlayerSeek,
} from "../components/VideoPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { ListLoadError } from "../components/ListLoadError";
import { SegmentKindIcon, segmentKindIconKind } from "../components/EntityCards";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { FieldProvenanceHover } from "../components/shared";
import { EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useAppConfig } from "../state/AppConfigContext";
import { SegmentVisualSimilarityPanel, useSegmentVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { buildSubVideoCreate } from "../utils/subVideoCreation";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { formatSegmentTime, parseSegmentTimeInput } from "../utils/segmentTime";
import { SegmentOverview, SegmentSummary, type SegmentMembership } from "./segments/SegmentOverview";
import {
  formatConfidencePercent,
  type NearbyGroup,
  type NearbyItem,
  LoadingNextMarker,
  SegmentHeaderLinks,
  SuggestedName,
} from "./segments/SegmentDetailSections";
import { useSegmentFace } from "./segments/useSegmentFace";
import { RawSegmentIcon, SegmentIcon } from "../components/segmentViewIcons";
import {
  buildRawSegmentTitle,
  formatShownTitle,
  rawSegmentFaceId,
  rawSegmentFaceName,
  rawSegmentPlaceholderFaceId,
} from "./segments/segmentDisplayUtils";
import { useListedFaceNames } from "./segments/useListedFaceNames";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type SegmentTab = "overview" | "similar" | "metadata";
type EditableSegmentKind = "tag" | "performer" | "face";

const EDITABLE_SEGMENT_KIND_OPTIONS: Array<{ value: EditableSegmentKind; label: string }> = [
  { value: "tag", label: "Tag" },
  { value: "performer", label: "Performer" },
  { value: "face", label: "Face" },
];

function getEditableSegmentKind(segment: SegmentRecord): EditableSegmentKind {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  if (normalizedKind === "performer") return "performer";
  if (normalizedKind.includes("face")) return "face";
  if (segment.performerId != null || (segment.refId != null && normalizedKind === "performer")) return "performer";
  return "tag";
}

function getSegmentPerformerId(segment: SegmentRecord) {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  if (segment.performerId != null) return segment.performerId;
  return normalizedKind === "performer" && segment.refId != null ? Number(segment.refId) : null;
}

function getSegmentFaceId(segment: SegmentRecord) {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  return normalizedKind.includes("face") && segment.refId != null ? Number(segment.refId) : null;
}

function isHexColorValue(value?: string | null) {
  return /^#[0-9a-fA-F]{6}$/.test(value?.trim() ?? "");
}

function getSegmentContextDescriptor(segment: SegmentRecord) {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  const performerId = getSegmentPerformerId(segment);
  const faceId = getSegmentFaceId(segment);
  const referenceLabel = segment.performerName?.trim() || segment.refLabel?.trim() || segment.tagName?.trim();

  if (segment.tagId != null || segment.tagName?.trim()) {
    return {
      label: "Tag",
      title: "Next With Same Tag",
      emptyMessage: segment.tagName?.trim()
        ? `No later ${segment.tagName.trim()} segment is in this video.`
        : "This segment does not have a tag to follow.",
      matchKey: segment.tagId != null ? `tag:${segment.tagId}` : `tag-name:${segment.tagName?.trim().toLowerCase()}`,
    };
  }

  if (performerId != null || (normalizedKind === "performer" && referenceLabel)) {
    return {
      label: "Performer",
      title: "Next With Same Performer",
      emptyMessage: referenceLabel
        ? `No later ${referenceLabel} segment is in this video.`
        : "This segment does not have a performer to follow.",
      matchKey: performerId != null ? `performer:${performerId}` : `performer-name:${referenceLabel?.toLowerCase()}`,
    };
  }

  if (faceId != null || (normalizedKind.includes("face") && referenceLabel)) {
    return {
      label: "Face",
      title: "Next With Same Face",
      emptyMessage: referenceLabel
        ? `No later ${referenceLabel} segment is in this video.`
        : "This segment does not have a face to follow.",
      matchKey: faceId != null ? `face:${faceId}` : `face-name:${referenceLabel?.toLowerCase()}`,
    };
  }

  if (segment.refId != null || referenceLabel) {
    const label = normalizedKind ? normalizedKind[0].toUpperCase() + normalizedKind.slice(1) : "Reference";
    return {
      label,
      title: `Next With Same ${label}`,
      emptyMessage: referenceLabel
        ? `No later ${referenceLabel} segment is in this video.`
        : `This segment does not have a ${label.toLowerCase()} to follow.`,
      matchKey:
        segment.refId != null
          ? `${normalizedKind || "reference"}:${segment.refId}`
          : `${normalizedKind || "reference"}-name:${referenceLabel?.toLowerCase()}`,
    };
  }

  return null;
}

function getSegmentContextMatchKey(segment: SegmentRecord) {
  return getSegmentContextDescriptor(segment)?.matchKey;
}

function setStartTimeFromSeconds(
  seconds: number,
  setStartSec: (value: number) => void,
  setStartText: (value: string) => void,
) {
  const normalized = Math.max(0, seconds);
  setStartSec(normalized);
  setStartText(formatSegmentTime(normalized));
}

function setEndTimeFromSeconds(
  seconds: number | "",
  setEndSec: (value: number | "") => void,
  setEndText: (value: string) => void,
) {
  if (seconds === "") {
    setEndSec("");
    setEndText("");
    return;
  }

  const normalized = Math.max(0, seconds);
  setEndSec(normalized);
  setEndText(formatSegmentTime(normalized));
}

export function SegmentDetailPage({ id, onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWriteSegments = canWriteEntity("segment", hasPermission);
  const canDeleteSegments = canDeleteEntity("segment", hasPermission);
  const canReadVideos = canReadEntity("video", hasPermission);
  const canWriteVideos = canWriteEntity("video", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canReadFaces = canReadEntity("face", hasPermission);
  const canWriteFaces = canWriteEntity("face", hasPermission);
  const { backLabel, goBack, backAlternative, goToOrigin } = useBackNavigation({ page: "segments" }, onNavigate);

  const {
    data: segment,
    isLoading,
    isPlaceholderData: loadingNextSegment,
    error: segmentError,
    refetch: retrySegment,
  } = useQuery({
    queryKey: ["segment", id],
    queryFn: () => segmentLibrary.get(id),
    // Moving to another segment keeps this one on screen until the next loads, so the player, the open tab and the
    // sidebar scroll stay put instead of passing through a loading skeleton.
    placeholderData: keepPreviousData,
  });
  const segmentLoadError = getLoadError(segment, segmentError);

  const [title, setTitle] = useState("");
  const [kind, setKind] = useState<EditableSegmentKind>("tag");
  const [colorHint, setColorHint] = useState("");
  const [startSec, setStartSec] = useState(0);
  const [endSec, setEndSec] = useState<number | "">("");
  const [startText, setStartText] = useState("0:00");
  const [endText, setEndText] = useState("");
  const [selectedTagId, setSelectedTagId] = useState<number | null>(null);
  const [selectedPerformerId, setSelectedPerformerId] = useState<number | null>(null);
  const [selectedFaceId, setSelectedFaceId] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<SegmentTab>("overview");
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const [segmentVideoTime, setSegmentVideoTime] = useState(0);
  const titleInputRef = useRef<HTMLInputElement | null>(null);
  const opsMenuRef = useRef<HTMLDivElement | null>(null);

  const resetEditState = () => {
    if (!segment) {
      return;
    }

    setTitle(segment.title ?? "");
    setKind(getEditableSegmentKind(segment));
    setColorHint(segment.colorHint ?? "");
    setStartTimeFromSeconds(segment.startSec, setStartSec, setStartText);
    setEndTimeFromSeconds(segment.endSec ?? "", setEndSec, setEndText);
    setSelectedTagId(segment.tagId ?? null);
    setSelectedPerformerId(getSegmentPerformerId(segment));
    setSelectedFaceId(getSegmentFaceId(segment));
    setSegmentVideoTime(segment.startSec);
  };

  const [prevSegment, setPrevSegment] = useState<typeof segment>(undefined);
  if (segment !== prevSegment) {
    // Dialogs opened for one segment close when another one is shown.
    if (segment?.id !== prevSegment?.id) {
      setConfirmDelete(false);
      setCoverOpen(false);
      setShowOpsMenu(false);
    }
    setPrevSegment(segment);
    resetEditState();
  }

  const {
    data: siblingSegmentsData,
    error: siblingSegmentsError,
    refetch: retrySiblingSegments,
  } = useQuery({
    // Shared with the video page, so moving between segments of one video does not reload them.
    queryKey: ["video", segment?.hostId, "segments"],
    queryFn: () => videos.segments.list(segment!.hostId),
    // Only video segments have siblings to list; other hosts have no video segment list to read.
    enabled: !!segment && segment.hostType === "video",
  });
  const siblingSegmentsLoadError = getLoadError(siblingSegmentsData, siblingSegmentsError);
  const siblingSegments = useMemo(() => siblingSegmentsData ?? [], [siblingSegmentsData]);
  const { data: playbackVideo, isLoading: playbackVideoLoading } = useQuery({
    queryKey: ["video", segment?.hostId],
    queryFn: () => videos.get(segment!.hostId),
    enabled: !!segment && segment.hostType === "video" && canReadVideos,
  });
  const segmentFace = useSegmentFace(segment ?? undefined, canReadFaces);
  const { data: videoSpans } = useQuery({
    // Shared with the video page's spans for the default profile, which segment edits there refresh.
    queryKey: ["video", segment?.hostId, "resolved-spans", undefined],
    queryFn: () => videos.segments.spans(segment!.hostId),
    enabled: !!segment && segment.hostType === "video",
  });
  // How the Segments view shows this raw segment under the default display profile.
  const membership = useMemo<SegmentMembership>(() => {
    if (!segment || !videoSpans) return { state: "loading" };
    const span = videoSpans.spans.find((candidate) => candidate.segmentIds.includes(segment.id));
    return span ? { state: "shown", span, profileId: videoSpans.profileId } : { state: "hidden" };
  }, [segment, videoSpans]);
  const placeholderFaceIds = useMemo(
    () => siblingSegments.flatMap((item) => rawSegmentPlaceholderFaceId(item) ?? []),
    [siblingSegments],
  );
  const siblingFaceNames = useListedFaceNames(placeholderFaceIds, canReadFaces);
  const playerSeekRef = useRef<VideoPlayerSeek | null>(null);
  const playerControlsRef = useRef<VideoPlayerPlaybackControls | null>(null);
  const registerPlayerSeek = useCallback((seek: VideoPlayerSeek) => {
    playerSeekRef.current = seek;
  }, []);
  const registerPlayerControls = useCallback((controls: VideoPlayerPlaybackControls) => {
    playerControlsRef.current = controls;
    return () => {
      if (playerControlsRef.current === controls) playerControlsRef.current = null;
    };
  }, []);
  const { engagementById: videoEngagement } = useEntityEngagementBatch(
    "video",
    segment?.hostType === "video" && segment.hostId != null ? [segment.hostId] : [],
  );
  const normalizedTitle = title.trim() || undefined;
  const normalizedKind = kind;
  const normalizedColorHint = colorHint.trim() || undefined;
  // startSec/endSec hold the exact time behind each input: the stored or chosen time, or the typed text once it
  // parses. Saving them instead of re-parsing the displayed text keeps untouched times at full precision.
  const parsedStart = parseSegmentTimeInput(startText);
  const endTextIsBlank = endText.trim() === "";
  const parsedEnd = endTextIsBlank ? null : parseSegmentTimeInput(endText);
  const endTimeIsValid = endTextIsBlank || parsedEnd != null;
  const normalizedEndSec = endSec === "" ? undefined : endSec;
  const hasSelectedReference =
    kind === "tag"
      ? selectedTagId != null
      : kind === "performer"
        ? selectedPerformerId != null
        : selectedFaceId != null;
  const canSave =
    canWriteSegments &&
    parsedStart != null &&
    parsedStart >= 0 &&
    endTimeIsValid &&
    (normalizedEndSec == null || normalizedEndSec >= startSec) &&
    hasSelectedReference;

  const hasChanges =
    !!segment &&
    (normalizedTitle !== (segment.title?.trim() || undefined) ||
      kind !== getEditableSegmentKind(segment) ||
      normalizedColorHint !== (segment.colorHint?.trim() || undefined) ||
      startSec !== Math.max(0, segment.startSec) ||
      normalizedEndSec !== (segment.endSec == null ? undefined : Math.max(0, segment.endSec)) ||
      // Only the reference of the chosen kind is saved.
      (kind === "tag"
        ? selectedTagId !== (segment.tagId ?? null)
        : kind === "performer"
          ? selectedPerformerId !== getSegmentPerformerId(segment)
          : selectedFaceId !== getSegmentFaceId(segment)));

  const invalidateSegmentQueries = (current?: SegmentRecord | null, nextTagId?: number | null) => {
    queryClient.invalidateQueries({ queryKey: ["segments"] });
    // The edited segment, which can differ from the route while the next one is loading.
    queryClient.invalidateQueries({ queryKey: ["segment", current?.id ?? id] });
    // Span cards on the Segments page and dashboard rows version their image by the segment.
    queryClient.invalidateQueries({ queryKey: ["segments-page"] });
    queryClient.invalidateQueries({ queryKey: ["front-page-saved"] });

    if (current?.hostId != null) {
      queryClient.invalidateQueries({ queryKey: ["video", current.hostId, "segments"] });
      queryClient.invalidateQueries({ queryKey: ["video", current.hostId, "resolved-spans"] });
      queryClient.invalidateQueries({ queryKey: ["video", current.hostId] });
    }

    if (current?.tagId != null) {
      queryClient.invalidateQueries({ queryKey: ["tag", current.tagId] });
    }

    if (nextTagId != null) {
      queryClient.invalidateQueries({ queryKey: ["tag", nextTagId] });
    }
  };

  // Each mutation is handed the segment it acts on, so its success refreshes that segment even when another one is
  // on screen by the time it finishes.
  const updateMutation = useMutation({
    mutationFn: async ({ segment }: { segment: SegmentRecord; nextTagId: number | null }) => {
      if (parsedStart == null) {
        throw new Error("Segment start is invalid");
      }

      const nextTagId = kind === "tag" ? (selectedTagId ?? undefined) : undefined;
      const nextRefId =
        kind === "performer"
          ? (selectedPerformerId ?? undefined)
          : kind === "face"
            ? (selectedFaceId ?? undefined)
            : undefined;

      return videos.segments.update(segment.hostId, segment.id, {
        startSec,
        endSec: normalizedEndSec,
        tagId: nextTagId,
        kind: normalizedKind,
        refId: nextRefId,
        payload: segment.payload,
        sourceKey: segment.sourceKey || "user",
        sourceRunId: segment.sourceRunId,
        confidence: segment.confidence,
        title: normalizedTitle,
        colorHint: normalizedColorHint,
      });
    },
    onSuccess: (_updated, edited) => {
      invalidateSegmentQueries(edited.segment, edited.nextTagId);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (deleted: SegmentRecord) => videos.segments.delete(deleted.hostId, deleted.id),
    onSuccess: (_result, deleted) => {
      invalidateSegmentQueries(deleted);
      setConfirmDelete(false);
      goToOrigin();
    },
  });

  const setSegmentCoverMutation = useMutation({
    mutationFn: async ({ target, atSeconds }: { target: SegmentRecord; atSeconds?: number }) =>
      entityImages.setSegmentCoverFromFrame(target.id, atSeconds),
    onSuccess: (_result, { target }) => {
      invalidateSegmentQueries(target);
      setCoverOpen(false);
    },
  });

  const createSubVideoMutation = useMutation({
    mutationFn: async () => {
      if (!segment || segment.hostType !== "video" || !playbackVideo) {
        throw new Error("Segment is not video-backed");
      }

      const clipEndSec = segment.endSec ?? playbackVideo?.files[0]?.duration;
      if (clipEndSec == null || clipEndSec <= segment.startSec) {
        throw new Error("Segment needs an end time before it can become a video");
      }

      return videos.createSubVideo(
        segment.hostId,
        buildSubVideoCreate(
          playbackVideo,
          {
            startSec: segment.startSec,
            endSec: clipEndSec,
          },
          {
            // A suggested name is only a guess, so the new video keeps the name the segment was stored with.
            title: displayTitleIsSuggestion ? buildRawSegmentTitle(segment) : displayTitle,
            tagIds: segment.tagId ? [segment.tagId] : undefined,
          },
        ),
      );
    },
    onSuccess: (newVideo) => {
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      queryClient.invalidateQueries({ queryKey: ["video", segment?.hostId] });
      onNavigate({ page: "video", id: newVideo.id });
    },
  });

  // Named as the segment cards name it: a given title, else its tag, performer or face, with a face known only by
  // its placeholder key read by its current name or, flagged as such, its suggested performer.
  // An ignored or merged face is no longer matched to performers, so it is not named after its old suggestion.
  const shownFaceName = segmentFace.shownName;
  const displayTitle = shownFaceName?.name ?? (segment ? buildRawSegmentTitle(segment) : "Segment");
  const displayTitleIsSuggestion = !!shownFaceName?.suggested;
  const displayTitleSpoken = formatShownTitle(displayTitle, displayTitleIsSuggestion);
  // The same icon a card shows for the segment: a tag segment is marked as one only while it is named after its tag.
  const displayKindIcon = segment
    ? segmentKindIconKind(segment.kind, !segment.tagName || segment.tagName === displayTitle)
    : null;
  const orderedSiblingSegments = useMemo(
    () =>
      [...siblingSegments].sort(
        (left, right) =>
          left.startSec - right.startSec ||
          (left.endSec ?? left.startSec) - (right.endSec ?? right.startSec) ||
          left.id - right.id,
      ),
    [siblingSegments],
  );
  const contextDescriptor = useMemo(() => (segment ? getSegmentContextDescriptor(segment) : null), [segment]);
  const videoContext = useMemo(() => {
    if (!segment) {
      return {
        currentIndex: -1,
        previous: [] as SegmentRecord[],
        next: [] as SegmentRecord[],
        nextSameReference: undefined as SegmentRecord | undefined,
        intersecting: [] as SegmentRecord[],
      };
    }

    const currentIndex = orderedSiblingSegments.findIndex((item) => item.id === segment.id);
    const currentEnd = segment.endSec ?? segment.startSec;
    const previous = orderedSiblingSegments
      .filter((item) => item.id !== segment.id && (item.endSec ?? item.startSec) <= segment.startSec)
      .slice(-2);
    const next = orderedSiblingSegments
      .filter((item) => item.id !== segment.id && item.startSec >= currentEnd)
      .slice(0, 2);
    const intersectsCurrent = (item: SegmentRecord) => {
      const itemEnd = item.endSec ?? item.startSec;
      return item.id !== segment.id && item.startSec < currentEnd && itemEnd > segment.startSec;
    };
    const currentContextMatchKey = getSegmentContextMatchKey(segment);
    const nextSameReference =
      currentContextMatchKey == null
        ? undefined
        : orderedSiblingSegments.find(
            (item) =>
              item.id !== segment.id &&
              getSegmentContextMatchKey(item) === currentContextMatchKey &&
              item.startSec >= currentEnd,
          );
    const intersecting = orderedSiblingSegments.filter(intersectsCurrent).slice(0, 6);

    return {
      currentIndex,
      previous,
      next,
      nextSameReference,
      intersecting,
    };
  }, [orderedSiblingSegments, segment]);
  const previousSegment = videoContext.previous.at(-1);
  const nextSegment = videoContext.next[0];
  const canCreateSubVideo =
    !!segment &&
    segment.hostType === "video" &&
    !!playbackVideo &&
    canReadVideos &&
    canWriteVideos &&
    (segment.endSec != null || (playbackVideo?.files[0]?.duration ?? 0) > segment.startSec);
  const canSetSegmentCover = !!segment && segment.hostType === "video" && canWriteSegments && canReadVideos;
  const coverActionPending = setSegmentCoverMutation.isPending;
  const hasVisualSimilarity = useSegmentVisualSimilarityAvailable({
    videoId: segment?.hostType === "video" ? segment.hostId : undefined,
    startSec: segment?.startSec,
    endSec: segment?.endSec ?? undefined,
  });
  const tabs = useMemo(() => {
    return [
      { key: "overview", label: "Overview" },
      ...(hasVisualSimilarity ? [{ key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> }] : []),
      { key: "metadata", label: canWriteSegments ? "Edit" : "Metadata" },
    ];
  }, [canWriteSegments, hasVisualSimilarity]);
  const segmentKeyboardShortcuts = useMemo(() => {
    if (!segment) {
      return [];
    }

    return [
      {
        id: "detail.edit",
        key: "e",
        description: canWriteSegments ? "Edit raw segment" : "Open raw segment details",
        handler: () => {
          setActiveTab("metadata");
          if (canWriteSegments) {
            window.setTimeout(() => titleInputRef.current?.focus(), 0);
          }
        },
      },
      {
        id: "detail.segment.parent",
        key: "s",
        description: "Open video at this segment",
        handler: () => {
          if (segment.hostType === "video" && canReadVideos) {
            onNavigate(buildVideoRouteForSegment(segment));
          }
        },
      },
      {
        id: "detail.previous",
        key: "[",
        description: "Open previous raw segment",
        handler: () => {
          if (previousSegment) {
            onNavigate({ page: "segment", id: previousSegment.id });
          }
        },
      },
      {
        id: "detail.next",
        key: "]",
        description: "Open next raw segment",
        handler: () => {
          if (nextSegment) {
            onNavigate({ page: "segment", id: nextSegment.id });
          }
        },
      },
    ];
  }, [canReadVideos, canWriteSegments, nextSegment, onNavigate, previousSegment, segment]);
  useDocumentTitle(segment ? displayTitleSpoken : null);

  useEffect(() => {
    const handler = (event: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(event.target as Node)) {
        setShowOpsMenu(false);
      }
    };

    if (showOpsMenu) {
      document.addEventListener("mousedown", handler);
    }

    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(segmentLoadError)) {
    return <div className="py-16 text-center text-secondary">Segment not found</div>;
  }

  if (segmentLoadError) {
    return (
      <ListLoadError
        error={segmentLoadError}
        onRetry={() => {
          void retrySegment();
        }}
        title="Could not load segment"
        className="mx-0 mt-0"
      />
    );
  }

  if (!segment) {
    return <div className="py-16 text-center text-secondary">Segment not found</div>;
  }

  const displayTitleWithProvenance = (
    <FieldProvenanceHover
      fieldProvenance={segment.fieldProvenance}
      fieldKey={["title", "tag_id", "performer_id", "ref_id", "kind"]}
    >
      <span className="inline-flex items-center gap-2">
        {displayKindIcon ? (
          // The section below says what kind of segment this is; here the icon only echoes it.
          <span aria-hidden className="inline-flex">
            <SegmentKindIcon kind={displayKindIcon} className="h-5 w-5 shrink-0 text-secondary" />
          </span>
        ) : null}
        <SuggestedName name={displayTitle} suggested={displayTitleIsSuggestion} markerClassName="text-xs" />
      </span>
    </FieldProvenanceHover>
  );
  const subtitleWithProvenance = (
    <>
      <FieldProvenanceHover fieldProvenance={segment.fieldProvenance} fieldKey={["start_sec", "end_sec"]}>
        {formatSegmentRange(segment.startSec, segment.endSec)}
      </FieldProvenanceHover>
      <span> • {formatSegmentDuration(segment.startSec, segment.endSec)}</span>
    </>
  );

  const editContent = (
    <section className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">
            {canWriteSegments ? "Edit Raw Segment" : "Raw Segment Details"}
          </h2>
          <p className="mt-1 text-sm text-secondary">
            {canWriteSegments
              ? "Update the timing, metadata, and reference assignment for this segment."
              : "You have read access to this segment, but not write access."}
          </p>
        </div>
      </div>

      {canWriteSegments ? (
        <div className="mt-4 grid gap-4 lg:grid-cols-2">
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Title
            <input
              ref={titleInputRef}
              type="text"
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder="Optional segment title"
              className="mt-2 w-full rounded-lg border border-border bg-input px-3 py-2 text-sm font-normal text-foreground outline-none focus:border-accent"
            />
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Kind
            <select
              value={kind}
              onChange={(event) => {
                const nextKind = event.target.value as EditableSegmentKind;
                setKind(nextKind);
                if (nextKind !== "tag") setSelectedTagId(null);
                if (nextKind !== "performer") setSelectedPerformerId(null);
                if (nextKind !== "face") setSelectedFaceId(null);
              }}
              className="mt-2 w-full rounded-lg border border-border bg-input px-3 py-2 text-sm font-normal text-foreground outline-none focus:border-accent"
            >
              {EDITABLE_SEGMENT_KIND_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Start
            <div className="mt-2 flex gap-1">
              <input
                type="text"
                inputMode="decimal"
                placeholder="0:00"
                value={startText}
                onChange={(event) => {
                  const next = event.target.value;
                  setStartText(next);
                  const parsed = parseSegmentTimeInput(next);
                  if (parsed != null) setStartSec(parsed);
                }}
                onBlur={() => setStartText(formatSegmentTime(startSec))}
                className="min-w-0 flex-1 rounded-lg border border-border bg-input px-3 py-2 font-mono text-sm font-normal text-foreground outline-none focus:border-accent"
              />
              <button
                type="button"
                onClick={() => setStartTimeFromSeconds(segmentVideoTime, setStartSec, setStartText)}
                className="inline-flex items-center justify-center rounded-lg border border-border px-3 text-secondary hover:text-foreground"
                title="Use current time"
                aria-label="Use current time for segment start"
              >
                <Clock className="h-4 w-4" />
              </button>
            </div>
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            End
            <div className="mt-2 flex gap-1">
              <input
                type="text"
                inputMode="decimal"
                value={endText}
                onChange={(event) => {
                  const next = event.target.value;
                  setEndText(next);
                  if (next.trim() === "") {
                    setEndSec("");
                    return;
                  }
                  const parsed = parseSegmentTimeInput(next);
                  if (parsed != null) setEndSec(parsed);
                }}
                onBlur={() => setEndText(endSec === "" ? "" : formatSegmentTime(endSec))}
                placeholder="Optional"
                className="min-w-0 flex-1 rounded-lg border border-border bg-input px-3 py-2 font-mono text-sm font-normal text-foreground outline-none focus:border-accent"
              />
              <button
                type="button"
                onClick={() => setEndTimeFromSeconds(segmentVideoTime, setEndSec, setEndText)}
                className="inline-flex items-center justify-center rounded-lg border border-border px-3 text-secondary hover:text-foreground"
                title="Use current time"
                aria-label="Use current time for segment end"
              >
                <Clock className="h-4 w-4" />
              </button>
            </div>
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            {kind === "tag" ? "Tag" : kind === "performer" ? "Performer" : "Face"}
            <EntityReferenceSelector
              entityType={kind}
              value={
                kind === "tag"
                  ? (selectedTagId ?? undefined)
                  : kind === "performer"
                    ? (selectedPerformerId ?? undefined)
                    : (selectedFaceId ?? undefined)
              }
              selectedLabel={
                kind === "tag"
                  ? (segment.tagName ?? undefined)
                  : kind === "performer"
                    ? (segment.performerName ?? segment.refLabel ?? undefined)
                    : (segment.refLabel ?? undefined)
              }
              onChange={(value) => {
                if (kind === "tag") setSelectedTagId(value ?? null);
                if (kind === "performer") setSelectedPerformerId(value ?? null);
                if (kind === "face") setSelectedFaceId(value ?? null);
              }}
              placeholder={
                kind === "tag" ? "Search tags..." : kind === "performer" ? "Search performers..." : "Search faces..."
              }
              disabled={kind === "tag" ? !canReadTags : kind === "performer" ? !canReadPerformers : !canReadFaces}
              selectedDisplay="input"
              inputClassName="mt-2 w-full rounded-lg border border-border bg-input px-3 py-2 pr-8 text-sm font-normal text-foreground outline-none focus:border-accent disabled:cursor-not-allowed disabled:text-muted"
            />
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Color hint
            <div className="mt-2 flex items-center gap-2">
              <input
                type="color"
                value={isHexColorValue(colorHint) ? colorHint : "#6ee7b7"}
                onChange={(event) => setColorHint(event.target.value)}
                className="h-10 w-12 rounded border border-border bg-card p-1"
                aria-label="Choose segment color hint"
              />
              <input
                type="text"
                value={colorHint}
                onChange={(event) => setColorHint(event.target.value)}
                placeholder="#6ee7b7"
                className="min-w-0 flex-1 rounded-lg border border-border bg-input px-3 py-2 text-sm font-normal text-foreground outline-none focus:border-accent"
              />
            </div>
          </label>
        </div>
      ) : (
        <div className="mt-4 grid gap-3 md:grid-cols-2">
          <ReadOnlyField label="Title" value={segment.title} />
          <ReadOnlyField label="Kind" value={segment.kind} />
          <ReadOnlyField label="Start" value={formatSegmentTime(segment.startSec)} />
          <ReadOnlyField label="End" value={segment.endSec == null ? undefined : formatSegmentTime(segment.endSec)} />
          <ReadOnlyField label="Reference" value={segment.performerName || segment.refLabel} />
          <ReadOnlyField label="Color hint" value={segment.colorHint} />
        </div>
      )}

      {canWriteSegments ? (
        <div className="mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
          <div className="text-xs text-secondary">
            {parsedStart == null || parsedStart < 0
              ? "Start must be a valid time."
              : !endTimeIsValid
                ? "End must be a valid time."
                : normalizedEndSec != null && normalizedEndSec < startSec
                  ? "End time must be after the start time."
                  : !hasSelectedReference
                    ? `Choose a ${kind}.`
                    : "Changes are written back through the owning video segment API."}
          </div>
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => {
                resetEditState();
                setActiveTab("overview");
              }}
              className="px-4 py-2 text-sm text-secondary transition hover:text-foreground"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => updateMutation.mutate({ segment, nextTagId: selectedTagId })}
              disabled={!canSave || updateMutation.isPending}
              className={`rounded-lg ${saveButtonTone(hasChanges)} px-4 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-50`}
            >
              {updateMutation.isPending ? "Saving..." : "Save"}
            </button>
          </div>
        </div>
      ) : null}
    </section>
  );

  const nearbyItem = (item: SegmentRecord): NearbyItem => {
    const faceName = rawSegmentFaceName(item, siblingFaceNames);
    const itemTitle = faceName?.name ?? buildRawSegmentTitle(item);
    const kindIcon = segmentKindIconKind(item.kind, !item.tagName || item.tagName === itemTitle);
    return {
      key: item.id,
      title: itemTitle,
      suggested: faceName?.suggested,
      range: formatSegmentRange(item.startSec, item.endSec),
      meta: formatConfidencePercent(item.confidence),
      icon: kindIcon ? (
        <span aria-hidden className="inline-flex">
          <SegmentKindIcon kind={kindIcon} className="h-3.5 w-3.5 shrink-0 text-secondary" />
        </span>
      ) : undefined,
      onClick: () => onNavigate({ page: "segment", id: item.id }),
    };
  };
  const nearbyGroups: NearbyGroup[] = [
    { title: "Before", items: videoContext.previous.map(nearbyItem) },
    { title: "After", items: videoContext.next.map(nearbyItem) },
    { title: "Overlapping", items: videoContext.intersecting.map(nearbyItem) },
    {
      title: `Next with the same ${contextDescriptor?.label.toLowerCase() ?? "reference"}`,
      items: videoContext.nextSameReference ? [nearbyItem(videoContext.nextSameReference)] : [],
    },
  ];

  const overviewContent = (
    <div className="space-y-6">
      <SegmentOverview
        segment={segment}
        heading={displayTitle}
        face={segmentFace.face}
        faceLoading={segmentFace.faceLoading}
        suggestions={segmentFace.suggestions}
        decisionPending={segmentFace.decisionPending}
        decisionError={segmentFace.decisionError}
        canReadFaces={canReadFaces}
        canWriteFaces={canWriteFaces}
        canReadPerformers={canReadPerformers}
        canReadTags={canReadTags}
        canReadVideos={canReadVideos}
        position={
          videoContext.currentIndex >= 0
            ? { index: videoContext.currentIndex, total: orderedSiblingSegments.length }
            : undefined
        }
        video={playbackVideo}
        videoEngagement={segment.hostType === "video" ? videoEngagement.get(segment.hostId) : undefined}
        membership={membership}
        nearby={nearbyGroups}
        nearbyError={
          siblingSegmentsLoadError ? (
            <ListLoadError
              error={siblingSegmentsLoadError}
              onRetry={() => {
                void retrySiblingSegments();
              }}
              title="Could not load nearby raw segments"
            />
          ) : null
        }
        onAcceptSuggestion={(suggestion) => segmentFace.decide(suggestion, "accept")}
        onRejectSuggestion={(suggestion) => segmentFace.decide(suggestion, "reject")}
        // Only a mounted player can show the frame.
        onSeek={
          playbackVideo?.files[0]
            ? (timeSec) => {
                // The best frame is for looking at where the face is, so playback stops on it.
                playerControlsRef.current?.pause();
                playerSeekRef.current?.(timeSec, false);
              }
            : undefined
        }
        onNavigate={onNavigate}
      />
    </div>
  );

  const activeContent =
    activeTab === "metadata" ? (
      editContent
    ) : activeTab === "similar" ? (
      segment.hostType === "video" ? (
        <SegmentVisualSimilarityPanel
          videoId={segment.hostId}
          startSec={segment.startSec}
          endSec={segment.endSec}
          onNavigate={onNavigate}
        />
      ) : (
        <EmptyPanel
          icon={<Film className="h-10 w-10" />}
          message="Visual similarity is only available for video-backed segments."
        />
      )
    ) : (
      overviewContent
    );

  return (
    <>
      <CoverImageDialog
        open={coverOpen}
        title="Set Raw Segment Cover"
        entityType="segment"
        entityId={segment.id}
        currentImageUrl={entityImages.segmentCoverUrl(segment.id, segment.updatedAt)}
        onUpload={(file) => entityImages.uploadSegmentCoverImage(segment.id, file)}
        onDelete={() => entityImages.deleteSegmentCoverImage(segment.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => invalidateSegmentQueries(segment)}
        aspectRatio="16/9"
        externalPending={coverActionPending}
        extraActions={
          canSetSegmentCover
            ? (imageOperationPending) => (
                <>
                  <button
                    type="button"
                    onClick={() => setSegmentCoverMutation.mutate({ target: segment, atSeconds: segmentVideoTime })}
                    disabled={coverActionPending || imageOperationPending}
                    className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
                  >
                    {coverActionPending ? (
                      <span className="h-3.5 w-3.5 animate-spin rounded-full border-b-2 border-accent" />
                    ) : (
                      <Camera className="h-3.5 w-3.5" />
                    )}
                    From Current Frame
                  </button>
                  {setSegmentCoverMutation.error ? (
                    <p role="alert" className="mt-2 text-xs text-red-400">
                      {(setSegmentCoverMutation.error as Error).message}
                    </p>
                  ) : null}
                </>
              )
            : null
        }
      />
      <MediaDetailLayout
        title={displayTitleWithProvenance}
        eyebrow={
          <span
            className="inline-flex items-center gap-1.5"
            title="One detection, exactly as its source recorded it. The Segments list can join it with nearby raw segments into one segment."
          >
            <RawSegmentIcon className="h-3.5 w-3.5" />
            Raw segment
            <LoadingNextMarker loading={loadingNextSegment} />
          </span>
        }
        subtitle={subtitleWithProvenance}
        headerDetails={
          <SegmentSummary
            segment={segment}
            heading={displayTitle}
            canReadTags={canReadTags}
            canReadPerformers={canReadPerformers}
            onNavigate={onNavigate}
          />
        }
        backLabel={backLabel}
        onGoBack={goBack}
        backAlternative={backAlternative}
        media={
          <SegmentPlaybackPanel
            segment={segment}
            video={playbackVideo}
            videoLoading={playbackVideoLoading}
            canReadVideos={canReadVideos}
            onNavigate={onNavigate}
            onTimeUpdate={setSegmentVideoTime}
            faces={segmentFace.playerFaces}
            onSeekRegister={registerPlayerSeek}
            onPlaybackControlRegister={registerPlayerControls}
            embedded
          />
        }
        mediaAspectRatio="auto"
        mediaFullBleed
        tabs={tabs}
        activeTab={activeTab}
        onTabChange={(key) => setActiveTab(key as SegmentTab)}
        keyboardShortcuts={segmentKeyboardShortcuts}
        actions={
          <>
            {previousSegment ? (
              <button
                type="button"
                aria-label="Open previous raw segment"
                title="Open previous raw segment"
                onClick={() => onNavigate({ page: "segment", id: previousSegment.id })}
                className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
              >
                <ChevronLeft className="h-4 w-4" />
              </button>
            ) : null}
            {nextSegment ? (
              <button
                type="button"
                aria-label="Open next raw segment"
                title="Open next raw segment"
                onClick={() => onNavigate({ page: "segment", id: nextSegment.id })}
                className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
              >
                <ChevronRight className="h-4 w-4" />
              </button>
            ) : null}
            <SegmentHeaderLinks
              videoTitle={segment.hostTitle}
              onOpenVideo={
                segment.hostType === "video" && canReadVideos
                  ? () => onNavigate(buildVideoRouteForSegment(segment))
                  : undefined
              }
              tagName={segment.tagName}
              onOpenTag={
                segment.tagId != null && canReadTags ? () => onNavigate({ page: "tag", id: segment.tagId }) : undefined
              }
            />
            {canSetSegmentCover || canCreateSubVideo || canDeleteSegments ? (
              <div className="relative" ref={opsMenuRef}>
                <button
                  type="button"
                  onClick={() => setShowOpsMenu((current) => !current)}
                  className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
                  title="Operations"
                >
                  <MoreVertical className="h-4 w-4" />
                </button>
                <FloatingActionMenu
                  open={showOpsMenu}
                  anchorRef={opsMenuRef}
                  onClose={() => setShowOpsMenu(false)}
                  className="min-w-[190px] py-1"
                >
                  {canSetSegmentCover ? (
                    <button
                      type="button"
                      onClick={() => {
                        setCoverOpen(true);
                        setShowOpsMenu(false);
                      }}
                      disabled={coverActionPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <Image className="h-3.5 w-3.5" /> Set Cover...
                    </button>
                  ) : null}
                  {canSetSegmentCover && (canCreateSubVideo || canDeleteSegments) ? (
                    <div className="my-1 border-t border-border" />
                  ) : null}
                  {canCreateSubVideo ? (
                    <button
                      type="button"
                      onClick={() => {
                        createSubVideoMutation.mutate();
                        setShowOpsMenu(false);
                      }}
                      disabled={createSubVideoMutation.isPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <Clapperboard className="h-3.5 w-3.5" />{" "}
                      {createSubVideoMutation.isPending ? "Creating video" : "Make video"}
                    </button>
                  ) : null}
                  {canCreateSubVideo && canDeleteSegments ? <div className="my-1 border-t border-border" /> : null}
                  {canDeleteSegments ? (
                    <button
                      type="button"
                      onClick={() => {
                        setConfirmDelete(true);
                        setShowOpsMenu(false);
                      }}
                      disabled={deleteMutation.isPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-400 transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </button>
                  ) : null}
                </FloatingActionMenu>
              </div>
            ) : null}
          </>
        }
      >
        <ConfirmDialog
          open={confirmDelete}
          title="Delete Raw Segment"
          message={`Delete raw segment #${segment.id}? This cannot be undone.`}
          confirmLabel={deleteMutation.isPending ? "Deleting..." : "Delete"}
          onConfirm={() => deleteMutation.mutate(segment)}
          onCancel={() => setConfirmDelete(false)}
          isPending={deleteMutation.isPending}
        />
        {/* segments do not support engagement (see ui/src/api/types.ts AffinityHostType) */}
        <MediaDetailLayout.Content>{activeContent}</MediaDetailLayout.Content>
      </MediaDetailLayout>
    </>
  );
}

function ReadOnlyField({ label, value }: { label: string; value?: string }) {
  return (
    <div className="border-t border-border/70 py-3 first:border-t-0">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-2 text-sm text-foreground">{value || "Not set"}</div>
    </div>
  );
}

function SegmentPlaybackPanel({
  segment,
  video,
  videoLoading,
  canReadVideos,
  onNavigate,
  onTimeUpdate,
  faces = [],
  onSeekRegister,
  onPlaybackControlRegister,
  embedded = false,
}: {
  segment: SegmentRecord;
  video?: Video;
  videoLoading: boolean;
  canReadVideos: boolean;
  onNavigate: (r: any) => void;
  onTimeUpdate?: (time: number) => void;
  faces?: FaceOverlayInfo[];
  onSeekRegister?: (seek: VideoPlayerSeek) => void;
  onPlaybackControlRegister?: (controls: VideoPlayerPlaybackControls) => () => void;
  embedded?: boolean;
}) {
  const { config } = useAppConfig();
  const file = video?.files[0];
  const clipDuration = getSegmentDuration(segment.startSec, segment.endSec);
  const containerClassName = embedded
    ? "flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black"
    : "self-start overflow-hidden rounded-3xl border border-border bg-card/80 shadow-sm xl:sticky xl:top-4";

  if (segment.hostType !== "video") {
    return (
      <article className={containerClassName}>
        <div className="flex aspect-video items-center justify-center bg-surface/70 text-muted">
          <Film className="h-16 w-16" />
        </div>
        <div className="space-y-2 p-5">
          <h2 className="text-lg font-semibold text-foreground">Segment Playback</h2>
          <p className="text-sm text-secondary">
            Inline playback is only available for video-backed segments right now.
          </p>
        </div>
      </article>
    );
  }

  if (!canReadVideos) {
    return (
      <article className={containerClassName}>
        <div className="flex aspect-video items-center justify-center bg-surface/70 text-muted">
          <Film className="h-16 w-16" />
        </div>
        <div className="space-y-2 p-5">
          <h2 className="text-lg font-semibold text-foreground">Segment Playback</h2>
          <p className="text-sm text-secondary">
            The shared video player is unavailable because your current permissions do not allow video playback.
          </p>
        </div>
      </article>
    );
  }

  return (
    <article className={containerClassName}>
      {!embedded ? (
        <div className="flex flex-wrap items-start justify-between gap-3 border-b border-border px-5 py-4">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-muted">
              <Clapperboard className="h-3.5 w-3.5" />
              Segment Playback
            </div>
            <p className="mt-2 text-sm text-secondary">
              This now uses the same video player surface as the main video page, starting at the clip's time range.
            </p>
          </div>
          <div className="flex flex-wrap gap-2 text-xs">
            <StatusPill label={formatSegmentRange(segment.startSec, segment.endSec)} tone="accent" />
            {clipDuration > 0 ? (
              <StatusPill label={formatSegmentDuration(segment.startSec, segment.endSec)} tone="muted" />
            ) : null}
            <StatusPill label={segment.hostTitle || `Video #${segment.hostId}`} tone="muted" />
          </div>
        </div>
      ) : null}

      <div
        className={
          embedded
            ? "flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black"
            : "bg-black px-3 py-3 sm:px-4"
        }
      >
        {videoLoading ? (
          <div
            className={
              embedded
                ? "flex flex-1 items-center justify-center bg-black text-sm text-secondary"
                : "mx-auto flex aspect-video max-w-5xl items-center justify-center rounded-2xl bg-black text-sm text-secondary"
            }
          >
            Loading video player...
          </div>
        ) : file ? (
          <div
            className={
              embedded
                ? "flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black"
                : "mx-auto aspect-video max-w-5xl overflow-hidden rounded-2xl bg-black"
            }
          >
            <VideoPlayer
              streamUrl={videos.streamUrl(segment.hostId)}
              posterUrl={videos.screenshotUrl(segment.hostId, segment.updatedAt)}
              format={file.format}
              duration={file.duration}
              resumeTime={segment.startSec}
              videoId={segment.hostId}
              detections={[]}
              segments={[segment]}
              faces={faces}
              // A face segment is about where its face is, so its box shows unless the viewer turned X-ray off.
              faceOverlayDefault={rawSegmentFaceId(segment) != null}
              onSeekRegister={onSeekRegister}
              onPlaybackControlRegister={onPlaybackControlRegister}
              captions={file.captions}
              onPlay={() => {}}
              onTimeUpdate={onTimeUpdate}
              autostart={config?.ui.autostartVideo}
              trackingEnabled
              playbackTracking={{
                hostType: "segment",
                hostId: segment.id,
                surface: "segmentDetail",
                scopeKey: `segment:${segment.id}`,
                parentHostType: "video",
                parentHostId: segment.hostId,
                itemHostType: "video",
                itemHostId: segment.hostId,
                segmentId: segment.id,
                clipStartSec: segment.startSec,
                clipEndSec: segment.endSec ?? file.duration,
              }}
              clip={{ start: segment.startSec, end: segment.endSec ?? file.duration, loop: false }}
            />
          </div>
        ) : (
          <div
            className={
              embedded
                ? "flex flex-1 items-center justify-center bg-black text-sm text-secondary"
                : "mx-auto flex aspect-video max-w-5xl items-center justify-center rounded-2xl bg-black text-sm text-secondary"
            }
          >
            No playable video file is available for this segment.
          </div>
        )}
      </div>

      {!embedded ? (
        <div className="space-y-4 p-5">
          <div className="grid gap-3 sm:grid-cols-3">
            <InfoMetric label="Clip Start" value={formatSegmentTime(segment.startSec)} />
            <InfoMetric
              label="Clip End"
              value={segment.endSec != null ? formatSegmentTime(segment.endSec) : "Video end"}
            />
            <InfoMetric label="Duration" value={clipDuration > 0 ? formatSegmentTime(clipDuration) : "Instant"} />
          </div>

          <div className="rounded-2xl border border-border bg-surface/50 p-4">
            <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">
              Clip-focused playback
            </div>
            <p className="text-sm text-secondary">
              The shared video player opens at this segment's start time with the normal video controls, captions,
              quality selection, and X-ray overlays.
            </p>
            <div className="mt-3 flex flex-wrap gap-2 text-xs">
              <StatusPill label={segment.sourceKey} tone="muted" />
              {segment.kind ? <StatusPill label={segment.kind} tone="muted" /> : null}
              {segment.tagName ? <StatusPill label={segment.tagName} tone="muted" /> : null}
              <StatusPill label={formatConfidence(segment.confidence)} tone="muted" />
            </div>
          </div>

          <div className="rounded-2xl border border-border bg-surface/50 p-4">
            <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Video handoff</div>
            <p className="text-sm text-secondary">Open the parent video exactly where this segment begins.</p>
            <div className="mt-4 flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => onNavigate(buildVideoRouteForSegment(segment))}
                className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
              >
                <ExternalLink className="h-4 w-4" />
                Open at clip start
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </article>
  );
}

function InfoMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="border-t border-border/70 py-3 first:border-t-0 sm:border-l sm:border-t-0 sm:px-3 sm:first:border-l-0">
      <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-1 text-sm font-medium text-foreground">{value}</div>
    </div>
  );
}

function StatusPill({ label, tone }: { label: string; tone: "accent" | "muted" }) {
  const toneClass =
    tone === "accent" ? "border-accent/30 bg-accent/10 text-accent" : "border-border bg-surface text-secondary";

  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-1 ${toneClass}`}>
      <SegmentIcon className="mr-1 h-3 w-3" />
      {label}
    </span>
  );
}

function EmptyPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="mt-4 flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      <div className="mb-3 opacity-60 text-muted">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function buildVideoRouteForSegment(segment: Pick<SegmentRecord, "hostId" | "startSec">, seekTo = segment.startSec) {
  return {
    page: "video",
    id: segment.hostId,
    seekTo,
  };
}

function formatSegmentRange(startSec: number, endSec?: number) {
  const start = formatSegmentTime(startSec);
  return endSec == null ? start : `${start} - ${formatSegmentTime(endSec)}`;
}

function formatSegmentDuration(startSec: number, endSec?: number) {
  const duration = getSegmentDuration(startSec, endSec);
  return duration > 0 ? `${formatSegmentTime(duration)} long` : "Instant";
}

function getSegmentDuration(startSec?: number, endSec?: number) {
  if (startSec == null || endSec == null) {
    return 0;
  }

  return Math.max(0, endSec - startSec);
}

function formatConfidence(confidence?: number) {
  return confidence == null ? "Not set" : `${(confidence * 100).toFixed(0)}%`;
}
