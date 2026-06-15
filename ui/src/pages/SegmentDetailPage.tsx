import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Bookmark, Camera, ChevronLeft, ChevronRight, Clapperboard, Clock, ExternalLink, Film, Image, MoreVertical, Network, Sparkles, Trash2 } from "lucide-react";
import { entityImages, videos, segmentLibrary } from "../api/client";
import type { Video, SegmentRecord, TagProvenance } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { VideoPlayer } from "../components/VideoPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { VideoCard } from "../components/EntityCards";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { FieldProvenanceHover, formatDate, ProvenanceBadge, TagBadge } from "../components/shared";
import { EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { SegmentVisualSimilarityPanel, useSegmentVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { buildSubVideoCreate } from "../utils/subVideoCreation";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type SegmentTab = "overview" | "context" | "similar" | "metadata";
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

function parseSegmentTimeInput(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return null;

  const parts = trimmed.split(":").map((part) => part.trim());
  if (parts.length > 3 || parts.some((part) => part === "" || Number.isNaN(Number(part)))) return null;

  const numbers = parts.map(Number);
  if (numbers.some((part) => part < 0 || !Number.isFinite(part))) return null;

  if (numbers.length === 1) return numbers[0];
  if (numbers.length === 2) return numbers[0] * 60 + numbers[1];
  return numbers[0] * 3600 + numbers[1] * 60 + numbers[2];
}

function formatSegmentTimeInput(seconds: number) {
  const safeSeconds = Math.max(0, seconds || 0);
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  const wholeSeconds = Math.floor(safeSeconds % 60);
  const tenths = Math.round((safeSeconds - Math.floor(safeSeconds)) * 10);
  const normalizedWholeSeconds = tenths === 10 ? wholeSeconds + 1 : wholeSeconds;
  const normalizedTenths = tenths === 10 ? 0 : tenths;
  const secondText = normalizedTenths > 0
    ? `${normalizedWholeSeconds.toString().padStart(2, "0")}.${normalizedTenths}`
    : normalizedWholeSeconds.toString().padStart(2, "0");

  return hours > 0 ? `${hours}:${minutes.toString().padStart(2, "0")}:${secondText}` : `${minutes}:${secondText}`;
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
      matchKey: segment.refId != null
        ? `${normalizedKind || "reference"}:${segment.refId}`
        : `${normalizedKind || "reference"}-name:${referenceLabel?.toLowerCase()}`,
    };
  }

  return null;
}

function getSegmentContextMatchKey(segment: SegmentRecord) {
  return getSegmentContextDescriptor(segment)?.matchKey;
}

function setStartTimeFromSeconds(seconds: number, setStartSec: (value: number) => void, setStartText: (value: string) => void) {
  const normalized = Math.max(0, seconds);
  setStartSec(normalized);
  setStartText(formatSegmentTimeInput(normalized));
}

