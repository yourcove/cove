import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Check, Download, FileText, Headphones, Image as ImageIcon, Layers3, Loader2, X } from "lucide-react";
import { groups, system } from "../api/client";
import type { DownloaderMatch } from "../api/types";
import { formatBatchDownloadSummary, type BatchDownloadResult } from "../utils/batchDownloads";
import { loadScrapeApplyPreferences } from "./videoScrapeUtils";

type SourceDownloadEntity = "Audio" | "Image" | "Text";
type GroupMode = "none" | "existing" | "create";

interface SourceDownloadPreferences {
  groupMode?: GroupMode;
  selectedGroupId?: number | null;
  parentGroupId?: number | null;
}

interface SourceDownloadMetadata {
  title?: string;
  details?: string;
  date?: string;
  rating?: number;
  studioId?: number;
  tagIds?: number[];
  performerIds?: number[];
}

interface Props {
  open: boolean;
  entity: SourceDownloadEntity;
  sourceUrl: string;
  matches: DownloaderMatch[];
  baseTitle?: string;
  metadata?: SourceDownloadMetadata;
  autoApplyMetadata?: boolean;
  onClose: () => void;
  onQueued: () => void;
}

function getMatchTitle(match: DownloaderMatch, fallback: string, entity: SourceDownloadEntity, index: number) {
  return match.label?.trim() || fallback || `${entity} ${index + 1}`;
}

function getDefaultTitle(sourceUrl: string, fallback?: string) {
  if (fallback?.trim()) return fallback.trim();

  try {
    const parsed = new URL(sourceUrl);
    const segment = parsed.pathname.split("/").filter(Boolean).at(-1);
    return segment ? decodeURIComponent(segment).replace(/[._-]+/g, " ").trim() : parsed.hostname;
  } catch {
    return sourceUrl;
  }
}

function getEntityIcon(entity: SourceDownloadEntity) {
  if (entity === "Audio") return <Headphones className="mt-0.5 h-4 w-4 flex-shrink-0 text-muted" />;
  if (entity === "Text") return <FileText className="mt-0.5 h-4 w-4 flex-shrink-0 text-muted" />;
  return <ImageIcon className="mt-0.5 h-4 w-4 flex-shrink-0 text-muted" />;
}

function getPreferencesKey(entity: SourceDownloadEntity) {
  return `cove.source-download:${entity.toLowerCase()}:group`;
}

function loadSourceDownloadPreferences(entity: SourceDownloadEntity): SourceDownloadPreferences {
  try {
    const parsed = JSON.parse(localStorage.getItem(getPreferencesKey(entity)) || "{}");
    return {
      groupMode: parsed.groupMode === "existing" || parsed.groupMode === "create" ? parsed.groupMode : "none",
      selectedGroupId: typeof parsed.selectedGroupId === "number" ? parsed.selectedGroupId : null,
      parentGroupId: typeof parsed.parentGroupId === "number" ? parsed.parentGroupId : null,
    };
  } catch {
    return { groupMode: "none", selectedGroupId: null, parentGroupId: null };
  }
}

function saveSourceDownloadPreferences(entity: SourceDownloadEntity, preferences: SourceDownloadPreferences) {
  localStorage.setItem(getPreferencesKey(entity), JSON.stringify(preferences));
}

