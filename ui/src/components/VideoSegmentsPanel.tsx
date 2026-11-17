import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Layers,
  Plus,
  Pencil,
  Trash2,
  Clock,
  Filter,
  X,
  ChevronDown,
  ChevronRight,
  ChevronUp,
  ListTree,
  List as ListIcon,
} from "lucide-react";
import { videos, tagGroups as tagGroupsApi } from "../api/client";
import { SegmentIcon } from "./segmentViewIcons";
import { createNestedRouteLinkProps } from "./cardNavigation";
import type { Route } from "../router/location";
import type { Face, ResolvedSpan, Segment, SegmentDisplayProfile } from "../api/types";
import { resolveSpanName } from "../pages/segments/segmentDisplayUtils";
import { faceKeyLabel, spanPerson, type SegmentSection } from "../pages/videoDetail/segmentPeople";
import { useFaceSuggestionDecision } from "../pages/videoDetail/useFaceSuggestionDecision";
import { SuggestionDecision } from "../pages/videoDetail/SuggestionDecision";
import { FaceCrop, FaceStack } from "../pages/videoDetail/FaceCrops";
import { SuggestedNameMarker } from "./EntityCards";
import { AddToGroupDialog, type AddToGroupEntry } from "./AddToGroupDialog";
import { ConfirmDialog } from "./ConfirmDialog";
import { EntityReferenceSelector, EntityReferenceMultiSelector } from "./EntityReferenceSelector";
import { compareNatural } from "../utils/naturalCompare";
import { formatSegmentTime, parseSegmentTimeInput } from "../utils/segmentTime";
import {
  type SegmentFilterState,
  type SegmentFilterContext,
  EMPTY_SEGMENT_FILTER,
  matchesSegmentFilter,
  buildSegmentFacets,
  isSegmentFilterActive,
  countActiveFilters,
} from "./segmentFilter";

interface Props {
  videoId: number;
  spans: ResolvedSpan[];
  rawSegments: Segment[];
  /** The video's faces by id, which name face segments the way every other view does. */
  facesById?: ReadonlyMap<number, Face>;
  /** Each person's colour, shared with the timeline's face lanes. */
  personColors?: ReadonlyMap<string, string>;
  /** The viewer may accept or reject a face's suggested performer. */
  canDecideFaces?: boolean;
  loading: boolean;
  profiles: SegmentDisplayProfile[];
  currentProfileId?: number;
  onProfileChange: (profileId: number) => void;
  filter: SegmentFilterState;
  onFilterChange: (filter: SegmentFilterState) => void;
  tagIdToGroupId: Map<number, number>;
  canEdit: boolean;
  onSeek?: (time: number) => void;
  currentTime?: number;
  /** Set by the parent (e.g. a keyboard shortcut) to open the add form; cleared via onAddRequestHandled. */
  addRequested?: boolean;
  onAddRequestHandled?: () => void;
  onNavigate: (r: any) => void;
}

type ViewMode = "grouped" | "flat";

const VIEW_STORAGE_KEY = "cove.segments.viewMode";
const FILTER_INPUT_CLASS =
  "w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none";

