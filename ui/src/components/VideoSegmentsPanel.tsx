import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Layers, Plus, ExternalLink, Pencil, Trash2, Clock, Filter, X,
  ChevronDown, ChevronRight, ListTree, List as ListIcon,
} from "lucide-react";
import { videos, tagGroups as tagGroupsApi } from "../api/client";
import type { ResolvedSpan, Segment, SegmentDisplayProfile } from "../api/types";
import { AddToGroupDialog, type AddToGroupEntry } from "./AddToGroupDialog";
import { ConfirmDialog } from "./ConfirmDialog";
import { EntityReferenceSelector, EntityReferenceMultiSelector } from "./EntityReferenceSelector";
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
  onNavigate: (r: any) => void;
}

type ViewMode = "grouped" | "flat";

const VIEW_STORAGE_KEY = "cove.segments.viewMode";
const FILTER_INPUT_CLASS = "w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none";

export function VideoSegmentsPanel({
  videoId,
  spans,
  rawSegments,
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
  onNavigate,
}: Props) {
  const queryClient = useQueryClient();
  const [selectedSpanKeys, setSelectedSpanKeys] = useState<Set<string>>(new Set());
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [view, setView] = useState<ViewMode>(() => {
    if (typeof window === "undefined") return "grouped";
    return window.localStorage.getItem(VIEW_STORAGE_KEY) === "flat" ? "flat" : "grouped";
  });
  const setViewMode = (next: ViewMode) => {
    setView(next);
    try { window.localStorage.setItem(VIEW_STORAGE_KEY, next); } catch { /* ignore */ }
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
  const filterContext = useMemo<SegmentFilterContext>(() => ({ rawSegmentsById, tagIdToGroupId }), [rawSegmentsById, tagIdToGroupId]);

  const orderedProfiles = useMemo(
    () => [...profiles].sort((left, right) => Number(right.isDefault) - Number(left.isDefault) || left.name.localeCompare(right.name)),
    [profiles],
  );
  const activeProfileId = currentProfileId ?? orderedProfiles.find((profile) => profile.isDefault)?.id ?? orderedProfiles[0]?.id;

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
  const { data: allTagGroups = [] } = useQuery({ queryKey: ["taggroups"], queryFn: () => tagGroupsApi.list() });

  useEffect(() => {
    setSelectedSpanKeys(new Set());
  }, [currentProfileId, videoId]);

  const selectedEntries = useMemo<AddToGroupEntry[]>(() => {
    return filteredSpans
      .filter((span) => selectedSpanKeys.has(span.spanKey))
      .map((span) => ({
        key: span.spanKey,
        videoId,
        spanKey: span.spanKey,
        title: spanLabel(span, rawSegmentsById),
        profileId: activeProfileId,
      }));
  }, [filteredSpans, selectedSpanKeys, videoId, rawSegmentsById, activeProfileId]);

  const toggleSelection = (spanKey: string) => {
    setSelectedSpanKeys((current) => {
      const next = new Set(current);
      if (next.has(spanKey)) next.delete(spanKey); else next.add(spanKey);
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
    mutationFn: (data: { title?: string; kind?: string; startSec: number; endSec?: number; tagId?: number; refId?: number }) =>
      videos.segments.create(videoId, { startSec: data.startSec, endSec: data.endSec, tagId: data.tagId, refId: data.refId, kind: data.kind, title: data.title }),
    onSuccess: () => { invalidateSegments(); resetForm(); },
  });
  const updateMutation = useMutation({
    mutationFn: (data: { segment: Segment; startSec: number; endSec?: number; tagId?: number; refId?: number; kind?: string; title?: string }) =>
      videos.segments.update(videoId, data.segment.id, {
        startSec: data.startSec, endSec: data.endSec, tagId: data.tagId, kind: data.kind,
        refId: data.refId ?? (data.kind === data.segment.kind ? data.segment.refId : undefined),
        payload: data.segment.payload, sourceKey: data.segment.sourceKey || "user", sourceRunId: data.segment.sourceRunId,
        confidence: data.segment.confidence, title: data.title, colorHint: data.segment.colorHint,
      }),
    onSuccess: () => { invalidateSegments(); resetForm(); },
  });
  const deleteMutation = useMutation({
    mutationFn: async (ids: number[]) => { for (const id of ids) await videos.segments.delete(videoId, id); },
    onSuccess: () => invalidateSegments(),
  });

  // ----- Edit form helpers -----
  const parsedStart = parseSegmentTimeInput(startText);
  const parsedEnd = endText.trim() === "" ? null : parseSegmentTimeInput(endText);
  const hasSelectedEntity = kind === "performer" ? selectedPerformerId != null : selectedTagId != null;
  const canSaveSegment = parsedStart != null && parsedStart >= 0 && (parsedEnd == null || parsedEnd >= parsedStart) && hasSelectedEntity;

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
  function setStartTimeFromSeconds(seconds: number) {
    const normalized = Math.max(0, seconds);
    setStartSec(normalized);
    setStartText(formatSegmentTimeInput(normalized));
  }
  function setEndTimeFromSeconds(seconds: number | "") {
    if (seconds === "") { setEndSec(""); setEndText(""); return; }
    const normalized = Math.max(0, seconds);
    setEndSec(normalized);
    setEndText(formatSegmentTimeInput(normalized));
  }
  function startEditSegment(segment: Segment) {
    setChooser(null);
    setAdding(true);
    setEditingId(segment.id);
    setTitle(segment.title || "");
    setKind(segment.kind?.toLowerCase() === "performer" ? "performer" : "tag");
    setStartTimeFromSeconds(segment.startSec);
    setEndTimeFromSeconds(segment.endSec ?? "");
    setSelectedTagId(segment.kind?.toLowerCase() === "performer" ? null : segment.tagId ?? null);
    setSelectedPerformerId(segment.kind?.toLowerCase() === "performer" && segment.refId != null ? Number(segment.refId) : null);
  }

  const underlyingSegments = (span: ResolvedSpan) =>
    (span.segmentIds ?? []).map((id) => rawSegmentsById.get(id)).filter((segment): segment is Segment => segment != null);

  const handleEditSpan = (span: ResolvedSpan) => {
    const segments = underlyingSegments(span);
    if (segments.length === 0) return;
    if (segments.length === 1) { startEditSegment(segments[0]); return; }
    setChooser({ span, segments });
  };
  const handleDeleteSpan = (span: ResolvedSpan) => {
    const segments = underlyingSegments(span);
    if (segments.length === 0) return;
    setPendingDelete({ span, segments });
  };

  const editingSegment = editingId != null ? rawSegmentsById.get(editingId) ?? null : null;
  const saveSegment = () => {
    if (!canSaveSegment || parsedStart == null) return;
    const nextEndSec = parsedEnd == null ? undefined : parsedEnd;
    const nextTagId = kind === "tag" ? selectedTagId ?? undefined : undefined;
    const nextRefId = kind === "performer" ? selectedPerformerId ?? undefined : undefined;
    if (editingSegment) {
      updateMutation.mutate({ segment: editingSegment, startSec: parsedStart, endSec: nextEndSec, tagId: nextTagId, refId: nextRefId, kind, title: title || undefined });
      return;
    }
    createMutation.mutate({ title: title || undefined, startSec: parsedStart, endSec: nextEndSec, tagId: nextTagId, refId: nextRefId, kind });
  };

  const activeFilterCount = countActiveFilters(filter);
  const filterActive = isSegmentFilterActive(filter);

  return (
    <section className="space-y-3">
      <AddToGroupDialog open={showAddDialog} onClose={() => setShowAddDialog(false)} items={selectedEntries} onAdded={() => setSelectedSpanKeys(new Set())} />

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
                  {profile.name}{profile.isDefault ? " (Default)" : ""}{profile.userId == null ? "" : " (Mine)"}
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
              onClick={() => (adding ? resetForm() : setAdding(true))}
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
        <FilterBar
          filter={filter}
          onFilterChange={onFilterChange}
          facets={facets}
          tagGroups={allTagGroups}
        />
      ) : null}

      {/* Add / Edit form */}
      {adding && canEdit ? (
        <SegmentForm
          editing={editingSegment != null}
          title={title}
          setTitle={setTitle}
          kind={kind}
          setKind={(next) => { setKind(next); if (next === "performer") setSelectedTagId(null); else setSelectedPerformerId(null); }}
          startText={startText}
          endText={endText}
          onStartChange={(value) => { setStartText(value); const parsed = parseSegmentTimeInput(value); if (parsed != null) setStartSec(parsed); }}
          onStartBlur={() => setStartText(formatSegmentTimeInput(startSec))}
          onEndChange={(value) => { setEndText(value); if (value.trim() === "") { setEndSec(""); return; } const parsed = parseSegmentTimeInput(value); if (parsed != null) setEndSec(parsed); }}
          onEndBlur={() => setEndText(endSec === "" ? "" : formatSegmentTimeInput(endSec))}
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
          <button type="button" onClick={() => onFilterChange(EMPTY_SEGMENT_FILTER)} className="text-accent hover:underline">Clear filter</button>
        </div>
      ) : view === "grouped" ? (
        <GroupedList
          spans={sortedSpans}
          rawSegmentsById={rawSegmentsById}
          selectedSpanKeys={selectedSpanKeys}
          onToggleSelect={toggleSelection}
          onSeek={onSeek}
          canEdit={canEdit}
          onEdit={handleEditSpan}
          onDelete={handleDeleteSpan}
          onOpen={(span) => onNavigate({ page: "video-span", id: videoId, spanKey: span.spanKey, profileId: activeProfileId })}
        />
      ) : (
        <div className="mt-3 space-y-1">
          {sortedSpans.map((span) => (
            <SegmentRow
              key={span.spanKey}
              span={span}
              rawSegmentsById={rawSegmentsById}
              checked={selectedSpanKeys.has(span.spanKey)}
              onToggleSelect={() => toggleSelection(span.spanKey)}
              onSeek={onSeek}
              canEdit={canEdit}
              onEdit={() => handleEditSpan(span)}
              onDelete={() => handleDeleteSpan(span)}
              onOpen={() => onNavigate({ page: "video-span", id: videoId, spanKey: span.spanKey, profileId: activeProfileId })}
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
        message={pendingDelete
          ? pendingDelete.segments.length === 1
            ? `Delete the underlying raw segment "${rawSegmentLabel(pendingDelete.segments[0])}" (${formatTime(pendingDelete.segments[0].startSec)})? This cannot be undone.`
            : `This segment combines ${pendingDelete.segments.length} raw segments — deleting it removes all of them (${pendingDelete.segments.map((segment) => `${rawSegmentLabel(segment)} ${formatTime(segment.startSec)}`).join(", ")}). This cannot be undone.`
          : ""}
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
            <FilterChip key={value} active={filter.kinds.includes(value)} onClick={() => update({ kinds: toggleValue(filter.kinds, value) })}>
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

function FilterChip({ active, onClick, color, children }: { active: boolean; onClick: () => void; color?: string; children: React.ReactNode }) {
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
function GroupedList({
  spans,
  rawSegmentsById,
  selectedSpanKeys,
  onToggleSelect,
  onSeek,
  canEdit,
  onEdit,
  onDelete,
  onOpen,
}: {
  spans: ResolvedSpan[];
  rawSegmentsById: Map<number, Segment>;
  selectedSpanKeys: Set<string>;
  onToggleSelect: (spanKey: string) => void;
  onSeek?: (time: number) => void;
  canEdit: boolean;
  onEdit: (span: ResolvedSpan) => void;
  onDelete: (span: ResolvedSpan) => void;
  onOpen: (span: ResolvedSpan) => void;
}) {
  const groups = useMemo(() => {
    const byLabel = new Map<string, ResolvedSpan[]>();
    for (const span of spans) {
      const label = spanLabel(span, rawSegmentsById);
      const bucket = byLabel.get(label) ?? [];
      bucket.push(span);
      byLabel.set(label, bucket);
    }
    return Array.from(byLabel.entries())
      .map(([label, items]) => ({
        label,
        items,
        totalSec: items.reduce((sum, span) => sum + Math.max(0, span.endSec - span.startSec), 0),
      }))
      .sort((left, right) => right.totalSec - left.totalSec || left.label.localeCompare(right.label));
  }, [spans, rawSegmentsById]);

  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const toggleGroup = (label: string) => setCollapsed((current) => {
    const next = new Set(current);
    if (next.has(label)) next.delete(label); else next.add(label);
    return next;
  });

  return (
    <div className="divide-y divide-border/50">
      {groups.map((group) => {
        const open = !collapsed.has(group.label);
        return (
          <div key={group.label}>
            <button
              type="button"
              onClick={() => toggleGroup(group.label)}
              className="flex w-full items-center justify-between gap-3 py-1.5 text-left"
            >
              <span className="flex min-w-0 items-center gap-2">
                {open ? <ChevronDown className="h-4 w-4 shrink-0 text-muted" /> : <ChevronRight className="h-4 w-4 shrink-0 text-muted" />}
                <span className="truncate text-sm font-medium text-foreground">{group.label}</span>
              </span>
              <span className="flex shrink-0 items-center gap-2 text-xs text-secondary">
                <span>{group.items.length}×</span>
                <span className="font-mono">{formatTime(group.totalSec)}</span>
              </span>
            </button>
            {open ? (
              <div className="space-y-0.5 pb-1.5 pl-6">
                {group.items.map((span) => (
                  <SegmentRow
                    key={span.spanKey}
                    span={span}
                    rawSegmentsById={rawSegmentsById}
                    checked={selectedSpanKeys.has(span.spanKey)}
                    onToggleSelect={() => onToggleSelect(span.spanKey)}
                    onSeek={onSeek}
                    canEdit={canEdit}
                    onEdit={() => onEdit(span)}
                    onDelete={() => onDelete(span)}
                    onOpen={() => onOpen(span)}
                    hideLabel
                  />
                ))}
              </div>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

// ===== Compact row =====
function SegmentRow({
  span,
  rawSegmentsById,
  checked,
  onToggleSelect,
  onSeek,
  canEdit,
  onEdit,
  onDelete,
  onOpen,
  hideLabel = false,
}: {
  span: ResolvedSpan;
  rawSegmentsById: Map<number, Segment>;
  checked: boolean;
  onToggleSelect: () => void;
  onSeek?: (time: number) => void;
  canEdit: boolean;
  onEdit: () => void;
  onDelete: () => void;
  onOpen: () => void;
  hideLabel?: boolean;
}) {
  const label = spanLabel(span, rawSegmentsById);
  const rawCount = span.segmentIds?.length ?? 0;
  return (
    <div className={`group flex items-center gap-2 rounded px-1.5 py-1 text-sm transition-colors ${checked ? "bg-accent/10" : "hover:bg-surface/50"}`}>
      <input
        type="checkbox"
        checked={checked}
        onChange={onToggleSelect}
        className="h-3.5 w-3.5 shrink-0 rounded border-border accent-accent"
        title="Select for group"
      />
      <button className="flex min-w-0 flex-1 items-center gap-2.5 text-left" onClick={() => onSeek?.(span.startSec)}>
        <span className="w-24 shrink-0 font-mono text-xs text-accent">
          {formatTime(span.startSec)}{span.endSec > span.startSec ? `–${formatTime(span.endSec)}` : ""}
        </span>
        {!hideLabel ? <span className="truncate text-foreground group-hover:text-accent">{label}</span> : null}
        {span.kind && span.kind !== "tag" ? <span className="rounded bg-surface px-1.5 py-0.5 text-xs text-secondary">{span.kind}</span> : null}
        {rawCount > 1 ? <span className="rounded bg-surface px-1.5 py-0.5 text-[10px] text-muted" title={`${rawCount} raw segments`}>{rawCount} raw</span> : null}
      </button>
      <div className="flex shrink-0 items-center gap-1.5 opacity-0 transition-opacity group-hover:opacity-100">
        <button onClick={onOpen} className="text-muted hover:text-accent" title="Open segment"><ExternalLink className="h-3.5 w-3.5" /></button>
        {canEdit ? <button onClick={onEdit} className="text-muted hover:text-accent" title="Edit segment"><Pencil className="h-3.5 w-3.5" /></button> : null}
        {canEdit ? <button onClick={onDelete} className="text-muted hover:text-red-400" title="Delete segment"><Trash2 className="h-3.5 w-3.5" /></button> : null}
      </div>
    </div>
  );
}

// ===== Raw segment chooser modal =====
function RawSegmentChooser({
  span,
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
      <div className="w-full max-w-md rounded-2xl border border-border bg-card p-5 shadow-xl" onClick={(event) => event.stopPropagation()}>
        <div className="mb-1 text-sm font-semibold text-foreground">Edit a raw segment</div>
        <p className="mb-4 text-sm text-secondary">This segment combines {segments.length} raw segments. Choose which one to edit.</p>
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
                {formatTime(segment.startSec)}{segment.endSec != null ? `–${formatTime(segment.endSec)}` : ""}
              </span>
            </button>
          ))}
        </div>
        <div className="mt-4 flex justify-end">
          <button type="button" onClick={onClose} className="px-3 py-1.5 text-sm text-secondary hover:text-foreground">Cancel</button>
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
            <input type="text" inputMode="decimal" placeholder="0:00" value={props.startText} onChange={(event) => props.onStartChange(event.target.value)} onBlur={props.onStartBlur} className={inputCls} />
            <button type="button" onClick={props.onUseCurrentStart} className="inline-flex items-center justify-center rounded border border-border px-2 text-secondary hover:text-foreground" title="Use current time"><Clock className="h-3.5 w-3.5" /></button>
          </div>
        </label>
        <label className="space-y-1">
          <span className="text-xs text-secondary">End</span>
          <div className="flex gap-1">
            <input type="text" inputMode="decimal" placeholder="Optional" value={props.endText} onChange={(event) => props.onEndChange(event.target.value)} onBlur={props.onEndBlur} className={inputCls} />
            <button type="button" onClick={props.onUseCurrentEnd} className="inline-flex items-center justify-center rounded border border-border px-2 text-secondary hover:text-foreground" title="Use current time"><Clock className="h-3.5 w-3.5" /></button>
          </div>
        </label>
        {props.kind === "tag" ? (
          <label className="min-w-0 space-y-1 sm:col-span-2 xl:col-span-1">
            <span className="text-xs text-secondary">Tag</span>
            <EntityReferenceSelector entityType="tag" value={props.selectedTagId ?? undefined} onChange={(tagId) => props.setSelectedTagId(tagId ?? null)} placeholder="Search tags…" inputClassName="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground" />
          </label>
        ) : (
          <label className="min-w-0 space-y-1 sm:col-span-2 xl:col-span-1">
            <span className="text-xs text-secondary">Performer</span>
            <EntityReferenceSelector entityType="performer" value={props.selectedPerformerId ?? undefined} onChange={(performerId) => props.setSelectedPerformerId(performerId ?? null)} placeholder="Search performers…" inputClassName="w-full rounded border border-border bg-input px-3 py-1.5 text-sm text-foreground" />
          </label>
        )}
      </div>
      {!props.canSave ? <div className="text-xs text-red-300">Use valid times and choose a {props.kind}.</div> : null}
      <div className="flex justify-end gap-2">
        <button onClick={props.onCancel} className="px-3 py-1 text-sm text-secondary hover:text-foreground">Cancel</button>
        <button onClick={props.onSave} disabled={!props.canSave || props.saving} className="rounded bg-accent px-3 py-1 text-sm text-white hover:bg-accent-hover disabled:opacity-50">
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
  const candidate = segment.tagName?.trim() || segment.performerName?.trim() || segment.refLabel?.trim()
    || (segment.title?.trim() && !isRawDataLabel(segment.title) ? segment.title.trim() : "");
  if (candidate && candidate.toLowerCase() !== "performer") return candidate;
  return segment.kind?.trim() || cleanSourceLabel(segment.sourceKey || "") || "Segment";
}

function spanLabel(span: ResolvedSpan, rawSegmentsById: Map<number, Segment>) {
  const tagName = span.tagName?.trim();
  if (tagName) return tagName;
  for (const segmentId of span.segmentIds ?? []) {
    const segment = rawSegmentsById.get(segmentId);
    if (!segment) continue;
    const label = rawSegmentLabel(segment);
    if (label && label.toLowerCase() !== "performer") return label;
  }
  const kind = span.kind?.trim();
  if (kind && kind !== "tag") return kind;
  const sourceKey = span.sourceKey?.trim();
  if (sourceKey) return cleanSourceLabel(sourceKey);
  return "Segment";
}

function formatTime(seconds: number) {
  const safe = Math.max(0, seconds || 0);
  const hours = Math.floor(safe / 3600);
  const minutes = Math.floor((safe % 3600) / 60);
  const secs = Math.floor(safe % 60);
  const fractional = safe % 1;
  const secText = fractional > 0 && hours === 0
    ? `${secs.toString().padStart(2, "0")}.${Math.round(fractional * 10)}`
    : secs.toString().padStart(2, "0");
  return hours > 0 ? `${hours}:${minutes.toString().padStart(2, "0")}:${secs.toString().padStart(2, "0")}` : `${minutes}:${secText}`;
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
