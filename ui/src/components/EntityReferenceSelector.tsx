import { useMemo, useRef, useState } from "react";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { faces, galleries, groups, images, performers, videos, studios, tags } from "../api/client";
import type { CustomFieldType, Face, Gallery, Group, Image, Performer, Video, Studio, Tag, TagProvenance } from "../api/types";
import { TagProvenanceHover } from "./TagProvenanceHover";
import { TagActionMenu } from "./shared";
import { rankSearchOptions } from "../utils/searchRanking";
import { AutocompleteDropdown } from "./AutocompleteDropdown";

export type EntityReferenceType = Extract<CustomFieldType, "tag" | "performer" | "studio" | "video" | "gallery" | "image" | "group"> | "face";

export interface EntityReferenceOption {
  id: number;
  label: string;
  secondaryLabel?: string;
}

const REFERENCE_TYPES = new Set<string>(["tag", "performer", "studio", "video", "gallery", "image", "group", "face"]);

const ENTITY_LABELS: Record<EntityReferenceType, { singular: string; plural: string; sort: string }> = {
  tag: { singular: "tag", plural: "tags", sort: "name" },
  performer: { singular: "performer", plural: "performers", sort: "name" },
  face: { singular: "face", plural: "faces", sort: "label" },
  studio: { singular: "studio", plural: "studios", sort: "name" },
  video: { singular: "video", plural: "videos", sort: "title" },
  gallery: { singular: "gallery", plural: "galleries", sort: "title" },
  image: { singular: "image", plural: "images", sort: "title" },
  group: { singular: "group", plural: "groups", sort: "name" },
};

export function isEntityReferenceType(type: string | undefined): type is EntityReferenceType {
  return Boolean(type && REFERENCE_TYPES.has(type));
}

export function getEntityReferenceLabel(type: EntityReferenceType) {
  return ENTITY_LABELS[type];
}

export function parseEntityReferenceIds(value: unknown): number[] {
  const values = Array.isArray(value) ? value : value == null || value === "" ? [] : [value];
  const ids: number[] = [];

  for (const entry of values) {
    const id = parseEntityReferenceId(entry);
    if (id != null && !ids.includes(id)) {
      ids.push(id);
    }
  }

  return ids;
}

export function parseEntityReferenceId(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isInteger(value)) {
    return value;
  }

  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    return Number.isInteger(parsed) ? parsed : undefined;
  }

  if (value && typeof value === "object") {
    const candidate = value as { id?: unknown; value?: unknown };
    return parseEntityReferenceId(candidate.id ?? candidate.value);
  }

  return undefined;
}