export function VideoSegmentsPanel({
  videoId,
  spans,
  rawSegments,
  facesById,
  personColors,
  canDecideFaces = false,
  loading,
  profiles,
  currentProfileId,
  onProfileChange,
  filter,
  onFilterChange,
  tagIdToGroupId,
  canEdit,
  onSeek,
  currentTime = 0,
  addRequested = false,
  onAddRequestHandled,
  onNavigate,
}: Props) {
  const queryClient = useQueryClient();
  const faceDecision = useFaceSuggestionDecision(videoId, onNavigate);
  const [selectedSpanKeys, setSelectedSpanKeys] = useState<Set<string>>(new Set());
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [view, setView] = useState<ViewMode>(() => {
    if (typeof window === "undefined") return "grouped";
    return window.localStorage.getItem(VIEW_STORAGE_KEY) === "flat" ? "flat" : "grouped";
  });
  const setViewMode = (next: ViewMode) => {
    setView(next);
    try {
      window.localStorage.setItem(VIEW_STORAGE_KEY, next);
    } catch {
      /* ignore */
    }
  };

  // Editing state (raw segment add / edit form).
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [title, setTitle] = useState("");
  const [kind, setKind] = useState<"tag" | "performer">("tag");
  const [startSec, setStartSec] = useState(0);
  const [endSec, setEndSec] = useState<number | "">("");
  const [startText, setStartText] = useState("0:00");
  const [endText, setEndText] = useState("");
  const [selectedTagId, setSelectedTagId] = useState<number | null>(null);
  const [selectedPerformerId, setSelectedPerformerId] = useState<number | null>(null);
  // Drill-down state when a span maps to multiple raw segments.
  const [chooser, setChooser] = useState<{ span: ResolvedSpan; segments: Segment[] } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<{ span: ResolvedSpan; segments: Segment[] } | null>(null);

  const rawSegmentsById = useMemo(() => new Map(rawSegments.map((segment) => [segment.id, segment])), [rawSegments]);
  const filterContext = useMemo<SegmentFilterContext>(
    () => ({ rawSegmentsById, tagIdToGroupId }),
    [rawSegmentsById, tagIdToGroupId],
  );

  const orderedProfiles = useMemo(
    () =>
      [...profiles].sort(
        (left, right) => Number(right.isDefault) - Number(left.isDefault) || compareNatural(left.name, right.name),
      ),
    [profiles],
  );
  const activeProfileId =
    currentProfileId ?? orderedProfiles.find((profile) => profile.isDefault)?.id ?? orderedProfiles[0]?.id;
  const spanRoute = (span: ResolvedSpan): Route => ({
    page: "video-span",
    id: videoId,
    spanKey: span.spanKey,
    profileId: activeProfileId,
  });

  const facets = useMemo(() => buildSegmentFacets(spans, filterContext), [spans, filterContext]);
  const filteredSpans = useMemo(
    () => spans.filter((span) => matchesSegmentFilter(span, filter, filterContext)),
    [spans, filter, filterContext],
  );
  const sortedSpans = useMemo(
    () => [...filteredSpans].sort((left, right) => left.startSec - right.startSec || left.endSec - right.endSec),
    [filteredSpans],
  );

  // All tag groups are offered as filter chips; the parent expands a selected group to its
  // member tags so segments tagged within that group match.
  const { data: allTagGroups = [] } = useQuery({ queryKey: ["tag-groups"], queryFn: () => tagGroupsApi.list() });

  const [prevSelectionScope, setPrevSelectionScope] = useState({ currentProfileId, videoId });
  if (currentProfileId !== prevSelectionScope.currentProfileId || videoId !== prevSelectionScope.videoId) {
    setPrevSelectionScope({ currentProfileId, videoId });
    setSelectedSpanKeys(new Set());
  }

  const selectedEntries = useMemo<AddToGroupEntry[]>(() => {
    return filteredSpans
      .filter((span) => selectedSpanKeys.has(span.spanKey))
      .map((span) => ({
        key: span.spanKey,
        videoId,
        spanKey: span.spanKey,
        title: resolveSpanName(span, rawSegmentsById, facesById).name,
        profileId: activeProfileId,
      }));
  }, [filteredSpans, selectedSpanKeys, videoId, rawSegmentsById, facesById, activeProfileId]);

  const toggleSelection = (spanKey: string) => {
    setSelectedSpanKeys((current) => {
      const next = new Set(current);
      if (next.has(spanKey)) next.delete(spanKey);
      else next.add(spanKey);
      return next;
    });
  };

  // ----- Mutations -----
  // After a segment mutation, refresh both the raw segment list and the
  // server-resolved spans (sidebar + scrubber swimlanes render from spans).
  // The resolved-spans query key includes a profile id, so invalidate by
  // prefix to cover every profile variant.
  const invalidateSegments = () => {
    queryClient.invalidateQueries({ queryKey: ["video", videoId, "segments"] });
    queryClient.invalidateQueries({ queryKey: ["video", videoId, "resolved-spans"] });
  };
  const createMutation = useMutation({
    mutationFn: (data: {
      title?: string;
      kind?: string;
      startSec: number;
      endSec?: number;
      tagId?: number;
      refId?: number;
    }) =>
      videos.segments.create(videoId, {
        startSec: data.startSec,
        endSec: data.endSec,
        tagId: data.tagId,
        refId: data.refId,
        kind: data.kind,
        title: data.title,
      }),
    onSuccess: () => {
      invalidateSegments();
      resetForm();
    },
  });
  const updateMutation = useMutation({
    mutationFn: (data: {
      segment: Segment;
      startSec: number;
      endSec?: number;
      tagId?: number;
      refId?: number;
      kind?: string;
      title?: string;
    }) =>
      videos.segments.update(videoId, data.segment.id, {
        startSec: data.startSec,
        endSec: data.endSec,
        tagId: data.tagId,
        kind: data.kind,
        refId: data.refId ?? (data.kind === data.segment.kind ? data.segment.refId : undefined),
        payload: data.segment.payload,
        sourceKey: data.segment.sourceKey || "user",
        sourceRunId: data.segment.sourceRunId,
        confidence: data.segment.confidence,
        title: data.title,
        colorHint: data.segment.colorHint,
      }),
    onSuccess: () => {
      invalidateSegments();
      resetForm();
    },
  });
  const deleteMutation = useMutation({
    mutationFn: async (ids: number[]) => {
      for (const id of ids) await videos.segments.delete(videoId, id);
    },
    onSuccess: () => invalidateSegments(),
  });

  // ----- Edit form helpers -----
  // startSec/endSec hold the exact time behind each input: the stored or chosen time, or the typed text once it
  // parses. Saving them instead of re-parsing the displayed text keeps untouched times at full precision.
  const parsedStart = parseSegmentTimeInput(startText);
  const endTextIsBlank = endText.trim() === "";
  const parsedEnd = endTextIsBlank ? null : parseSegmentTimeInput(endText);
  const nextEndSec = endSec === "" ? undefined : endSec;
  const hasSelectedEntity = kind === "performer" ? selectedPerformerId != null : selectedTagId != null;
  const canSaveSegment =
    parsedStart != null &&
    parsedStart >= 0 &&
    (endTextIsBlank || parsedEnd != null) &&
    (nextEndSec == null || nextEndSec >= startSec) &&
    hasSelectedEntity;

  function resetForm() {
    setAdding(false);
    setEditingId(null);
    setTitle("");
    setKind("tag");
    setStartTimeFromSeconds(0);
    setEndTimeFromSeconds("");
    setSelectedTagId(null);
    setSelectedPerformerId(null);
  }
  // A new segment starts at the current playback position, so the user can keep watching and only
  // fill in the rest (#602).
  function startAdding() {
    resetForm();
    setAdding(true);
    setStartTimeFromSeconds(currentTime);
  }
  function setStartTimeFromSeconds(seconds: number) {
    const normalized = Math.max(0, seconds);
    setStartSec(normalized);
    setStartText(formatSegmentTime(normalized));
  }
  function setEndTimeFromSeconds(seconds: number | "") {
    if (seconds === "") {
      setEndSec("");
      setEndText("");
      return;
    }
    const normalized = Math.max(0, seconds);
    setEndSec(normalized);
    setEndText(formatSegmentTime(normalized));
  }
  function startEditSegment(segment: Segment) {
    setChooser(null);
    setAdding(true);
    setEditingId(segment.id);
    setTitle(segment.title || "");
    setKind(segment.kind?.toLowerCase() === "performer" ? "performer" : "tag");
    setStartTimeFromSeconds(segment.startSec);
    setEndTimeFromSeconds(segment.endSec ?? "");
    setSelectedTagId(segment.kind?.toLowerCase() === "performer" ? null : (segment.tagId ?? null));
    setSelectedPerformerId(
      segment.kind?.toLowerCase() === "performer" && segment.refId != null ? Number(segment.refId) : null,
    );
  }

  const underlyingSegments = (span: ResolvedSpan) =>
    (span.segmentIds ?? [])
      .map((id) => rawSegmentsById.get(id))
      .filter((segment): segment is Segment => segment != null);

  const handleEditSpan = (span: ResolvedSpan) => {
    const segments = underlyingSegments(span);
    if (segments.length === 0) return;
    if (segments.length === 1) {
      startEditSegment(segments[0]);
      return;
    }
    setChooser({ span, segments });
  };
  const handleDeleteSpan = (span: ResolvedSpan) => {
    const segments = underlyingSegments(span);
    if (segments.length === 0) return;
    setPendingDelete({ span, segments });
  };

  const editingSegment = editingId != null ? (rawSegmentsById.get(editingId) ?? null) : null;
  const saveSegment = () => {
    if (!canSaveSegment) return;
    const nextTagId = kind === "tag" ? (selectedTagId ?? undefined) : undefined;
    const nextRefId = kind === "performer" ? (selectedPerformerId ?? undefined) : undefined;
    if (editingSegment) {
      updateMutation.mutate({
        segment: editingSegment,
        startSec,
        endSec: nextEndSec,
        tagId: nextTagId,
        refId: nextRefId,
        kind,
        title: title || undefined,
      });
      return;
    }
    createMutation.mutate({
      title: title || undefined,
      startSec,
      endSec: nextEndSec,
      tagId: nextTagId,
      refId: nextRefId,
      kind,
    });
  };

  // The "add segment at current time" shortcut asks the panel to open the add form, possibly
  // mounting it in the same render. A form that is already open (including an edit) is left alone.
  const [prevAddRequested, setPrevAddRequested] = useState(false);
  if (addRequested !== prevAddRequested) {
    setPrevAddRequested(addRequested);
    if (addRequested && canEdit && !adding) startAdding();
  }
  useEffect(() => {
    if (addRequested) onAddRequestHandled?.();
  }, [addRequested, onAddRequestHandled]);

  const activeFilterCount = countActiveFilters(filter);
  const filterActive = isSegmentFilterActive(filter);

  return (
    <section className="space-y-3">
      <AddToGroupDialog
        open={showAddDialog}
        onClose={() => setShowAddDialog(false)}
        items={selectedEntries}
        onAdded={() => setSelectedSpanKeys(new Set())}
      />

      {/* Header */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-muted">
          <Layers className="h-4 w-4" />
          Segments
          <span className="rounded-full border border-border bg-surface/60 px-2 py-0.5 text-xs font-normal normal-case tracking-normal text-secondary">
            {filterActive ? `${filteredSpans.length} / ${spans.length}` : spans.length}
          </span>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <label className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-muted">
            Profile
            <select
              value={activeProfileId ?? ""}
              onChange={(event) => onProfileChange(Number(event.target.value))}
              className="rounded-lg border border-border bg-card px-2.5 py-1.5 text-sm font-normal normal-case tracking-normal text-foreground focus:border-accent focus:outline-none"
            >
              {orderedProfiles.map((profile) => (
                <option key={profile.id} value={profile.id}>
                  {profile.name}
                  {profile.isDefault ? " (Default)" : ""}
                  {profile.userId == null ? "" : " (Mine)"}
                </option>
              ))}
            </select>
          </label>
          {/* View toggle */}
          <div className="flex items-center rounded-lg border border-border bg-card p-0.5">
            <button
              type="button"
              onClick={() => setViewMode("grouped")}
              className={`inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs ${view === "grouped" ? "bg-accent/15 text-accent" : "text-secondary hover:text-foreground"}`}
              title="Group by track"
            >
              <ListTree className="h-3.5 w-3.5" /> Grouped
            </button>
            <button
              type="button"
              onClick={() => setViewMode("flat")}
              className={`inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs ${view === "flat" ? "bg-accent/15 text-accent" : "text-secondary hover:text-foreground"}`}
              title="Flat timeline order"
            >
              <ListIcon className="h-3.5 w-3.5" /> Flat
            </button>
          </div>
          <button
            type="button"
            onClick={() => setFiltersOpen((value) => !value)}
            className={`inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-sm transition-colors ${filterActive ? "border-accent text-accent" : "border-border text-foreground hover:border-accent"}`}
          >
            <Filter className="h-4 w-4" />
            Filter{activeFilterCount > 0 ? ` (${activeFilterCount})` : ""}
          </button>
          {canEdit ? (
            <button
              type="button"
              onClick={() => (adding ? resetForm() : startAdding())}
              className="inline-flex items-center gap-1.5 rounded-lg border border-border px-2.5 py-1.5 text-sm text-foreground transition-colors hover:border-accent"
            >
              <Plus className="h-4 w-4" /> {adding ? "Cancel" : "Add"}
            </button>
          ) : null}
          {selectedEntries.length > 0 ? (
            <button
              type="button"
              onClick={() => setShowAddDialog(true)}
              className="inline-flex items-center gap-1.5 rounded-lg border border-border px-2.5 py-1.5 text-sm text-foreground transition-colors hover:border-accent"
            >
              <Plus className="h-4 w-4" /> Add {selectedEntries.length} to group
            </button>
          ) : null}
        </div>
      </div>

      {/* Filter bar */}
      {filtersOpen ? (
        <FilterBar filter={filter} onFilterChange={onFilterChange} facets={facets} tagGroups={allTagGroups} />
      ) : null}

      {/* Add / Edit form */}
      {adding && canEdit ? (
        <SegmentForm
          editing={editingSegment != null}
          title={title}
          setTitle={setTitle}
          kind={kind}
          setKind={(next) => {
            setKind(next);
            if (next === "performer") setSelectedTagId(null);
            else setSelectedPerformerId(null);
          }}
          startText={startText}
          endText={endText}
          onStartChange={(value) => {
            setStartText(value);
            const parsed = parseSegmentTimeInput(value);
            if (parsed != null) setStartSec(parsed);
          }}
          onStartBlur={() => setStartText(formatSegmentTime(startSec))}
          onEndChange={(value) => {
            setEndText(value);
            if (value.trim() === "") {
              setEndSec("");
              return;
            }
            const parsed = parseSegmentTimeInput(value);
            if (parsed != null) setEndSec(parsed);
          }}
          onEndBlur={() => setEndText(endSec === "" ? "" : formatSegmentTime(endSec))}
          onUseCurrentStart={() => setStartTimeFromSeconds(currentTime)}
          onUseCurrentEnd={() => setEndTimeFromSeconds(currentTime)}
          selectedTagId={selectedTagId}
          setSelectedTagId={setSelectedTagId}
          selectedPerformerId={selectedPerformerId}
          setSelectedPerformerId={setSelectedPerformerId}
          canSave={canSaveSegment}
          saving={createMutation.isPending || updateMutation.isPending}
          onCancel={resetForm}
          onSave={saveSegment}
        />
      ) : null}

      {/* Body */}
      {loading ? (
        <div className="text-sm text-secondary">Loading segments…</div>
      ) : spans.length === 0 ? (
        <div className="py-6 text-sm text-secondary">This profile has no segments for the current video.</div>
      ) : filteredSpans.length === 0 ? (
        <div className="flex flex-wrap items-center justify-between gap-2 py-6 text-sm text-secondary">
          <span>No segments match the current filter.</span>
          <button
            type="button"
            onClick={() => onFilterChange(EMPTY_SEGMENT_FILTER)}
            className="text-accent hover:underline"
          >
            Clear filter
          </button>
        </div>
      ) : view === "grouped" ? (
        <GroupedList
          facesById={facesById}
          spans={sortedSpans}
          rawSegmentsById={rawSegmentsById}
          selectedSpanKeys={selectedSpanKeys}
          onToggleSelect={toggleSelection}
          onSeek={onSeek}
          canEdit={canEdit}
          onEdit={handleEditSpan}
          onDelete={handleDeleteSpan}
          onOpen={(span) => onNavigate(spanRoute(span))}
          onOpenFace={(faceId) => onNavigate({ page: "face", id: faceId })}
          spanRoute={spanRoute}
          personColors={personColors}
          onDecide={canDecideFaces ? faceDecision.decide : undefined}
          decidingFaceId={faceDecision.decidingFaceId}
          decisionError={faceDecision.decisionError}
        />
      ) : (
        <div className="mt-3 space-y-1">
          {sortedSpans.map((span) => (
            <SegmentRow
              key={span.spanKey}
              span={span}
              rawSegmentsById={rawSegmentsById}
              facesById={facesById}
              checked={selectedSpanKeys.has(span.spanKey)}
              onToggleSelect={() => toggleSelection(span.spanKey)}
              onSeek={onSeek}
              canEdit={canEdit}
              onEdit={() => handleEditSpan(span)}
              onDelete={() => handleDeleteSpan(span)}
              onOpen={() => onNavigate(spanRoute(span))}
              openRoute={spanRoute(span)}
            />
          ))}
        </div>
      )}

      {/* Raw-segment chooser (span backed by multiple raw segments) */}
      {chooser ? (
        <RawSegmentChooser
          span={chooser.span}
          segments={chooser.segments}
          onPick={startEditSegment}
          onClose={() => setChooser(null)}
        />
      ) : null}

      {/* Delete confirmation drilling into raw segments */}
      <ConfirmDialog
        open={pendingDelete != null}
        title="Delete segment"
        message={
          pendingDelete
            ? pendingDelete.segments.length === 1
              ? `Delete the underlying raw segment "${rawSegmentLabel(pendingDelete.segments[0])}" (${formatTime(pendingDelete.segments[0].startSec)})? This cannot be undone.`
              : `This segment combines ${pendingDelete.segments.length} raw segments — deleting it removes all of them (${pendingDelete.segments.map((segment) => `${rawSegmentLabel(segment)} ${formatTime(segment.startSec)}`).join(", ")}). This cannot be undone.`
            : ""
        }
        onConfirm={() => {
          if (pendingDelete) deleteMutation.mutate(pendingDelete.segments.map((segment) => segment.id));
          setPendingDelete(null);
        }}
        onCancel={() => setPendingDelete(null)}
      />
    </section>
  );
}

// ===== Filter bar =====
function FilterBar({
  filter,
  onFilterChange,
  facets,
  tagGroups,
}: {
  filter: SegmentFilterState;
  onFilterChange: (filter: SegmentFilterState) => void;
  facets: { kinds: string[] };
  tagGroups: { id: number; name: string; color?: string | null }[];
}) {
  const update = (patch: Partial<SegmentFilterState>) => onFilterChange({ ...filter, ...patch });

  return (
    <div className="space-y-3 border-t border-border/60 pt-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <label className="space-y-1 text-xs font-medium uppercase tracking-wide text-muted">
          Tags
          <EntityReferenceMultiSelector
            entityType="tag"
            values={filter.tagIds}
            onChange={(tagIds) => update({ tagIds })}
            placeholder="Filter by tags…"
            inputClassName={FILTER_INPUT_CLASS}
          />
        </label>
        <label className="space-y-1 text-xs font-medium uppercase tracking-wide text-muted">
          Faces
          <EntityReferenceMultiSelector
            entityType="face"
            values={filter.faceIds}
            onChange={(faceIds) => update({ faceIds })}
            placeholder="Filter by faces…"
            inputClassName={FILTER_INPUT_CLASS}
          />
        </label>
        <label className="space-y-1 text-xs font-medium uppercase tracking-wide text-muted">
          Performers
          <EntityReferenceMultiSelector
            entityType="performer"
            values={filter.performerIds}
            onChange={(performerIds) => update({ performerIds })}
            placeholder="Filter by performers…"
            inputClassName={FILTER_INPUT_CLASS}
          />
        </label>
      </div>

      {tagGroups.length > 0 ? (
        <ChipRow label="Tag groups">
          {tagGroups.map((group) => (
            <FilterChip
              key={group.id}
              active={filter.tagGroupIds.includes(group.id)}
              onClick={() => update({ tagGroupIds: toggleValue(filter.tagGroupIds, group.id) })}
              color={group.color ?? undefined}
            >
              {group.name}
            </FilterChip>
          ))}
        </ChipRow>
      ) : null}

      {facets.kinds.length > 0 ? (
        <ChipRow label="Kind">
          {facets.kinds.map((value) => (
            <FilterChip
              key={value}
              active={filter.kinds.includes(value)}
              onClick={() => update({ kinds: toggleValue(filter.kinds, value) })}
            >
              {value}
            </FilterChip>
          ))}
        </ChipRow>
      ) : null}

      {isSegmentFilterActive(filter) ? (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={() => onFilterChange(EMPTY_SEGMENT_FILTER)}
            className="inline-flex items-center gap-1 text-xs text-secondary hover:text-foreground"
          >
            <X className="h-3.5 w-3.5" /> Clear all filters
          </button>
        </div>
      ) : null}
    </div>
  );
}

function ChipRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <span className="mr-1 text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      {children}
    </div>
  );
}