function setEndTimeFromSeconds(seconds: number | "", setEndSec: (value: number | "") => void, setEndText: (value: string) => void) {
  if (seconds === "") {
    setEndSec("");
    setEndText("");
    return;
  }

  const normalized = Math.max(0, seconds);
  setEndSec(normalized);
  setEndText(formatSegmentTimeInput(normalized));
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
  const { backLabel, goBack } = useBackNavigation({ page: "segments" }, onNavigate);

  const { data: segment, isLoading } = useQuery({
    queryKey: ["segment", id],
    queryFn: () => segmentLibrary.get(id),
  });

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

  useEffect(() => {
    if (!segment) {
      return;
    }

    resetEditState();
  }, [segment]);

  const { data: siblingSegments = [], isLoading: siblingSegmentsLoading } = useQuery({
    queryKey: ["segment", id, "video-context", segment?.hostId],
    queryFn: () => videos.segments.list(segment!.hostId),
    enabled: !!segment,
  });
  const { data: playbackVideo, isLoading: playbackVideoLoading } = useQuery({
    queryKey: ["video", segment?.hostId],
    queryFn: () => videos.get(segment!.hostId),
    enabled: !!segment && segment.hostType === "video" && canReadVideos,
  });
  const normalizedTitle = title.trim() || undefined;
  const normalizedKind = kind;
  const normalizedColorHint = colorHint.trim() || undefined;
  const parsedStart = parseSegmentTimeInput(startText);
  const endTextIsBlank = endText.trim() === "";
  const parsedEnd = endTextIsBlank ? null : parseSegmentTimeInput(endText);
  const endTimeIsValid = endTextIsBlank || parsedEnd != null;
  const normalizedEndSec = parsedEnd == null ? undefined : parsedEnd;
  const hasSelectedReference = kind === "tag"
    ? selectedTagId != null
    : kind === "performer"
      ? selectedPerformerId != null
      : selectedFaceId != null;
  const canSave =
    canWriteSegments &&
    parsedStart != null &&
    parsedStart >= 0 &&
    endTimeIsValid &&
    (parsedEnd == null || parsedEnd >= parsedStart) &&
    hasSelectedReference;

  const invalidateSegmentQueries = (current?: SegmentRecord | null, nextTagId?: number | null) => {
    queryClient.invalidateQueries({ queryKey: ["segments"] });
    queryClient.invalidateQueries({ queryKey: ["segment", id] });

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

  const updateMutation = useMutation({
    mutationFn: async () => {
      if (!segment) {
        throw new Error("Segment not loaded");
      }

      if (parsedStart == null) {
        throw new Error("Segment start is invalid");
      }

      const nextTagId = kind === "tag" ? selectedTagId ?? undefined : undefined;
      const nextRefId = kind === "performer"
        ? selectedPerformerId ?? undefined
        : kind === "face"
          ? selectedFaceId ?? undefined
          : undefined;

      return videos.segments.update(segment.hostId, segment.id, {
        startSec: parsedStart,
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
    onSuccess: () => {
      invalidateSegmentQueries(segment, selectedTagId);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async () => {
      if (!segment) {
        throw new Error("Segment not loaded");
      }

      return videos.segments.delete(segment.hostId, segment.id);
    },
    onSuccess: () => {
      invalidateSegmentQueries(segment);
      setConfirmDelete(false);
      goBack();
    },
  });

  const setSegmentCoverMutation = useMutation({
    mutationFn: async (atSeconds?: number) => {
      if (!segment) {
        throw new Error("Segment not loaded");
      }

      return entityImages.setSegmentCoverFromFrame(segment.id, atSeconds);
    },
    onSuccess: () => {
      invalidateSegmentQueries(segment);
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
        buildSubVideoCreate(playbackVideo, {
          startSec: segment.startSec,
          endSec: clipEndSec,
        }, {
          title: displayTitle,
          tagIds: segment.tagId ? [segment.tagId] : undefined,
        }),
      );
    },
    onSuccess: (newVideo) => {
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      queryClient.invalidateQueries({ queryKey: ["video", segment?.hostId] });
      onNavigate({ page: "video", id: newVideo.id });
    },
  });

  const displayTitle = segment?.title?.trim() || segment?.tagName || segment?.performerName || segment?.refLabel || segment?.kind || "Segment";
  const orderedSiblingSegments = useMemo(
    () => [...siblingSegments].sort((left, right) => left.startSec - right.startSec || (left.endSec ?? left.startSec) - (right.endSec ?? right.startSec) || left.id - right.id),
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
    const previous = currentIndex > 0 ? orderedSiblingSegments.slice(Math.max(0, currentIndex - 2), currentIndex) : [];
    const next = currentIndex >= 0 ? orderedSiblingSegments.slice(currentIndex + 1, currentIndex + 3) : [];
    const currentEnd = segment.endSec ?? segment.startSec;
    const intersectsCurrent = (item: SegmentRecord) => {
      const itemEnd = item.endSec ?? item.startSec;
      return item.id !== segment.id && item.startSec < currentEnd && itemEnd > segment.startSec;
    };
    const currentContextMatchKey = getSegmentContextMatchKey(segment);
    const nextSameReference = currentContextMatchKey == null
      ? undefined
      : orderedSiblingSegments.find((item) => item.id !== segment.id && getSegmentContextMatchKey(item) === currentContextMatchKey && item.startSec >= segment.startSec);
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
  const canCreateSubVideo = !!segment
    && segment.hostType === "video"
    && !!playbackVideo
    && canReadVideos
    && canWriteVideos
    && (segment.endSec != null || (playbackVideo?.files[0]?.duration ?? 0) > segment.startSec);
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
      { key: "context", label: "Context", icon: <Network className="h-4 w-4" /> },
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
        key: "e",
        description: canWriteSegments ? "Edit segment" : "Open segment details",
        handler: () => {
          setActiveTab("metadata");
          if (canWriteSegments) {
            window.setTimeout(() => titleInputRef.current?.focus(), 0);
          }
        },
      },
      {
        key: "s",
        description: "Open parent video",
        handler: () => {
          if (segment.hostType === "video" && canReadVideos) {
            onNavigate(buildVideoRouteForSegment(segment));
          }
        },
      },
      {
        key: "[",
        description: "Open previous segment",
        handler: () => {
          if (previousSegment) {
            onNavigate({ page: "segment", id: previousSegment.id });
          }
        },
      },
      {
        key: "]",
        description: "Open next segment",
        handler: () => {
          if (nextSegment) {
            onNavigate({ page: "segment", id: nextSegment.id });
          }
        },
      },
    ];
  }, [canReadVideos, canWriteSegments, nextSegment, onNavigate, previousSegment, segment]);
  useDocumentTitle(segment ? displayTitle : null);

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

  if (!segment) {
    return <div className="py-16 text-center text-secondary">Segment not found</div>;
  }

  const displayTitleWithProvenance = (
    <FieldProvenanceHover fieldProvenance={segment.fieldProvenance} fieldKey={["title", "tag_id", "performer_id", "ref_id", "kind"]}>
      {displayTitle}
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
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">{canWriteSegments ? "Edit Segment" : "Segment Details"}</h2>
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
                <option key={option.value} value={option.value}>{option.label}</option>
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
                onBlur={() => setStartText(formatSegmentTimeInput(startSec))}
                className="min-w-0 flex-1 rounded-lg border border-border bg-input px-3 py-2 font-mono text-sm font-normal text-foreground outline-none focus:border-accent"
              />
              <button type="button" onClick={() => setStartTimeFromSeconds(segmentVideoTime, setStartSec, setStartText)} className="inline-flex items-center justify-center rounded-lg border border-border px-3 text-secondary hover:text-foreground" title="Use current time" aria-label="Use current time for segment start"><Clock className="h-4 w-4" /></button>
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
                onBlur={() => setEndText(endSec === "" ? "" : formatSegmentTimeInput(endSec))}
                placeholder="Optional"
                className="min-w-0 flex-1 rounded-lg border border-border bg-input px-3 py-2 font-mono text-sm font-normal text-foreground outline-none focus:border-accent"
              />
              <button type="button" onClick={() => setEndTimeFromSeconds(segmentVideoTime, setEndSec, setEndText)} className="inline-flex items-center justify-center rounded-lg border border-border px-3 text-secondary hover:text-foreground" title="Use current time" aria-label="Use current time for segment end"><Clock className="h-4 w-4" /></button>
            </div>
          </label>

          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            {kind === "tag" ? "Tag" : kind === "performer" ? "Performer" : "Face"}
            <EntityReferenceSelector
              entityType={kind}
              value={kind === "tag" ? selectedTagId ?? undefined : kind === "performer" ? selectedPerformerId ?? undefined : selectedFaceId ?? undefined}
              selectedLabel={kind === "tag" ? segment.tagName ?? undefined : kind === "performer" ? segment.performerName ?? segment.refLabel ?? undefined : segment.refLabel ?? undefined}
              onChange={(value) => {
                if (kind === "tag") setSelectedTagId(value ?? null);
                if (kind === "performer") setSelectedPerformerId(value ?? null);
                if (kind === "face") setSelectedFaceId(value ?? null);
              }}
              placeholder={kind === "tag" ? "Search tags..." : kind === "performer" ? "Search performers..." : "Search faces..."}
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
              : parsedEnd != null && parsedEnd < parsedStart
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
              onClick={() => updateMutation.mutate()}
              disabled={!canSave || updateMutation.isPending}
              className="rounded-lg bg-accent px-4 py-2 text-sm text-white transition hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              {updateMutation.isPending ? "Saving..." : "Save"}
            </button>
          </div>
        </div>
      ) : null}
    </section>
  );

  const relatedContent = (
    <section className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Timeline Context</h2>
          <p className="mt-1 text-sm text-secondary">
            {videoContext.currentIndex >= 0
              ? `Segment ${videoContext.currentIndex + 1} of ${orderedSiblingSegments.length} by timeline order in this video.`
              : "See nearby segments from the same video to understand context."}
          </p>
        </div>
        <div className="text-xs text-muted">{orderedSiblingSegments.length} segment{orderedSiblingSegments.length === 1 ? "" : "s"} in video</div>
      </div>

      {siblingSegmentsLoading ? (
        <div className="mt-4 text-sm text-secondary">Loading video context...</div>
      ) : (
        <div className="mt-4 space-y-4">
          {segment.hostType === "video" && playbackVideo ? (
            <div className="max-w-sm">
              <VideoCard video={playbackVideo} onClick={() => onNavigate(buildVideoRouteForSegment(segment))} onNavigate={onNavigate} />
            </div>
          ) : null}
          {orderedSiblingSegments.length <= 1 ? (
            <EmptyPanel icon={<Clapperboard className="h-10 w-10" />} message="No additional segments exist in this video yet." />
          ) : (
            <>
              <SegmentContextSection title="Previous Segments" items={videoContext.previous} onNavigate={onNavigate} emptyMessage="This is the first segment in the video." />
              <SegmentContextSection title="Next Segments" items={videoContext.next} onNavigate={onNavigate} emptyMessage="This is the last segment in the video." />
              <SegmentContextSection title="Intersecting Segments" items={videoContext.intersecting} onNavigate={onNavigate} emptyMessage="No other segments overlap this time range." />
              <SegmentContextSection
                title={contextDescriptor?.title ?? "Next With Same Reference"}
                items={videoContext.nextSameReference ? [videoContext.nextSameReference] : []}
                onNavigate={onNavigate}
                emptyMessage={contextDescriptor?.emptyMessage ?? "This segment does not have a matching reference to follow."}
                compact
              />
            </>
          )}
        </div>
      )}
    </section>
  );

  const overviewContent = (
    <div className="space-y-6">
      <SegmentSummaryCard
        segment={segment}
        canReadVideos={canReadVideos}
        onNavigate={onNavigate}
        showHeading={false}
      />
    </div>
  );

  const contextContent = relatedContent;

  const activeContent =
    activeTab === "metadata"
      ? editContent
      : activeTab === "context"
          ? contextContent
          : activeTab === "similar"
              ? segment.hostType === "video"
                ? <SegmentVisualSimilarityPanel videoId={segment.hostId} startSec={segment.startSec} endSec={segment.endSec} onNavigate={onNavigate} />
                : <EmptyPanel icon={<Film className="h-10 w-10" />} message="Visual similarity is only available for video-backed segments." />
            : overviewContent;

  return (
    <>
      <CoverImageDialog
        open={coverOpen}
        title="Set Segment Cover"
        currentImageUrl={entityImages.segmentCoverUrl(segment.id, segment.updatedAt)}
        onUpload={(file) => entityImages.uploadSegmentCoverImage(segment.id, file)}
        onDelete={() => entityImages.deleteSegmentCoverImage(segment.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => invalidateSegmentQueries(segment)}
        aspectRatio="16/9"
        extraActions={canSetSegmentCover ? (
          <button
            type="button"
            onClick={() => setSegmentCoverMutation.mutate(segmentVideoTime)}
            disabled={coverActionPending}
            className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
          >
            {coverActionPending ? <span className="h-3.5 w-3.5 animate-spin rounded-full border-b-2 border-accent" /> : <Camera className="h-3.5 w-3.5" />}
            From Current Frame
          </button>
        ) : null}
      />
      <MediaDetailLayout
        title={displayTitleWithProvenance}
        subtitle={subtitleWithProvenance}
        backLabel={backLabel}
        onGoBack={goBack}
        media={
          <SegmentPlaybackPanel
            segment={segment}
            video={playbackVideo}
            videoLoading={playbackVideoLoading}
            canReadVideos={canReadVideos}
            onNavigate={onNavigate}
            onTimeUpdate={setSegmentVideoTime}
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
              aria-label="Open previous segment"
              title="Open previous segment"
              onClick={() => onNavigate({ page: "segment", id: previousSegment.id })}
              className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
            >
              <ChevronLeft className="h-4 w-4" />
            </button>
          ) : null}
          {nextSegment ? (
            <button
              type="button"
              aria-label="Open next segment"
              title="Open next segment"
              onClick={() => onNavigate({ page: "segment", id: nextSegment.id })}
              className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
            >
              <ChevronRight className="h-4 w-4" />
            </button>
          ) : null}
          {segment.hostType === "video" && canReadVideos ? (
            <button
              type="button"
              onClick={() => onNavigate(buildVideoRouteForSegment(segment))}
              className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
              title="Open parent video"
            >
              <ExternalLink className="h-4 w-4" />
            </button>
          ) : null}
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
              <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[190px] py-1">
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
                  {canSetSegmentCover && (canCreateSubVideo || canDeleteSegments) ? <div className="my-1 border-t border-border" /> : null}
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
                      <Clapperboard className="h-3.5 w-3.5" /> {createSubVideoMutation.isPending ? "Creating video" : "Make video"}
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
          title="Delete Segment"
          message={`Delete segment #${segment.id}? This cannot be undone.`}
          confirmLabel={deleteMutation.isPending ? "Deleting..." : "Delete"}
          onConfirm={() => deleteMutation.mutate()}
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

function VideoReferenceCard({
  videoId,
  title,
  updatedAt,
  startSec,
  disabled,
  onNavigate,
}: {
  videoId: number;
  title: string;
  updatedAt?: string;
  startSec: number;
  disabled: boolean;
  onNavigate: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onNavigate}
      disabled={disabled}
      className="group flex w-full overflow-hidden rounded-lg border border-border bg-card text-left transition-colors hover:border-accent disabled:cursor-default disabled:hover:border-border"
    >
      <div className="aspect-video w-36 shrink-0 bg-black sm:w-44">
        <img
          src={videos.screenshotUrl(videoId, updatedAt, startSec)}
          alt=""
          className="h-full w-full object-cover"
          loading="lazy"
        />
      </div>
      <div className="flex min-w-0 flex-1 items-center justify-between gap-3 px-4 py-3">
        <div className="min-w-0">
          <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">Video</div>
          <div className="mt-1 truncate text-sm font-medium text-foreground group-hover:text-accent">{title}</div>
          <div className="mt-1 text-xs text-secondary">Starts at {formatSegmentTime(startSec)}</div>
        </div>
        <ExternalLink className="h-4 w-4 shrink-0 text-muted" />
      </div>
    </button>
  );
}