export function EntityReferenceSelector({
  entityType,
  value,
  onChange,
  placeholder,
  disabled = false,
  inputClassName,
  excludeIds,
  selectedDisplay = "chip",
  selectedLabel,
}: {
  entityType: EntityReferenceType;
  value?: number;
  onChange: (value: number | undefined, option?: EntityReferenceOption) => void;
  placeholder?: string;
  disabled?: boolean;
  inputClassName?: string;
  excludeIds?: Iterable<number>;
  selectedDisplay?: "chip" | "input";
  selectedLabel?: string;
}) {
  const [searchText, setSearchText] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const trimmedSearch = searchText.trim();
  const labels = getEntityReferenceLabel(entityType);
  const queryClient = useQueryClient();

  const cachedOptions = useMemo(
    () => getCachedEntityReferenceOptions(queryClient, entityType),
    [entityType, queryClient],
  );
  const cachedSearchOptions = useMemo(() => {
    if (!trimmedSearch || cachedOptions == null) return undefined;

    const needle = trimmedSearch.toLowerCase();
    return rankSearchOptions(cachedOptions.filter((option) => option.label.toLowerCase().includes(needle)), trimmedSearch).slice(0, 25);
  }, [cachedOptions, trimmedSearch]);

  const { data: searchResults, isLoading, isFetching } = useQuery({
    queryKey: ["entity-reference-selector", entityType, trimmedSearch],
    queryFn: () => searchEntityReferences(entityType, trimmedSearch),
    enabled: !disabled && trimmedSearch.length >= 1 && cachedSearchOptions == null,
    staleTime: 60_000,
    placeholderData: (previousData) => previousData,
  });

  const searchOptions = useMemo(
    () => cachedSearchOptions ?? rankSearchOptions(searchResults ?? [], trimmedSearch).slice(0, 25),
    [cachedSearchOptions, searchResults, trimmedSearch],
  );
  const selectedSearchOption = searchOptions.find((option) => option.id === value);
  const { data: selectedOption, isLoading: selectedLoading } = useQuery({
    queryKey: ["entity-reference-selector", entityType, "selected", value],
    queryFn: () => getEntityReference(entityType, value as number),
    enabled: typeof value === "number" && !selectedSearchOption,
    staleTime: 60_000,
  });

  const selected = selectedSearchOption ?? selectedOption;
  const selectedInputLabel = selected?.label ?? selectedLabel?.trim() ?? (selectedLoading ? `Loading ${labels.singular}...` : "");
  const showSelectedInInput = selectedDisplay === "input" && typeof value === "number" && searchText === "" && selectedInputLabel !== "";
  const excluded = useMemo(() => new Set(excludeIds ?? []), [excludeIds]);
  const visibleResults = useMemo(
    () => searchOptions.filter((option) => option.id !== value && !excluded.has(option.id)),
    [excluded, searchOptions, value],
  );

  const creatableTypes: Partial<Record<EntityReferenceType, true>> = { tag: true, performer: true, group: true, studio: true, gallery: true };
  const exactMatchExists = useMemo(
    () => searchOptions.some((o) => o.label.toLowerCase() === trimmedSearch.toLowerCase()),
    [searchOptions, trimmedSearch],
  );

  const createMutation = useMutation({
    mutationFn: async (entityName: string) => {
      switch (entityType) {
        case "tag": return tags.create({ name: entityName });
        case "performer": return performers.create({ name: entityName });
        case "group": return groups.create({ name: entityName });
        case "studio": return studios.create({ name: entityName });
        case "gallery": return galleries.create({ title: entityName });
        default: throw new Error(`Cannot create entity of type ${entityType}`);
      }
    },
    onSuccess: (result, entityName) => {
      onChange(result.id, { id: result.id, label: entityName });
      setSearchText("");
      queryClient.invalidateQueries({ queryKey: [labels.plural] });
    },
  });

  const showCreateOption = trimmedSearch && !isFetching && creatableTypes[entityType] && !exactMatchExists;

  return (
    <div className="relative flex min-w-0 flex-col gap-2">
      {typeof value === "number" && selectedDisplay === "chip" ? (
        <div className="flex flex-wrap gap-1">
          <span className="inline-flex max-w-full items-center gap-1 rounded border border-border bg-card px-2 py-0.5 text-[10px] text-foreground">
            <span className="min-w-0 truncate">{selected?.label ?? (selectedLoading ? `Loading ${labels.singular}...` : `Unavailable ${labels.singular}`)}</span>
            {selected?.secondaryLabel ? <span className="text-muted">{selected.secondaryLabel}</span> : null}
            <button
              type="button"
              onClick={() => onChange(undefined)}
              className="hover:text-red-400"
              aria-label={`Clear selected ${labels.singular}`}
              disabled={disabled}
            >
              <X className="h-2.5 w-2.5" />
            </button>
          </span>
        </div>
      ) : null}

      <div className="relative">
        <input
          ref={inputRef}
          type="text"
          value={showSelectedInInput ? selectedInputLabel : searchText}
          onChange={(event) => setSearchText(event.target.value)}
          onFocus={(event) => {
            if (showSelectedInInput) {
              event.currentTarget.select();
            }
          }}
          placeholder={placeholder ?? `Search ${labels.plural}...`}
          disabled={disabled}
          className={inputClassName ?? "w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted disabled:opacity-50 focus:border-accent focus:outline-none"}
        />
        {selectedDisplay === "input" && typeof value === "number" ? (
          <button
            type="button"
            onClick={() => {
              setSearchText("");
              onChange(undefined);
            }}
            className="absolute right-2 top-1/2 -translate-y-1/2 rounded p-1 text-muted hover:text-foreground disabled:opacity-50"
            aria-label={`Clear selected ${labels.singular}`}
            disabled={disabled}
          >
            <X className="h-3 w-3" />
          </button>
        ) : null}
      </div>

      {trimmedSearch ? (
        <AutocompleteDropdown anchorRef={inputRef} className="rounded border border-border bg-surface">
          {isLoading ? <div className="px-3 py-2 text-sm text-muted">Loading...</div> : null}
          {!isLoading && visibleResults.length === 0 && !showCreateOption ? (
            <div className="px-3 py-2 text-sm text-muted">No {labels.plural} found</div>
          ) : null}
          {visibleResults.map((option) => (
            <button
              key={option.id}
              type="button"
              onClick={() => {
                onChange(option.id, option);
                setSearchText("");
              }}
              className="flex w-full min-w-0 items-center justify-between gap-2 px-3 py-2 text-left text-sm text-foreground hover:bg-card"
            >
              <span className="inline-flex min-w-0 items-center gap-2">
                <Plus className="h-3 w-3" />
                <span className="truncate">{option.label}</span>
              </span>
              {option.secondaryLabel ? <span className="shrink-0 text-xs text-muted">{option.secondaryLabel}</span> : null}
            </button>
          ))}
          {showCreateOption ? (
            <button
              type="button"
              onClick={() => createMutation.mutate(trimmedSearch)}
              disabled={createMutation.isPending}
              className="flex w-full items-center gap-2 border-t border-border px-3 py-2 text-left text-sm text-accent hover:bg-card disabled:opacity-50"
            >
              {createMutation.isPending ? (
                <span className="text-muted">Creating...</span>
              ) : (
                <>
                  <Plus className="h-3 w-3" />
                  <span>Create &ldquo;{trimmedSearch}&rdquo;</span>
                </>
              )}
            </button>
          ) : null}
        </AutocompleteDropdown>
      ) : null}
    </div>
  );
}