function FilterChip({
  active,
  onClick,
  color,
  children,
}: {
  active: boolean;
  onClick: () => void;
  color?: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs transition-colors ${active ? "border-accent bg-accent/15 text-accent" : "border-border bg-card text-secondary hover:border-accent hover:text-foreground"}`}
    >
      {color ? <span className="h-2 w-2 rounded-full" style={{ backgroundColor: color }} /> : null}
      {children}
    </button>
  );
}

// ===== Grouped list =====
interface PersonFace {
  faceId: number;
  face?: Face;
  count: number;
  totalSec: number;
}

interface PersonGroup {
  key: string;
  name: string;
  suggested: boolean;
  section: SegmentSection;
  items: ResolvedSpan[];
  totalSec: number;
  /** The faces the person appears through in this video, most seen first. */
  faces: PersonFace[];
}

// Segments are grouped by the person they are about, so all of a performer's faces in the video are one group. The
// group's faces are shown as a small stack of face crops, which opens a list of them to tell them apart.
function GroupedList({
  spans,
  rawSegmentsById,
  facesById,
  selectedSpanKeys,
  onToggleSelect,
  onSeek,
  canEdit,
  onEdit,
  onDelete,
  onOpen,
  onOpenFace,
  spanRoute,
  personColors,
  onDecide,
  decidingFaceId,
  decisionError,
}: {
  spans: ResolvedSpan[];
  rawSegmentsById: Map<number, Segment>;
  facesById?: ReadonlyMap<number, Face>;
  selectedSpanKeys: Set<string>;
  onToggleSelect: (spanKey: string) => void;
  onSeek?: (time: number) => void;
  canEdit: boolean;
  onEdit: (span: ResolvedSpan) => void;
  onDelete: (span: ResolvedSpan) => void;
  onOpen: (span: ResolvedSpan) => void;
  onOpenFace: (faceId: number) => void;
  /** The page of a segment, for links that can open in a new tab. */
  spanRoute: (span: ResolvedSpan) => Route;
  personColors?: ReadonlyMap<string, string>;
  /** Accept or reject a face's suggested performer, when the viewer may. */
  onDecide?: (faceId: number, performerId: number, decision: "accept" | "reject") => void;
  decidingFaceId?: number;
  decisionError?: { faceId?: number; error: Error };
}) {
  const { groups, faceIdBySpanKey } = useMemo(() => {
    const byKey = new Map<string, PersonGroup & { facesById: Map<number, PersonFace> }>();
    const faceIdBySpanKey = new Map<string, number>();
    for (const span of spans) {
      const person = spanPerson(span, rawSegmentsById, facesById);
      const group = byKey.get(person.key) ?? {
        key: person.key,
        name: person.name,
        suggested: person.suggested,
        section: person.section,
        items: [],
        totalSec: 0,
        faces: [],
        facesById: new Map<number, PersonFace>(),
      };
      const duration = Math.max(0, span.endSec - span.startSec);
      group.items.push(span);
      group.totalSec += duration;
      if (person.faceId != null) {
        faceIdBySpanKey.set(span.spanKey, person.faceId);
        const entry = group.facesById.get(person.faceId) ?? {
          faceId: person.faceId,
          face: facesById?.get(person.faceId),
          count: 0,
          totalSec: 0,
        };
        entry.count += 1;
        entry.totalSec += duration;
        group.facesById.set(person.faceId, entry);
      }
      byKey.set(person.key, group);
    }
    const groups: PersonGroup[] = Array.from(byKey.values())
      .map(({ facesById: groupFaces, ...group }) => ({
        ...group,
        faces: Array.from(groupFaces.values()).sort(
          (left, right) => right.totalSec - left.totalSec || left.faceId - right.faceId,
        ),
      }))
      .sort((left, right) => right.totalSec - left.totalSec || compareNatural(left.name, right.name));
    return { groups, faceIdBySpanKey };
  }, [spans, rawSegmentsById, facesById]);

  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [facesOpen, setFacesOpen] = useState<Set<string>>(new Set());
  const toggleIn = (setter: typeof setCollapsed, key: string) =>
    setter((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  return (
    // People come first, then tags, then anything else; within a section the longest groups lead.
    <div className="space-y-4">
      {SEGMENT_SECTIONS.map((section) => {
        const sectionGroups = groups.filter((group) => group.section === section.key);
        if (sectionGroups.length === 0) return null;
        return (
          <section key={section.key} aria-labelledby={`segment-section-${section.key}`}>
            <h5
              id={`segment-section-${section.key}`}
              className="mb-1 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted"
            >
              {section.title}
              <span className="font-normal normal-case tracking-normal">{sectionGroups.length}</span>
            </h5>
            <div className="divide-y divide-border/50">
              {sectionGroups.map((group) => {
                const open = !collapsed.has(group.key);
                // A suggested group always lists its faces, since that is where each one is accepted or rejected.
                const listsFaces = group.faces.length > 1 || (group.suggested && group.faces.length > 0);
                const showFaces = facesOpen.has(group.key) && listsFaces;
                return (
                  <div key={group.key}>
                    <div className="flex items-center justify-between gap-3 py-1.5">
                      <button
                        type="button"
                        onClick={() => toggleIn(setCollapsed, group.key)}
                        aria-expanded={open}
                        className="flex min-w-0 flex-1 items-center gap-2 text-left"
                      >
                        {open ? (
                          <ChevronDown className="h-4 w-4 shrink-0 text-muted" />
                        ) : (
                          <ChevronRight className="h-4 w-4 shrink-0 text-muted" />
                        )}
                        {personColors?.get(group.key) ? (
                          // The colour of the person's bars on the timeline's face lanes.
                          <span
                            aria-hidden
                            className="h-2.5 w-2.5 shrink-0 rounded-full"
                            style={{ backgroundColor: personColors.get(group.key) }}
                          />
                        ) : null}
                        <ShownNameText name={group.name} suggested={group.suggested} className="text-sm font-medium" />
                      </button>
                      {listsFaces ? (
                        <button
                          type="button"
                          onClick={() => toggleIn(setFacesOpen, group.key)}
                          aria-expanded={showFaces}
                          aria-label={`${showFaces ? "Hide" : "Show"} the ${facesLabel(group.faces.length)} of ${group.name}`}
                          className="flex shrink-0 items-center gap-1.5 rounded-full border border-border px-1.5 py-0.5 text-xs text-secondary hover:border-accent hover:text-foreground"
                        >
                          <FaceStack faces={group.faces} />
                          <span>{facesLabel(group.faces.length)}</span>
                          {showFaces ? <ChevronUp className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />}
                        </button>
                      ) : group.faces.length === 1 ? (
                        <a
                          {...createNestedRouteLinkProps<HTMLAnchorElement>(
                            { page: "face", id: group.faces[0].faceId },
                            () => onOpenFace(group.faces[0].faceId),
                          )}
                          aria-label={`Open face ${faceKeyLabel(group.faces[0].face, group.faces[0].faceId)}`}
                          title="Open face"
                          className="shrink-0 rounded-full hover:ring-1 hover:ring-accent"
                        >
                          <FaceStack faces={group.faces} />
                        </a>
                      ) : null}
                      <span className="flex shrink-0 items-center gap-2 text-xs text-secondary">
                        <span>{group.items.length}×</span>
                        <span className="font-mono">{formatTime(group.totalSec)}</span>
                      </span>
                    </div>
                    {showFaces ? (
                      <ul className="mb-1.5 ml-6 space-y-1 rounded-lg border border-border bg-surface/30 p-1.5">
                        {group.faces.map((entry) => (
                          <li key={entry.faceId} className="flex flex-wrap items-center gap-2 text-xs">
                            <FaceCrop face={entry.face} className="h-6 w-6" />
                            <span className="min-w-0 flex-1 truncate text-foreground">
                              {faceKeyLabel(entry.face, entry.faceId)}
                            </span>
                            <span className="shrink-0 text-secondary">{entry.count}×</span>
                            <span className="shrink-0 font-mono text-secondary">{formatTime(entry.totalSec)}</span>
                            {group.suggested && onDecide && entry.face?.topSuggestion ? (
                              <SuggestionDecision
                                performerId={entry.face.topSuggestion.performerId}
                                pending={decidingFaceId === entry.faceId}
                                onDecide={(performerId, decision) => onDecide(entry.faceId, performerId, decision)}
                                onReview={() => onOpenFace(entry.faceId)}
                              />
                            ) : null}
                            <a
                              {...createNestedRouteLinkProps<HTMLAnchorElement>(
                                { page: "face", id: entry.faceId },
                                () => onOpenFace(entry.faceId),
                              )}
                              className="shrink-0 text-accent hover:underline"
                            >
                              Open face
                            </a>
                            {decisionError?.faceId === entry.faceId ? (
                              <span role="alert" className="basis-full text-red-400">
                                {decisionError.error.message}
                              </span>
                            ) : null}
                          </li>
                        ))}
                      </ul>
                    ) : null}
                    {open ? (
                      <div className="space-y-0.5 pb-1.5 pl-6">
                        {group.items.map((span) => {
                          const faceId = faceIdBySpanKey.get(span.spanKey);
                          return (
                            <SegmentRow
                              key={span.spanKey}
                              span={span}
                              rawSegmentsById={rawSegmentsById}
                              facesById={facesById}
                              checked={selectedSpanKeys.has(span.spanKey)}
                              onToggleSelect={() => onToggleSelect(span.spanKey)}
                              onSeek={onSeek}
                              canEdit={canEdit}
                              onEdit={() => onEdit(span)}
                              onDelete={() => onDelete(span)}
                              onOpen={() => onOpen(span)}
                              openRoute={spanRoute(span)}
                              // With the person's faces listed, each segment shows which of them it is.
                              leading={
                                showFaces && faceId != null ? (
                                  <FaceCrop face={facesById?.get(faceId)} className="h-4 w-4" />
                                ) : undefined
                              }
                              hideLabel
                            />
                          );
                        })}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
          </section>
        );
      })}
    </div>
  );
}

// A name with its "suggested" marker. The marker is hidden from screen readers, so a suggested name is read as one
// phrase ending in "(suggested)" instead.
function ShownNameText({ name, suggested, className = "" }: { name: string; suggested: boolean; className?: string }) {
  if (!suggested) return <span className={`truncate text-foreground ${className}`}>{name}</span>;
  return (
    <>
      <span aria-hidden className={`truncate text-foreground ${className}`}>
        {name}
      </span>
      <SuggestedNameMarker />
      <span className="sr-only">{`${name} (suggested)`}</span>
    </>
  );
}

const SEGMENT_SECTIONS: Array<{ key: SegmentSection; title: string }> = [
  { key: "people", title: "People" },
  { key: "tags", title: "Tags" },
  { key: "other", title: "Other" },
];

function facesLabel(count: number) {
  return count === 1 ? "1 face" : `${count} faces`;
}

// Accepting links the face to its suggested performer; a match that would import a new performer is reviewed on the
// face page.
// ===== Compact row =====
function SegmentRow({
  span,
  rawSegmentsById,
  facesById,
  checked,
  onToggleSelect,
  onSeek,
  canEdit,
  onEdit,
  onDelete,
  onOpen,
  openRoute,
  leading,
  hideLabel = false,
}: {
  span: ResolvedSpan;
  rawSegmentsById: Map<number, Segment>;
  facesById?: ReadonlyMap<number, Face>;
  checked: boolean;
  onToggleSelect: () => void;
  onSeek?: (time: number) => void;
  canEdit: boolean;
  onEdit: () => void;
  onDelete: () => void;
  onOpen: () => void;
  /** The segment page, so the open button is a link that can open in a new tab. */
  openRoute: Route;
  /** Shown before the time range, such as the crop of the face the segment shows. */
  leading?: React.ReactNode;
  hideLabel?: boolean;
}) {
  const shown = resolveSpanName(span, rawSegmentsById, facesById);
  const label = shown.name;
  const rawCount = span.segmentIds?.length ?? 0;
  return (
    <div
      className={`group flex items-center gap-2 rounded px-1.5 py-1 text-sm transition-colors ${checked ? "bg-accent/10" : "hover:bg-surface/50"}`}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={onToggleSelect}
        className="h-3.5 w-3.5 shrink-0 rounded border-border accent-accent"
        title="Select for group"
      />
      <button className="flex min-w-0 flex-1 items-center gap-2.5 text-left" onClick={() => onSeek?.(span.startSec)}>
        {leading}
        <span className="w-24 shrink-0 font-mono text-xs text-accent">
          {formatTime(span.startSec)}
          {span.endSec > span.startSec ? `–${formatTime(span.endSec)}` : ""}
        </span>
        {!hideLabel ? (
          <span className="flex min-w-0 items-center gap-1.5 group-hover:text-accent">
            <ShownNameText name={label} suggested={shown.suggested} />
          </span>
        ) : null}
        {/* A face or tag segment's name or group already says what it is; other kinds keep their label. */}
        {span.kind && span.kind !== "tag" && span.kind.toLowerCase() !== "face" ? (
          <span className="rounded bg-surface px-1.5 py-0.5 text-xs text-secondary">{span.kind}</span>
        ) : null}
        {rawCount > 1 ? (
          <span className="rounded bg-surface px-1.5 py-0.5 text-[10px] text-muted" title={`${rawCount} raw segments`}>
            {rawCount} raw
          </span>
        ) : null}
      </button>
      {/* Opening the segment page is always on offer. On a wide screen with a mouse, editing and deleting appear on
          hover or keyboard focus; on a narrow or touch screen they always show, with room to tap them. */}
      <a
        {...createNestedRouteLinkProps<HTMLAnchorElement>(openRoute, onOpen)}
        className="shrink-0 text-muted hover:text-accent pointer-coarse:p-1.5"
        title="Open segment"
        aria-label="Open segment"
      >
        <SegmentIcon className="h-3.5 w-3.5" />
      </a>
      <div className="flex shrink-0 items-center gap-1.5 transition-opacity md:pointer-fine:opacity-0 md:pointer-fine:group-hover:opacity-100 md:pointer-fine:focus-within:opacity-100">
        {canEdit ? (
          <button
            type="button"
            onClick={onEdit}
            className="text-muted hover:text-accent pointer-coarse:p-1.5"
            title="Edit segment"
            aria-label="Edit segment"
          >
            <Pencil className="h-3.5 w-3.5" />
          </button>
        ) : null}
        {canEdit ? (
          <button
            type="button"
            onClick={onDelete}
            className="text-muted hover:text-red-400 pointer-coarse:p-1.5"
            title="Delete segment"
            aria-label="Delete segment"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        ) : null}
      </div>
    </div>
  );
}

// ===== Raw segment chooser modal =====
function RawSegmentChooser({
  segments,
  onPick,
  onClose,
}: {
  span: ResolvedSpan;
  segments: Segment[];
  onPick: (segment: Segment) => void;
  onClose: () => void;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" onClick={onClose}>
      <div
        className="w-full max-w-md rounded-2xl border border-border bg-card p-5 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="mb-1 text-sm font-semibold text-foreground">Edit a raw segment</div>
        <p className="mb-4 text-sm text-secondary">
          This segment combines {segments.length} raw segments. Choose which one to edit.
        </p>
        <div className="space-y-1.5">
          {segments.map((segment) => (
            <button
              key={segment.id}
              type="button"
              onClick={() => onPick(segment)}
              className="flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-surface/40 px-3 py-2 text-left text-sm transition-colors hover:border-accent"
            >
              <span className="truncate text-foreground">{rawSegmentLabel(segment)}</span>
              <span className="shrink-0 font-mono text-xs text-accent">
                {formatTime(segment.startSec)}
                {segment.endSec != null ? `–${formatTime(segment.endSec)}` : ""}
              </span>
            </button>
          ))}
        </div>
        <div className="mt-4 flex justify-end">
          <button type="button" onClick={onClose} className="px-3 py-1.5 text-sm text-secondary hover:text-foreground">
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ===== Add / edit form =====
function SegmentForm(props: {
  editing: boolean;
  title: string;
  setTitle: (value: string) => void;
  kind: "tag" | "performer";
  setKind: (value: "tag" | "performer") => void;
  startText: string;
  endText: string;
  onStartChange: (value: string) => void;
  onStartBlur: () => void;
  onEndChange: (value: string) => void;
  onEndBlur: () => void;
  onUseCurrentStart: () => void;
  onUseCurrentEnd: () => void;
  selectedTagId: number | null;
  setSelectedTagId: (value: number | null) => void;
  selectedPerformerId: number | null;
  setSelectedPerformerId: (value: number | null) => void;
  canSave: boolean;
  saving: boolean;
  onCancel: () => void;
  onSave: () => void;
}) {
  const inputCls = "min-w-0 flex-1 rounded border border-border bg-input px-3 py-1.5 font-mono text-sm text-foreground";
  return (
    <div className="mt-3 space-y-2 rounded-xl border border-border bg-card p-3">
      <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_12rem]">
        <input
          type="text"
          placeholder="Segment title (optional)"
          value={props.title}
          onChange={(event) => props.setTitle(event.target.value)}
          className="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground"
        />
        <select
          value={props.kind}
          onChange={(event) => props.setKind(event.target.value === "performer" ? "performer" : "tag")}
          className="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground"
        >
          <option value="tag">Tag</option>
          <option value="performer">Performer</option>
        </select>
      </div>
      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-[minmax(7rem,0.75fr)_minmax(7rem,0.75fr)_minmax(10rem,1.8fr)]">
        <label className="space-y-1">
          <span className="text-xs text-secondary">Start</span>
          <div className="flex gap-1">
            <input
              type="text"
              inputMode="decimal"
              placeholder="0:00"
              value={props.startText}
              onChange={(event) => props.onStartChange(event.target.value)}
              onBlur={props.onStartBlur}
              className={inputCls}
            />
            <button
              type="button"
              onClick={props.onUseCurrentStart}
              className="inline-flex items-center justify-center rounded border border-border px-2 text-secondary hover:text-foreground"
              title="Use current time"
            >
              <Clock className="h-3.5 w-3.5" />
            </button>
          </div>
        </label>
        <label className="space-y-1">
          <span className="text-xs text-secondary">End</span>
          <div className="flex gap-1">
            <input
              type="text"
              inputMode="decimal"
              placeholder="Optional"
              value={props.endText}
              onChange={(event) => props.onEndChange(event.target.value)}
              onBlur={props.onEndBlur}
              className={inputCls}
            />
            <button
              type="button"
              onClick={props.onUseCurrentEnd}
              className="inline-flex items-center justify-center rounded border border-border px-2 text-secondary hover:text-foreground"
              title="Use current time"
            >
              <Clock className="h-3.5 w-3.5" />
            </button>
          </div>
        </label>
        {props.kind === "tag" ? (
          <label className="min-w-0 space-y-1 sm:col-span-2 xl:col-span-1">
            <span className="text-xs text-secondary">Tag</span>
            <EntityReferenceSelector
              entityType="tag"
              value={props.selectedTagId ?? undefined}
              onChange={(tagId) => props.setSelectedTagId(tagId ?? null)}
              placeholder="Search tags…"
              inputClassName="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground"
            />
          </label>
        ) : (
          <label className="min-w-0 space-y-1 sm:col-span-2 xl:col-span-1">
            <span className="text-xs text-secondary">Performer</span>
            <EntityReferenceSelector
              entityType="performer"
              value={props.selectedPerformerId ?? undefined}
              onChange={(performerId) => props.setSelectedPerformerId(performerId ?? null)}
              placeholder="Search performers…"
              inputClassName="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground"
            />
          </label>
        )}
      </div>
      {!props.canSave ? <div className="text-xs text-red-300">Use valid times and choose a {props.kind}.</div> : null}
      <div className="flex justify-end gap-2">
        <button onClick={props.onCancel} className="px-3 py-1 text-sm text-secondary hover:text-foreground">
          Cancel
        </button>
        <button
          onClick={props.onSave}
          disabled={!props.canSave || props.saving}
          className="rounded bg-accent px-3 py-1 text-sm text-white hover:bg-accent-hover disabled:opacity-50"
        >
          {props.editing ? "Update" : "Save"}
        </button>
      </div>
    </div>
  );
}

// ===== Helpers =====
function toggleValue<T>(values: T[], value: T): T[] {
  return values.includes(value) ? values.filter((existing) => existing !== value) : [...values, value];
}

function cleanSourceLabel(value: string) {
  return value.replace(/^ext:ai\./, "").replace(/^ext:/, "");
}

function isRawDataLabel(value: string) {
  return value.startsWith("{") || value.startsWith("[") || value.includes('"probabilit');
}

function rawSegmentLabel(segment: Segment) {
  const candidate =
    segment.tagName?.trim() ||
    segment.performerName?.trim() ||
    segment.refLabel?.trim() ||
    (segment.title?.trim() && !isRawDataLabel(segment.title) ? segment.title.trim() : "");
  if (candidate && candidate.toLowerCase() !== "performer") return candidate;
  return segment.kind?.trim() || cleanSourceLabel(segment.sourceKey || "") || "Segment";
}

function formatTime(seconds: number) {
  const safe = Math.max(0, seconds || 0);
  const hours = Math.floor(safe / 3600);
  const minutes = Math.floor((safe % 3600) / 60);
  const secs = Math.floor(safe % 60);
  const fractional = safe % 1;
  const secText =
    fractional > 0 && hours === 0
      ? `${secs.toString().padStart(2, "0")}.${Math.round(fractional * 10)}`
      : secs.toString().padStart(2, "0");
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, "0")}:${secs.toString().padStart(2, "0")}`
    : `${minutes}:${secText}`;
}