function SegmentSummaryCard({
  segment,
  canReadVideos,
  onNavigate,
  showHeading = true,
}: {
  segment: SegmentRecord;
  canReadVideos: boolean;
  onNavigate: (r: any) => void;
  showHeading?: boolean;
}) {
  const displayTitle = segment.title?.trim() || segment.tagName || segment.performerName || segment.refLabel || segment.kind || "Segment";
  const summaryMetrics = buildSegmentSummaryMetrics(segment);

  return (
    <section className="space-y-4">
      {showHeading ? (
        <div>
          <h1 className="text-xl font-semibold text-foreground">
            <FieldProvenanceHover fieldProvenance={segment.fieldProvenance} fieldKey={["title", "tag_id", "performer_id", "ref_id", "kind"]}>
              {displayTitle}
            </FieldProvenanceHover>
          </h1>
          <p className="mt-1 text-sm text-secondary">
            <FieldProvenanceHover fieldProvenance={segment.fieldProvenance} fieldKey={["start_sec", "end_sec"]}>
              {formatSegmentRange(segment.startSec, segment.endSec)}
            </FieldProvenanceHover>
          </p>
        </div>
      ) : null}

      <SegmentSourceSummary segment={segment} onNavigate={onNavigate} className={showHeading ? "mt-4" : ""} />

      <div className="mt-4 grid grid-cols-2 gap-2">
        {summaryMetrics.map((metric) => <InfoMetric key={metric.label} label={metric.label} value={metric.value} />)}
      </div>

      <dl className="mt-4 space-y-2 text-sm text-secondary">
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Range</dt>
          <dd className="text-right text-foreground">
            <FieldProvenanceHover fieldProvenance={segment.fieldProvenance} fieldKey={["start_sec", "end_sec"]}>
              {formatSegmentRange(segment.startSec, segment.endSec)}
            </FieldProvenanceHover>
          </dd>
        </div>
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Created</dt>
          <dd className="text-right text-foreground">{formatDate(segment.createdAt)}</dd>
        </div>
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Updated</dt>
          <dd className="text-right text-foreground">{formatDate(segment.updatedAt)}</dd>
        </div>
      </dl>

      {segment.hostType === "video" && canReadVideos ? (
        <div className="mt-5 grid gap-2 sm:grid-cols-2">
          {segment.hostType === "video" && canReadVideos ? (
            <button
              type="button"
              onClick={() => onNavigate(buildVideoRouteForSegment(segment))}
              className="w-full rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
            >
              Open video at clip start
            </button>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}

function SegmentPlaybackPanel({
  segment,
  video,
  videoLoading,
  canReadVideos,
  onNavigate,
  onTimeUpdate,
  embedded = false,
}: {
  segment: SegmentRecord;
  video?: Video;
  videoLoading: boolean;
  canReadVideos: boolean;
  onNavigate: (r: any) => void;
  onTimeUpdate?: (time: number) => void;
  embedded?: boolean;
}) {
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
          <p className="text-sm text-secondary">Inline playback is only available for video-backed segments right now.</p>
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
          <p className="text-sm text-secondary">The shared video player is unavailable because your current permissions do not allow video playback.</p>
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
            {clipDuration > 0 ? <StatusPill label={formatSegmentDuration(segment.startSec, segment.endSec)} tone="muted" /> : null}
            <StatusPill label={segment.hostTitle || `Video #${segment.hostId}`} tone="muted" />
          </div>
        </div>
      ) : null}

      <div className={embedded ? "flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black" : "bg-black px-3 py-3 sm:px-4"}>
        {videoLoading ? (
          <div className={embedded ? "flex flex-1 items-center justify-center bg-black text-sm text-secondary" : "mx-auto flex aspect-video max-w-5xl items-center justify-center rounded-2xl bg-black text-sm text-secondary"}>
            Loading video player...
          </div>
        ) : file ? (
          <div className={embedded ? "flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black" : "mx-auto aspect-video max-w-5xl overflow-hidden rounded-2xl bg-black"}>
            <VideoPlayer
              streamUrl={videos.streamUrl(segment.hostId)}
              posterUrl={videos.screenshotUrl(segment.hostId, segment.updatedAt)}
              format={file.format}
              duration={file.duration}
              resumeTime={segment.startSec}
              videoId={segment.hostId}
              detections={[]}
              segments={[segment]}
              captions={file.captions}
              onPlay={() => {}}
              onTimeUpdate={onTimeUpdate}
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
          <div className={embedded ? "flex flex-1 items-center justify-center bg-black text-sm text-secondary" : "mx-auto flex aspect-video max-w-5xl items-center justify-center rounded-2xl bg-black text-sm text-secondary"}>
            No playable video file is available for this segment.
          </div>
        )}
      </div>

      {!embedded ? (
      <div className="space-y-4 p-5">
        <div className="grid gap-3 sm:grid-cols-3">
          <InfoMetric label="Clip Start" value={formatSegmentTime(segment.startSec)} />
          <InfoMetric label="Clip End" value={segment.endSec != null ? formatSegmentTime(segment.endSec) : "Video end"} />
          <InfoMetric label="Duration" value={clipDuration > 0 ? formatSegmentTime(clipDuration) : "Instant"} />
        </div>

        <div className="rounded-2xl border border-border bg-surface/50 p-4">
          <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Clip-focused playback</div>
          <p className="text-sm text-secondary">
            The shared video player opens at this segment's start time with the normal video controls, captions, quality selection, and X-ray overlays.
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

function SegmentSourceSummary({ segment, onNavigate, className = "" }: { segment: SegmentRecord; onNavigate: (r: any) => void; className?: string }) {
  const provenance = buildSegmentTagProvenance(segment);
  const referenceLabel = segment.performerName || segment.refLabel;
  const referenceKind = getEditableSegmentKind(segment);
  const performerId = getSegmentPerformerId(segment);
  const faceId = getSegmentFaceId(segment);

  return (
    <div className={`${className} flex flex-wrap gap-2 text-xs`.trim()}>
      {segment.tagName ? (
        <TagBadge
          name={segment.tagName}
          provenance={provenance}
          onClick={segment.tagId ? () => onNavigate({ page: "tag", id: segment.tagId }) : undefined}
        />
      ) : referenceLabel && referenceKind === "performer" ? (
        <ProvenanceBadge
          name={referenceLabel}
          sourceLabel="Performer"
          provenance={provenance}
          onClick={performerId != null ? () => onNavigate({ page: "performer", id: performerId }) : undefined}
        />
      ) : referenceLabel && referenceKind === "face" ? (
        <ProvenanceBadge
          name={referenceLabel}
          sourceLabel="Face"
          provenance={provenance}
          onClick={faceId != null ? () => onNavigate({ page: "face", id: faceId }) : undefined}
        />
      ) : referenceLabel ? (
        <ProvenanceBadge name={referenceLabel} sourceLabel="Reference" provenance={provenance} />
      ) : (
        <StatusPill label={segment.kind || formatSegmentSourceLabel(segment.sourceKey)} tone="muted" />
      )}
    </div>
  );
}

function buildSegmentSummaryMetrics(segment: SegmentRecord) {
  const metrics: Array<{ label: string; value: string }> = [
    { label: "Duration", value: formatSegmentDuration(segment.startSec, segment.endSec) },
  ];

  if (segment.confidence != null) {
    metrics.push({ label: "Confidence", value: formatConfidence(segment.confidence) });
  }

  if (segment.tagName) {
    metrics.push({ label: "Tag", value: segment.tagName });
  }

  if (segment.performerName) {
    metrics.push({ label: "Performer", value: segment.performerName });
  } else if (segment.refLabel) {
    metrics.push({ label: "Reference", value: segment.refLabel });
  }

  if (segment.kind && !metrics.some((metric) => metric.value === segment.kind)) {
    metrics.push({ label: "Type", value: segment.kind });
  }

  return metrics;
}

function SegmentContextSection({
  title,
  items,
  onNavigate,
  emptyMessage,
  compact = false,
}: {
  title: string;
  items: SegmentRecord[];
  onNavigate: (r: any) => void;
  emptyMessage: string;
  compact?: boolean;
}) {
  return (
    <div>
      <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">{title}</div>
      {items.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border bg-surface/30 px-3 py-3 text-sm text-secondary">
          {emptyMessage}
        </div>
      ) : (
        <div className="grid gap-2">
          {items.map((item) => {
            const titleText = item.title?.trim() || item.kind || item.tagName || `Segment #${item.id}`;
            return (
              <button
                key={item.id}
                type="button"
                onClick={() => onNavigate({ page: "segment", id: item.id })}
                className="flex items-center justify-between gap-3 rounded-xl border border-border bg-surface/50 px-3 py-3 text-left transition-colors hover:border-accent"
              >
                <div className="min-w-0">
                  <div className="truncate text-sm font-medium text-foreground">{titleText}</div>
                  <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-secondary">
                    <span>{formatSegmentRange(item.startSec, item.endSec)}</span>
                    {item.tagName ? <span>{item.tagName}</span> : null}
                    {!compact && item.kind ? <span>{item.kind}</span> : null}
                  </div>
                </div>
                <div className="shrink-0 text-xs text-muted">{compact ? item.sourceKey : formatSegmentDuration(item.startSec, item.endSec)}</div>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

function StatusPill({ label, tone }: { label: string; tone: "accent" | "muted" }) {
  const toneClass = tone === "accent"
    ? "border-accent/30 bg-accent/10 text-accent"
    : "border-border bg-surface text-secondary";

  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-1 ${toneClass}`}>
      <Bookmark className="mr-1 h-3 w-3" />
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

function formatSegmentTime(value: number) {
  const totalHundredths = Math.max(0, Math.round(value * 100));
  const hours = Math.floor(totalHundredths / 360000);
  const minutes = Math.floor((totalHundredths % 360000) / 6000);
  const seconds = Math.floor((totalHundredths % 6000) / 100);
  const hundredths = totalHundredths % 100;

  if (hundredths === 0) {
    if (hours > 0) {
      return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    }

    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  }

  const fractional = hundredths % 10 === 0
    ? String(Math.floor(hundredths / 10))
    : String(hundredths).padStart(2, "0");

  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${fractional}`;
  }

  return `${minutes}:${String(seconds).padStart(2, "0")}.${fractional}`;
}

function formatConfidence(confidence?: number) {
  return confidence == null ? "Not set" : `${(confidence * 100).toFixed(0)}%`;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function formatSegmentSourceLabel(sourceKey?: string) {
  if (!sourceKey) {
    return "Unknown source";
  }

  if (sourceKey === "user") {
    return "User";
  }

  return sourceKey.startsWith("ext:")
    ? sourceKey.slice(4).split(/[._-]+/).filter(Boolean).map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join(" ")
    : sourceKey;
}

function buildSegmentTagProvenance(segment: SegmentRecord): TagProvenance[] {
  return [{
    sourceKey: segment.sourceKey,
    sourceRunId: segment.sourceRunId,
    confidence: segment.confidence,
    appliedAt: segment.updatedAt || segment.createdAt,
    totalDurationSec: getSegmentDuration(segment.startSec, segment.endSec),
  }];
}