export function EntityReferenceMultiSelector({
  entityType,
  values,
  onChange,
  placeholder,
  emptyMessage,
  disabled = false,
  inputClassName,
  resultsClassName,
  resultsMaxHeight,
  containerClassName,
  excludeIds,
  lockedIds,
  selectedProvenanceById,
  reportableIds,
  onReportIncorrect,
  onAdjustThreshold,
}: {
  entityType: EntityReferenceType;
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  emptyMessage?: string;
  disabled?: boolean;
  inputClassName?: string;
  resultsClassName?: string;
  resultsMaxHeight?: number;
  containerClassName?: string;
  excludeIds?: Iterable<number>;
  lockedIds?: Iterable<number>;
  selectedProvenanceById?: Record<number, TagProvenance[] | undefined>;
  // Locked chips whose id is in reportableIds get the same "⋯" correction menu as the Details tab.
  reportableIds?: Iterable<number>;
  onReportIncorrect?: (id: number) => void;
  onAdjustThreshold?: (id: number) => void;
}) {
  const [searchText, setSearchText] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const trimmedSearch = searchText.trim();
  const labels = getEntityReferenceLabel(entityType);
  const queryClient = useQueryClient();

  const cachedOptions = useMemo(
    () => getCachedEntityReferenceOptions(queryClient, entityType),
    [entityType, queryClient],
  );
  const cachedSearchOptions = useMemo(() => {
    if (!trimmedSearch || cachedOptions == null) return undefined;

    const needle = trimmedSearch.toLowerCase();
    return rankSearchOptions(cachedOptions.filter((option) => option.label.toLowerCase().includes(needle)), trimmedSearch).slice(0, 25);
  }, [cachedOptions, trimmedSearch]);

  const { data: searchResults, isLoading, isFetching } = useQuery({
    queryKey: ["entity-reference-selector", entityType, trimmedSearch],
    queryFn: () => searchEntityReferences(entityType, trimmedSearch),
    enabled: !disabled && trimmedSearch.length >= 1 && cachedSearchOptions == null,
    staleTime: 60_000,
    placeholderData: (previousData) => previousData,
  });

  const searchOptions = useMemo(
    () => cachedSearchOptions ?? rankSearchOptions(searchResults ?? [], trimmedSearch).slice(0, 25),
    [cachedSearchOptions, searchResults, trimmedSearch],
  );
  const selectedOptions = useEntityReferenceOptions(entityType, values, searchOptions);
  const excluded = useMemo(() => new Set(excludeIds ?? []), [excludeIds]);
  const locked = useMemo(() => new Set(lockedIds ?? []), [lockedIds]);
  const reportable = useMemo(() => new Set(reportableIds ?? []), [reportableIds]);
  const visibleResults = useMemo(
    () => searchOptions.filter((option) => !values.includes(option.id) && !excluded.has(option.id)),
    [excluded, searchOptions, values],
  );

  const creatableTypes: Partial<Record<EntityReferenceType, true>> = { tag: true, performer: true, group: true, studio: true, gallery: true };
  const exactMatchExists = useMemo(
    () => searchOptions.some((o) => o.label.toLowerCase() === trimmedSearch.toLowerCase()),
    [searchOptions, trimmedSearch],
  );

  const createMutation = useMutation({
    mutationFn: async (name: string) => {
      switch (entityType) {
        case "tag": return tags.create({ name });
        case "performer": return performers.create({ name });
        case "group": return groups.create({ name });
        case "studio": return studios.create({ name });
        case "gallery": return galleries.create({ title: name });
        default: throw new Error(`Cannot create entity of type ${entityType}`);
      }
    },
    onSuccess: (result) => {
      onChange([...values, result.id]);
      setSearchText("");
      queryClient.invalidateQueries({ queryKey: [labels.plural] });
    },
  });

  const showCreateOption = trimmedSearch && !isFetching && creatableTypes[entityType] && !exactMatchExists;

  return (
    <div className={`relative ${containerClassName ?? "flex flex-col gap-2"}`}>
      {values.length > 0 ? (
        <div className="flex flex-wrap gap-1">
          {values.map((id) => {
            const option = selectedOptions.get(id);
            const lockedValue = locked.has(id);
            const chip = (
              <span key={id} className="inline-flex items-center gap-1 rounded border border-border bg-card px-2 py-0.5 text-[10px] text-foreground" title={lockedValue ? "Derived tag" : undefined}>
                <span>{option?.label ?? `Loading ${labels.singular}...`}</span>
                {option?.secondaryLabel ? <span className="text-muted">{option.secondaryLabel}</span> : null}
                {!lockedValue ? (
                  <button
                    type="button"
                    onClick={() => onChange(values.filter((value) => value !== id))}
                    className="hover:text-red-400"
                    aria-label={`Remove ${option?.label ?? labels.singular}`}
                    disabled={disabled}
                  >
                    <X className="h-2.5 w-2.5" />
                  </button>
                ) : reportable.has(id) ? (
                  <TagActionMenu
                    name={option?.label ?? labels.singular}
                    onReportIncorrect={onReportIncorrect ? () => onReportIncorrect(id) : undefined}
                    onAdjustThreshold={onAdjustThreshold ? () => onAdjustThreshold(id) : undefined}
                    triggerClassName="-my-0.5 -mr-1 inline-flex items-center rounded px-0.5 text-muted transition hover:text-foreground"
                    iconClassName="h-3 w-3"
                  />
                ) : null}
              </span>
            );
            const provenance = selectedProvenanceById?.[id];
            return provenance?.length ? <TagProvenanceHover key={id} provenance={provenance}>{chip}</TagProvenanceHover> : chip;
          })}
        </div>
      ) : null}

      <input
        ref={inputRef}
        type="text"
        value={searchText}
        onChange={(event) => setSearchText(event.target.value)}
        placeholder={placeholder ?? `Search ${labels.plural}...`}
        disabled={disabled}
        className={inputClassName ?? "w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted disabled:opacity-50 focus:border-accent focus:outline-none"}
      />

      {trimmedSearch ? (
        <AutocompleteDropdown anchorRef={inputRef} maxHeight={resultsMaxHeight} className={resultsClassName ?? "rounded border border-border bg-surface"}>
          {isLoading ? <div className="px-3 py-2 text-sm text-muted">Loading...</div> : null}
          {!isLoading && visibleResults.length === 0 && !showCreateOption ? (
            <div className="px-3 py-2 text-sm text-muted">{emptyMessage ?? `No ${labels.plural} found`}</div>
          ) : null}
          {visibleResults.map((option) => (
            <button
              key={option.id}
              type="button"
              onClick={() => {
                onChange([...values, option.id]);
                setSearchText("");
              }}
              className="flex w-full items-center justify-between gap-2 px-3 py-2 text-left text-sm text-foreground hover:bg-card"
            >
              <span className="inline-flex items-center gap-2">
                <Plus className="h-3 w-3" />
                <span>{option.label}</span>
              </span>
              {option.secondaryLabel ? <span className="text-xs text-muted">{option.secondaryLabel}</span> : null}
            </button>
          ))}
          {showCreateOption ? (
            <button
              type="button"
              onClick={() => createMutation.mutate(trimmedSearch)}
              disabled={createMutation.isPending}
              className="flex w-full items-center gap-2 border-t border-border px-3 py-2 text-left text-sm text-accent hover:bg-card disabled:opacity-50"
            >
              {createMutation.isPending ? (
                <span className="text-muted">Creating...</span>
              ) : (
                <>
                  <Plus className="h-3 w-3" />
                  <span>Create &ldquo;{trimmedSearch}&rdquo;</span>
                </>
              )}
            </button>
          ) : null}
        </AutocompleteDropdown>
      ) : null}
    </div>
  );
}

