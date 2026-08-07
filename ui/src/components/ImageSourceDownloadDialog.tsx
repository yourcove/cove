import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Check, Download, FolderOpen, Image as ImageIcon, Layers3, Loader2, X } from "lucide-react";
import { galleries, groups, system } from "../api/client";
import type { DownloaderMatch, ImageCreate } from "../api/types";
import { formatBatchDownloadSummary, type BatchDownloadResult } from "../utils/batchDownloads";
import { loadScrapeApplyPreferences } from "./videoScrapeUtils";

type ContainerMode = "none" | "existing" | "create";

interface ImageSourceDownloadPreferences {
  galleryMode?: ContainerMode;
  groupMode?: ContainerMode;
  selectedGalleryId?: number | null;
  selectedGroupId?: number | null;
  parentGroupId?: number | null;
}

interface Props {
  open: boolean;
  sourceUrl: string;
  matches: DownloaderMatch[];
  baseTitle?: string;
  metadata?: ImageCreate;
  autoApplyMetadata?: boolean;
  onClose: () => void;
  onQueued: () => void;
}

function getMatchTitle(match: DownloaderMatch, fallback: string, index: number) {
  return match.label?.trim() || fallback || `Image ${index + 1}`;
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

const IMAGE_SOURCE_DOWNLOAD_PREFERENCES_KEY = "cove.image-source-download:containers";

function loadImageSourceDownloadPreferences(): ImageSourceDownloadPreferences {
  try {
    const parsed = JSON.parse(localStorage.getItem(IMAGE_SOURCE_DOWNLOAD_PREFERENCES_KEY) || "{}");
    return {
      galleryMode: parsed.galleryMode === "existing" || parsed.galleryMode === "create" ? parsed.galleryMode : "none",
      groupMode: parsed.groupMode === "existing" || parsed.groupMode === "create" ? parsed.groupMode : "none",
      selectedGalleryId: typeof parsed.selectedGalleryId === "number" ? parsed.selectedGalleryId : null,
      selectedGroupId: typeof parsed.selectedGroupId === "number" ? parsed.selectedGroupId : null,
      parentGroupId: typeof parsed.parentGroupId === "number" ? parsed.parentGroupId : null,
    };
  } catch {
    return { galleryMode: "none", groupMode: "none", selectedGalleryId: null, selectedGroupId: null, parentGroupId: null };
  }
}

function saveImageSourceDownloadPreferences(preferences: ImageSourceDownloadPreferences) {
  localStorage.setItem(IMAGE_SOURCE_DOWNLOAD_PREFERENCES_KEY, JSON.stringify(preferences));
}

export function ImageSourceDownloadDialog({ open, sourceUrl, matches, baseTitle, metadata, autoApplyMetadata = false, onClose, onQueued }: Props) {
  const [selectedIndexes, setSelectedIndexes] = useState<Set<number>>(new Set());
  const [galleryMode, setGalleryMode] = useState<ContainerMode>("none");
  const [groupMode, setGroupMode] = useState<ContainerMode>("none");
  const [selectedGalleryId, setSelectedGalleryId] = useState<number | null>(null);
  const [selectedGroupId, setSelectedGroupId] = useState<number | null>(null);
  const [parentGroupId, setParentGroupId] = useState<number | null>(null);
  const [gallerySearch, setGallerySearch] = useState("");
  const [groupSearch, setGroupSearch] = useState("");
  const [parentGroupSearch, setParentGroupSearch] = useState("");
  const [galleryTitle, setGalleryTitle] = useState("");
  const [groupTitle, setGroupTitle] = useState("");
  const [allowDuplicateDownloads, setAllowDuplicateDownloads] = useState(false);
  const resolvedBaseTitle = useMemo(() => getDefaultTitle(sourceUrl, baseTitle || metadata?.title), [baseTitle, metadata?.title, sourceUrl]);

  const galleryOptionsQuery = useQuery({
    queryKey: ["image-source-download-galleries", gallerySearch],
    enabled: open && galleryMode === "existing",
    queryFn: () => galleries.find({ page: 1, perPage: 20, sort: "title", direction: "asc", q: gallerySearch.trim() || undefined }),
  });

  const groupOptionsQuery = useQuery({
    queryKey: ["image-source-download-groups", groupSearch],
    enabled: open && groupMode === "existing",
    queryFn: () => groups.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: groupSearch.trim() || undefined }),
  });

  const parentGroupOptionsQuery = useQuery({
    queryKey: ["image-source-download-parent-groups", parentGroupSearch],
    enabled: open && (galleryMode === "create" || groupMode === "create"),
    queryFn: () => groups.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: parentGroupSearch.trim() || undefined }),
  });

  useEffect(() => {
    if (!open) return;
    const preferences = loadImageSourceDownloadPreferences();
    setSelectedIndexes(new Set(matches.map((_, index) => index)));
    setGalleryMode(preferences.galleryMode ?? "none");
    setGroupMode(preferences.groupMode ?? "none");
    setSelectedGalleryId(preferences.selectedGalleryId ?? null);
    setSelectedGroupId(preferences.selectedGroupId ?? null);
    setParentGroupId(preferences.parentGroupId ?? null);
    setGallerySearch("");
    setGroupSearch("");
    setParentGroupSearch("");
    setGalleryTitle(resolvedBaseTitle);
    setGroupTitle(resolvedBaseTitle);
    setAllowDuplicateDownloads(false);
  }, [matches, open, resolvedBaseTitle]);

  const selectedMatches = matches.filter((_, index) => selectedIndexes.has(index));
  const hasSelection = selectedMatches.length > 0;

  const queueMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (): Promise<BatchDownloadResult> => {
      if (!hasSelection) throw new Error("Select at least one image to download.");

      const fallbackItemTitle = galleryTitle.trim() || groupTitle.trim() || resolvedBaseTitle;
      const nextGalleryTitle = galleryTitle.trim() || fallbackItemTitle;
      const nextGroupTitle = groupTitle.trim() || fallbackItemTitle;
      const selectedGalleryIds: number[] = [];
      let groupId: number | undefined;

      if (galleryMode === "existing") {
        if (!selectedGalleryId) throw new Error("Select a gallery or choose no gallery.");
        selectedGalleryIds.push(selectedGalleryId);
      }

      if (galleryMode === "create") {
        const gallery = await galleries.create({
          title: nextGalleryTitle,
          organized: false,
          urls: [sourceUrl],
          tagIds: metadata?.tagIds,
          performerIds: metadata?.performerIds,
          studioId: metadata?.studioId,
          date: metadata?.date,
        });
        selectedGalleryIds.push(gallery.id);

        if (parentGroupId) {
          await groups.items.create(parentGroupId, { orderIndex: 0, kind: "gallery", hostType: "gallery", hostId: gallery.id, title: nextGalleryTitle });
        }
      }

      if (groupMode === "existing") {
        if (!selectedGroupId) throw new Error("Select a group or choose no group.");
        groupId = selectedGroupId;
      }

      if (groupMode === "create") {
        const group = await groups.create({
          name: nextGroupTitle,
          urls: [sourceUrl],
          allowedHostTypes: ["image"],
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

      saveImageSourceDownloadPreferences({ galleryMode, groupMode, selectedGalleryId, selectedGroupId, parentGroupId });

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
          const itemTitle = getMatchTitle(match, fallbackItemTitle, index);
          return {
            downloaderId: match.downloaderId,
            url: normalizedUrl,
            entity: "Image",
            qualityId: match.qualityOptions[0]?.id,
            sourceUrl: match.sourceUrl ?? undefined,
            label: itemTitle,
            title: itemTitle,
            createEntityIfMissing: true,
            autoApplyMetadata,
            ...metadataApplyOptions,
            galleryIds: selectedGalleryIds.length > 0 ? selectedGalleryIds : undefined,
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
      window.alert(formatBatchDownloadSummary("image", result));
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
                    <ImageIcon className="mt-0.5 h-4 w-4 flex-shrink-0 text-muted" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium text-foreground">{getMatchTitle(match, resolvedBaseTitle, index)}</span>
                      <span className="block text-xs text-secondary">{match.downloaderName}</span>
                      <span className="mt-1 block truncate text-xs text-muted">{match.normalizedUrl || sourceUrl}</span>
                    </span>
                  </button>
                );
              })}
            </div>
          </div>

          <div className="space-y-3 rounded-lg border border-border bg-card/50 px-3 py-2.5 text-sm text-foreground">
            <ContainerModeRow icon={<FolderOpen className="h-4 w-4 text-muted" />} label="Gallery" mode={galleryMode} onModeChange={setGalleryMode} />
            {galleryMode === "existing" ? (
              <div className="pl-0 lg:pl-[8.75rem]">
                <EntityPicker
                  label="Existing gallery"
                  value={selectedGalleryId}
                  search={gallerySearch}
                  onSearchChange={setGallerySearch}
                  items={galleryOptionsQuery.data?.items.map((gallery) => ({ id: gallery.id, label: gallery.title || `Gallery ${gallery.id}` })) ?? []}
                  onSelect={setSelectedGalleryId}
                />
              </div>
            ) : null}
            {galleryMode === "create" ? (
              <label className="block text-sm font-medium text-foreground lg:pl-[8.75rem]">
                Gallery name
                <input
                  value={galleryTitle}
                  onChange={(event) => setGalleryTitle(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                />
              </label>
            ) : null}

            <ContainerModeRow icon={<Layers3 className="h-4 w-4 text-muted" />} label="Group" mode={groupMode} onModeChange={setGroupMode} />
            {groupMode === "existing" ? (
              <div className="pl-0 lg:pl-[8.75rem]">
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
              <label className="block text-sm font-medium text-foreground lg:pl-[8.75rem]">
                Group name
                <input
                  value={groupTitle}
                  onChange={(event) => setGroupTitle(event.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                />
              </label>
            ) : null}

            {galleryMode === "create" || groupMode === "create" ? (
              <div className="pl-0 lg:pl-[8.75rem]">
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

function ContainerModeRow({ icon, label, mode, onModeChange }: { icon: ReactNode; label: string; mode: ContainerMode; onModeChange: (mode: ContainerMode) => void }) {
  return (
    <div className="grid gap-3 lg:grid-cols-[8rem_minmax(0,1fr)] lg:items-center">
      <div className="flex items-center gap-2 font-medium">{icon}{label}</div>
      <div className="inline-grid w-full grid-cols-3 gap-1 rounded-lg border border-border bg-surface p-1 sm:w-auto sm:min-w-[24rem]">
        <ContainerModeButton label="None" selected={mode === "none"} onClick={() => onModeChange("none")} />
        <ContainerModeButton label="Existing" selected={mode === "existing"} onClick={() => onModeChange("existing")} />
        <ContainerModeButton label="Create" selected={mode === "create"} onClick={() => onModeChange("create")} />
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