export function SourceDownloadDialog({ open, entity, sourceUrl, matches, baseTitle, metadata, autoApplyMetadata = false, onClose, onQueued }: Props) {
  const [selectedIndexes, setSelectedIndexes] = useState<Set<number>>(new Set());
  const [groupMode, setGroupMode] = useState<GroupMode>("none");
  const [selectedGroupId, setSelectedGroupId] = useState<number | null>(null);
  const [parentGroupId, setParentGroupId] = useState<number | null>(null);
  const [groupSearch, setGroupSearch] = useState("");
  const [parentGroupSearch, setParentGroupSearch] = useState("");
  const [containerTitle, setContainerTitle] = useState("");
  const [allowDuplicateDownloads, setAllowDuplicateDownloads] = useState(false);
  const resolvedBaseTitle = useMemo(() => getDefaultTitle(sourceUrl, baseTitle || metadata?.title), [baseTitle, metadata?.title, sourceUrl]);

  const groupOptionsQuery = useQuery({
    queryKey: ["source-download-groups", entity, groupSearch],
    enabled: open && groupMode === "existing",
    queryFn: () => groups.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: groupSearch.trim() || undefined }),
  });

  const parentGroupOptionsQuery = useQuery({
    queryKey: ["source-download-parent-groups", entity, parentGroupSearch],
    enabled: open && groupMode === "create",
    queryFn: () => groups.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: parentGroupSearch.trim() || undefined }),
  });

  useEffect(() => {
    if (!open) return;
    const preferences = loadSourceDownloadPreferences(entity);
    setSelectedIndexes(new Set(matches.map((_, index) => index)));
    setGroupMode(preferences.groupMode ?? "none");
    setSelectedGroupId(preferences.selectedGroupId ?? null);
    setParentGroupId(preferences.parentGroupId ?? null);
    setGroupSearch("");
    setParentGroupSearch("");
    setContainerTitle(resolvedBaseTitle);
    setAllowDuplicateDownloads(false);
  }, [entity, matches, open, resolvedBaseTitle]);

  const selectedMatches = matches.filter((_, index) => selectedIndexes.has(index));
  const hasSelection = selectedMatches.length > 0;

  const queueMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (): Promise<BatchDownloadResult> => {
      if (!hasSelection) throw new Error(`Select at least one ${entity.toLowerCase()} to download.`);

      const title = containerTitle.trim() || resolvedBaseTitle;
      let groupId: number | undefined;

      if (groupMode === "existing") {
        if (!selectedGroupId) throw new Error("Select a group or choose no group.");
        groupId = selectedGroupId;
      }

      if (groupMode === "create") {
        const group = await groups.create({
          name: title,
          urls: [sourceUrl],
          allowedHostTypes: [entity.toLowerCase()],
          tagIds: metadata?.tagIds,
          studioId: metadata?.studioId,
          date: metadata?.date,
          description: metadata?.details,
          rating: metadata?.rating,
        });
        groupId = group.id;

        if (parentGroupId) {
          await groups.addSubGroup(parentGroupId, group.id);
        }
      }

      saveSourceDownloadPreferences(entity, { groupMode, selectedGroupId, parentGroupId });

      const metadataPreferences = loadScrapeApplyPreferences();
      const metadataApplyOptions = autoApplyMetadata
        ? {
            createMissingTags: metadataPreferences.createMissingTags,
            createMissingPerformers: metadataPreferences.createMissingPerformers,
            createMissingStudio: metadataPreferences.createMissingStudio,
            markOrganized: metadataPreferences.markOrganized,
          }
        : {};

      const response = await system.startBatchDownload({
        items: selectedMatches.map((match, index) => {
          const normalizedUrl = match.normalizedUrl || sourceUrl;
          const itemTitle = getMatchTitle(match, title, entity, index);
          return {
            downloaderId: match.downloaderId,
            url: normalizedUrl,
            entity,
            qualityId: match.qualityOptions[0]?.id,
            sourceUrl: match.sourceUrl ?? undefined,
            label: itemTitle,
            title: itemTitle,
            createEntityIfMissing: true,
            autoApplyMetadata,
            ...metadataApplyOptions,
            groupIds: groupId ? [{ groupId, videoIndex: index }] : undefined,
          };
        }),
        followUp: {
          allowDuplicateDownloads,
        },
      });

      return {
        queuedCount: response.queuedCount,
        issues: response.issues ?? [],
        jobId: response.jobId ?? undefined,
      };
    },
    onSuccess: (result) => {
      window.alert(formatBatchDownloadSummary(entity.toLowerCase(), result));
      onQueued();
    },
  });

  if (!open) return null;

  const toggleIndex = (index: number) => {
    setSelectedIndexes((current) => {
      const next = new Set(current);
      next.has(index) ? next.delete(index) : next.add(index);
      return next;
    });
  };

  return (
    <div className="fixed inset-0 z-[110] flex items-center justify-center bg-black/70 p-4">
      <div className="flex max-h-[90vh] w-full max-w-3xl flex-col overflow-hidden rounded-2xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <Download className="h-5 w-5 text-accent" />
              Select Downloads
            </h2>
            <p className="mt-0.5 max-w-2xl truncate text-xs text-secondary">{sourceUrl}</p>
          </div>
          <button type="button" onClick={onClose} className="text-muted hover:text-foreground" aria-label="Close download dialog">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto px-5 py-4">
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-3">
              <label className="text-sm font-medium text-foreground">Items</label>
              <button
                type="button"
                onClick={() => setSelectedIndexes(selectedIndexes.size === matches.length ? new Set() : new Set(matches.map((_, index) => index)))}
                className="text-xs text-accent hover:text-accent-hover"
              >
                {selectedIndexes.size === matches.length ? "Clear" : "Select all"}
              </button>
            </div>
            <div className="space-y-2">
              {matches.map((match, index) => {
                const selected = selectedIndexes.has(index);
                return (
                  <button
                    key={`${match.downloaderId}:${match.normalizedUrl}:${index}`}
                    type="button"
                    onClick={() => toggleIndex(index)}
                    className={`flex w-full min-w-0 items-start gap-3 rounded-lg border px-3 py-3 text-left transition-colors ${
                      selected ? "border-accent bg-accent/10" : "border-border bg-card hover:border-accent/40"
                    }`}
                  >
                    <span className={`mt-0.5 flex h-5 w-5 flex-shrink-0 items-center justify-center rounded border ${selected ? "border-accent bg-accent text-white" : "border-border text-transparent"}`}>
                      <Check className="h-3.5 w-3.5" />
                    </span>
                    {getEntityIcon(entity)}
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium text-foreground">{getMatchTitle(match, resolvedBaseTitle, entity, index)}</span>
                      <span className="block text-xs text-secondary">{match.downloaderName}</span>
                      <span className="mt-1 block truncate text-xs text-muted">{match.normalizedUrl || sourceUrl}</span>
                    </span>
                  </button>
                );
              })}
            </div>
          </div>

          <div className="rounded-lg border border-border bg-card/50 px-3 py-2.5 text-sm text-foreground">
            <div className="grid gap-3 lg:grid-cols-[8rem_minmax(0,1fr)] lg:items-center">
              <div className="flex items-center gap-2 font-medium">
                <Layers3 className="h-4 w-4 text-muted" /> Group
              </div>
              <div className="inline-grid w-full grid-cols-3 gap-1 rounded-lg border border-border bg-surface p-1 sm:w-auto sm:min-w-[24rem]">
                <ContainerModeButton label="None" selected={groupMode === "none"} onClick={() => setGroupMode("none")} />
                <ContainerModeButton label="Existing" selected={groupMode === "existing"} onClick={() => setGroupMode("existing")} />
                <ContainerModeButton label="Create" selected={groupMode === "create"} onClick={() => setGroupMode("create")} />
              </div>

              {groupMode === "existing" ? (
                <div className="lg:col-start-2">
                  <EntityPicker
                    label="Existing group"
                    value={selectedGroupId}
                    search={groupSearch}
                    onSearchChange={setGroupSearch}
                    items={groupOptionsQuery.data?.items.map((group) => ({ id: group.id, label: group.name })) ?? []}
                    onSelect={setSelectedGroupId}
                  />
                </div>
              ) : null}

              {groupMode === "create" ? (
                <div className="grid gap-3 lg:col-start-2 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                  <label className="block text-sm font-medium text-foreground">
                    New group name
                    <input
                      value={containerTitle}
                      onChange={(event) => setContainerTitle(event.target.value)}
                      className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                    />
                  </label>
                  <EntityPicker
                    label="Parent group"
                    value={parentGroupId}
                    search={parentGroupSearch}
                    onSearchChange={setParentGroupSearch}
                    items={parentGroupOptionsQuery.data?.items.map((group) => ({ id: group.id, label: group.name })) ?? []}
                    onSelect={setParentGroupId}
                    allowNone
                  />
                </div>
              ) : null}
            </div>
          </div>

          <label className="flex items-start gap-3 rounded-lg border border-border bg-card/60 px-4 py-3 text-sm text-foreground">
            <input
              type="checkbox"
              checked={allowDuplicateDownloads}
              onChange={(event) => setAllowDuplicateDownloads(event.target.checked)}
              className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
            />
            <span className="font-medium">Allow duplicates</span>
          </label>

          {queueMutation.error ? (
            <div className="rounded-lg border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">
              {(queueMutation.error as Error).message || "Failed to queue downloads."}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-border px-5 py-4">
          <div className="text-xs text-muted">{selectedMatches.length} selected</div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={onClose} className="rounded-lg px-4 py-2 text-sm text-secondary hover:text-foreground">
              Cancel
            </button>
            <button
              type="button"
              onClick={() => queueMutation.mutate()}
              disabled={!hasSelection || queueMutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {queueMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
              Queue Selected
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function ContainerModeButton({ label, selected, onClick }: { label: string; selected: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md px-2.5 py-1.5 text-xs font-medium transition-colors ${selected ? "bg-card text-accent shadow-sm" : "text-secondary hover:bg-card hover:text-foreground"}`}
    >
      {label}
    </button>
  );
}

function EntityPicker({ label, value, search, onSearchChange, items, onSelect, allowNone = false }: { label: string; value: number | null; search: string; onSearchChange: (value: string) => void; items: { id: number; label: string }[]; onSelect: (id: number | null) => void; allowNone?: boolean }) {
  const selected = items.find((item) => item.id === value);
  return (
    <div className="space-y-2 text-sm">
      <label className="block font-medium text-foreground">
        {label}
        <input
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
          placeholder="Search"
          className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
        />
      </label>
      {selected ? <div className="text-xs text-secondary">Selected: {selected.label}</div> : null}
      <div className="max-h-32 overflow-y-auto rounded-lg border border-border bg-card">
        {allowNone ? (
          <button type="button" onClick={() => onSelect(null)} className={`block w-full px-3 py-2 text-left text-sm hover:bg-surface ${value == null ? "text-accent" : "text-foreground"}`}>
            None
          </button>
        ) : null}
        {items.map((item) => (
          <button key={item.id} type="button" onClick={() => onSelect(item.id)} className={`block w-full px-3 py-2 text-left text-sm hover:bg-surface ${value === item.id ? "text-accent" : "text-foreground"}`}>
            {item.label}
          </button>
        ))}
      </div>
    </div>
  );
}