export function EntityReferenceValue({ entityType, value }: { entityType: EntityReferenceType; value: unknown }) {
  const ids = useMemo(() => parseEntityReferenceIds(value), [value]);
  const options = useEntityReferenceOptions(entityType, ids);
  const labels = getEntityReferenceLabel(entityType);

  if (ids.length === 0) {
    return null;
  }

  const text = ids
    .map((id) => options.get(id)?.label ?? `Loading ${labels.singular}...`)
    .filter(Boolean)
    .join(", ");

  return <>{text || `Unavailable ${labels.singular}`}</>;
}

function useEntityReferenceOptions(entityType: EntityReferenceType, ids: number[], seedOptions: EntityReferenceOption[] = []) {
  const missingIds = useMemo(
    () => ids.filter((id) => !seedOptions.some((option) => option.id === id)),
    [ids, seedOptions],
  );
  const selectedQueries = useQueries({
    queries: missingIds.map((id) => ({
      queryKey: ["entity-reference-selector", entityType, "selected", id],
      queryFn: () => getEntityReference(entityType, id),
      staleTime: 60_000,
    })),
  });

  return useMemo(() => {
    const optionMap = new Map<number, EntityReferenceOption>();
    for (const option of seedOptions) {
      optionMap.set(option.id, option);
    }

    for (const query of selectedQueries) {
      if (query.data) {
        optionMap.set(query.data.id, query.data);
      }
    }

    return optionMap;
  }, [seedOptions, selectedQueries]);
}

function getCachedEntityReferenceOptions(queryClient: ReturnType<typeof useQueryClient>, entityType: EntityReferenceType): EntityReferenceOption[] | undefined {
  const queryKey = [getEntityReferenceLabel(entityType).plural, "all"];
  const cached = queryClient.getQueryData<unknown>(queryKey);
  if (!Array.isArray(cached)) {
    return undefined;
  }

  switch (entityType) {
    case "tag":
      return cached.map((item) => toTagOption(item as Tag));
    case "performer":
      return cached.map((item) => toPerformerOption(item as Performer));
    case "face":
      return cached.map((item) => toFaceOption(item as Face));
    case "studio":
      return cached.map((item) => toStudioOption(item as Studio));
    case "video":
      return cached.map((item) => toVideoOption(item as Video));
    case "gallery":
      return cached.map((item) => toGalleryOption(item as Gallery));
    case "image":
      return cached.map((item) => toImageOption(item as Image));
    case "group":
      return cached.map((item) => toGroupOption(item as Group));
  }
}

async function searchEntityReferences(entityType: EntityReferenceType, searchText: string): Promise<EntityReferenceOption[]> {
  const query = searchText || undefined;
  const labels = getEntityReferenceLabel(entityType);
  const filter = { q: query, perPage: 100, sort: labels.sort, direction: "asc" as const };

  switch (entityType) {
    case "tag": return (await tags.find(filter)).items.map(toTagOption);
    case "performer": return (await performers.find(filter)).items.map(toPerformerOption);
    case "face": return (await faces.list(filter)).items.map(toFaceOption);
    case "studio": return (await studios.find(filter)).items.map(toStudioOption);
    case "video": return (await videos.find(filter)).items.map(toVideoOption);
    case "gallery": return (await galleries.find(filter)).items.map(toGalleryOption);
    case "image": return (await images.find(filter)).items.map(toImageOption);
    case "group": return (await groups.find(filter)).items.map(toGroupOption);
  }
}

async function getEntityReference(entityType: EntityReferenceType, id: number): Promise<EntityReferenceOption> {
  switch (entityType) {
    case "tag": return toTagOption(await tags.get(id));
    case "performer": return toPerformerOption(await performers.get(id));
    case "face": return toFaceOption(await faces.get(id));
    case "studio": return toStudioOption(await studios.get(id));
    case "video": return toVideoOption(await videos.get(id));
    case "gallery": return toGalleryOption(await galleries.get(id));
    case "image": return toImageOption(await images.get(id));
    case "group": return toGroupOption(await groups.get(id));
  }
}

function toTagOption(tag: Tag): EntityReferenceOption {
  return { id: tag.id, label: tag.name };
}

function toPerformerOption(performer: Performer): EntityReferenceOption {
  return {
    id: performer.id,
    label: performer.name,
    secondaryLabel: performer.disambiguation ? `(${performer.disambiguation})` : undefined,
  };
}

function toFaceOption(face: Face): EntityReferenceOption {
  return {
    id: face.id,
    label: face.label?.trim() || face.performerName?.trim() || `Face #${face.id}`,
    secondaryLabel: face.performerName && face.label?.trim() ? face.performerName : undefined,
  };
}

function toStudioOption(studio: Studio): EntityReferenceOption {
  return { id: studio.id, label: studio.name };
}

function toVideoOption(video: Video): EntityReferenceOption {
  const fileName = video.files?.[0]?.basename;
  return { id: video.id, label: video.title?.trim() || video.code?.trim() || fileName || "Untitled video" };
}

function toGalleryOption(gallery: Gallery): EntityReferenceOption {
  const fileName = gallery.files?.[0]?.path?.split(/[\\/]/).pop();
  return { id: gallery.id, label: gallery.title?.trim() || gallery.code?.trim() || fileName || "Untitled gallery" };
}

function toImageOption(image: Image): EntityReferenceOption {
  const fileName = image.files?.[0]?.basename;
  return { id: image.id, label: image.title?.trim() || image.code?.trim() || fileName || "Untitled image" };
}

function toGroupOption(group: Group): EntityReferenceOption {
  return { id: group.id, label: group.name };
}
