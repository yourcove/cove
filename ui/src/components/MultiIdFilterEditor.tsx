import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { ArrowLeft, Minus, Plus, Search, X } from "lucide-react";
import {
  audios as audiosApi,
  faces as facesApi,
  galleries as galleriesApi,
  groups as groupsApi,
  performers as performersApi,
  studios as studiosApi,
  tagGroups as tagGroupsApi,
  tags as tagsApi,
  videos as videosApi,
} from "../api/client";
import type { CriterionModifier, MultiIdCriterion } from "../api/types";
import { rankByLabel } from "../utils/searchRanking";
import { getMultiIdModifierLabel } from "../utils/filterModifierLabels";
import { GroupedTagOptionList, groupTagsForSelector } from "./TagSelector";
import { ConfirmDialog } from "./ConfirmDialog";
import { MODIFIER_LABELS } from "./filterEditorControls";
import { NULL_VALUE_MODIFIERS } from "./filterCriterionState";
import type { EntityType } from "./filterCriteriaTypes";

export function MultiIdEditor({ value, onChange, entityType, modifiers, hierarchyToggleLabel }: { value?: MultiIdCriterion; onChange: (v: unknown) => void; entityType: EntityType; modifiers: CriterionModifier[]; hierarchyToggleLabel?: string }) {
  const includeModifiers = modifiers.filter((modifier) => modifier === "INCLUDES" || modifier === "INCLUDES_ALL");
  const supportsExclude = modifiers.some((modifier) => modifier === "EXCLUDES" || modifier === "EXCLUDES_ALL");
  const modifier = value?.modifier ?? (includeModifiers.includes("INCLUDES_ALL") ? "INCLUDES_ALL" : "INCLUDES");
  const nullModifiers = modifiers.filter((item) => NULL_VALUE_MODIFIERS.has(item));
  const isNullModifier = NULL_VALUE_MODIFIERS.has(modifier);
  const includedIds = value?.value ?? [];
  const excludedIds = supportsExclude ? value?.excludes ?? [] : [];
  const includeHierarchy = (value as any)?.depth === -1;
  const existingNames: Record<string, string> = (value as any)?._names ?? {};
  const [searchText, setSearchText] = useState("");
  const [activeResultIndex, setActiveResultIndex] = useState(-1);
  const [pendingNullModifier, setPendingNullModifier] = useState<CriterionModifier | null>(null);
  const resultsRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const selectedButtonRefs = useRef(new Map<number, HTMLButtonElement>());
  const pendingSelectedRemovalRef = useRef<{ id: number | null; label: string } | null>(null);
  const [selectedValueFocusId, setSelectedValueFocusId] = useState<number | null>(() => includedIds[0] ?? excludedIds[0] ?? null);
  const [selectionAnnouncement, setSelectionAnnouncement] = useState("");
  const selectedValueInstructionsId = useId();
  const trimmedSearchText = searchText.trim();

  const { data: entities, isPlaceholderData, isFetching } = useQuery({
    queryKey: ["multi-id-selector", entityType, trimmedSearchText],
    queryFn: async () => {
      switch (entityType) {
        case "tags": return (await tagsApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" }, { includeCounts: false })).items;
        case "tagGroups": return await tagGroupsApi.list();
        case "performers": return (await performersApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "studios": return (await studiosApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "groups": return (await groupsApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "galleries": return (await galleriesApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "title", direction: "asc" })).items;
        case "videos": return (await videosApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "title", direction: "asc" })).items;
        case "audios": return (await audiosApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "title", direction: "asc" })).items;
        case "faces": return (await facesApi.list({ q: trimmedSearchText || undefined, merged: false, page: 1, perPage: 50 })).items;
        default: return [];
      }
    },
    // Video libraries make a remote title search noticeably slower; clear their unrelated initial
    // page while searching so a successful autocomplete does not look broken.
    placeholderData: entityType === "videos" && trimmedSearchText ? undefined : (previousData) => previousData,
    staleTime: 60000,
  });

  const selectedIds = useMemo(() => Array.from(new Set([...includedIds, ...excludedIds])), [excludedIds, includedIds]);
  const selectedIdsSignature = selectedIds.join(",");
  useEffect(() => {
    if (selectedValueFocusId != null && selectedIds.includes(selectedValueFocusId)) return;
    setSelectedValueFocusId(selectedIds[0] ?? null);
  }, [selectedIdsSignature, selectedValueFocusId]);
  useEffect(() => {
    const pending = pendingSelectedRemovalRef.current;
    if (!pending) return;
    pendingSelectedRemovalRef.current = null;
    setSelectionAnnouncement(`Removed ${pending.label}. ${selectedIds.length} selected.`);
    if (pending.id != null) selectedButtonRefs.current.get(pending.id)?.focus();
    else searchInputRef.current?.focus();
  }, [selectedIdsSignature]);
  const missingSelectedIds = useMemo(() => {
    const availableIds = new Set((entities as any[] | undefined)?.map((entity) => entity.id) ?? []);
    return selectedIds.filter((id) => !existingNames[String(id)] && !availableIds.has(id));
  }, [entities, existingNames, selectedIds]);
  const selectedEntityQueries = useQueries({
    queries: missingSelectedIds.map((id) => ({
      queryKey: ["multi-id-selector", entityType, "selected", id],
      queryFn: () => getMultiIdEntityLabel(entityType, id),
      staleTime: 60000,
    })),
  });

  // Build a name lookup from available entities
  const nameMap = useMemo(() => {
    const map: Record<string, string> = { ...existingNames };
    if (entities) for (const e of entities as any[]) map[String(e.id)] = e.name || e.title || getMultiIdEntityLabels(entityType).singular;
    for (const query of selectedEntityQueries) {
      if (query.data) map[String(query.data.id)] = query.data.label;
    }
    return map;
  }, [entities, existingNames, selectedEntityQueries]);

  const buildCriterion = (inc: number[], exc: number[], mod: string, includeChildren: boolean) => {
    // Include _names so filter chips can display entity names without waiting for queries
    const names: Record<string, string> = {};
    for (const id of [...inc, ...exc]) {
      if (nameMap[String(id)]) names[String(id)] = nameMap[String(id)];
    }
    return {
      value: inc,
      modifier: mod,
      excludes: exc.length > 0 ? exc : undefined,
      ...(includeChildren ? { depth: -1 } : {}),
      _names: Object.keys(names).length > 0 ? names : undefined,
    };
  };

  const filteredEntities = useMemo(() => {
    if (!entities) return [];
    const q = searchText.trim().toLowerCase();
    const available = entityType === "tagGroups" && q
      ? entities.filter((entity: any) => (entity.name || entity.title || "").toLowerCase().includes(q))
      : entities;
    return q ? rankByLabel(available as any[], searchText, (entity) => entity.name || entity.title || entity.label || entity.performerName || "") : available;
  }, [entities, entityType, searchText]);

  const navigableEntities = useMemo(() => {
    const visible = filteredEntities.slice(0, 50);
    if (entityType === "tags" && !trimmedSearchText) {
      return groupTagsForSelector(visible as any[]).flatMap((group) => group.tags);
    }
    return visible;
  }, [entityType, filteredEntities, trimmedSearchText]);

  useEffect(() => {
    setActiveResultIndex((current) => current >= navigableEntities.length ? -1 : current);
  }, [navigableEntities.length]);

  useEffect(() => {
    if (activeResultIndex < 0) return;
    const activeEntity = navigableEntities[activeResultIndex] as any;
    if (!activeEntity) return;
    resultsRef.current
      ?.querySelector<HTMLElement>(`#multi-id-result-${entityType}-${activeEntity.id}`)
      ?.scrollIntoView({ block: "nearest" });
  }, [activeResultIndex, entityType, navigableEntities]);

  const addInclude = (id: number) => {
    const nextInc = includedIds.includes(id) ? includedIds : [...includedIds, id];
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
    setSearchText("");
    setActiveResultIndex(-1);
  };

  const addExclude = (id: number) => {
    if (!supportsExclude) {
      return;
    }

    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.includes(id) ? excludedIds : [...excludedIds, id];
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
    setSearchText("");
    setActiveResultIndex(-1);
  };

  const removeId = (id: number) => {
    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
  };

  const labels = getMultiIdEntityLabels(entityType);
  const selectedCount = includedIds.length + excludedIds.length;
  const selectNullModifier = (nextModifier: CriterionModifier) => {
    if (selectedCount > 0) {
      setPendingNullModifier(nextModifier);
      return;
    }

    onChange({ modifier: nextModifier });
  };
  const matchModifiers = [
    ...(includeModifiers.length > 0
      ? [...includeModifiers].sort((a, b) => (a === "INCLUDES_ALL" ? -1 : b === "INCLUDES_ALL" ? 1 : 0))
      : (["INCLUDES"] as CriterionModifier[])),
    ...nullModifiers,
  ];
  const selectMatchModifier = (nextModifier: CriterionModifier) => {
    if (NULL_VALUE_MODIFIERS.has(nextModifier)) {
      selectNullModifier(nextModifier);
      return;
    }

    onChange(buildCriterion(includedIds, excludedIds, nextModifier, includeHierarchy));
  };
  const handleMatchModeKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const currentButton = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-match-modifier]");
    if (!currentButton) return;
    const currentModifier = currentButton.dataset.matchModifier as CriterionModifier;
    const currentIndex = matchModifiers.indexOf(currentModifier);
    if (currentIndex < 0) return;

    event.preventDefault();
    event.stopPropagation();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const nextModifier = matchModifiers[(currentIndex + direction + matchModifiers.length) % matchModifiers.length];
    event.currentTarget
      .querySelector<HTMLButtonElement>(`button[data-match-modifier="${nextModifier}"]`)
      ?.focus();
    selectMatchModifier(nextModifier);
  };
  const getName = (e: any) => e.name || e.title || e.label || e.performerName || labels.singular;
  const getSelectedName = (id: number, entity?: any) => {
    if (entity) return getName(entity);
    const hydratedName = nameMap[String(id)];
    if (hydratedName) return hydratedName;
    const missingIndex = missingSelectedIds.indexOf(id);
    if (missingIndex >= 0 && selectedEntityQueries[missingIndex]?.isError) return `Unavailable ${labels.singular}`;
    return `Loading ${labels.singular}...`;
  };
  const focusSelectedValue = (id: number) => {
    setSelectedValueFocusId(id);
    selectedButtonRefs.current.get(id)?.focus();
  };
  const removeSelectedValue = (id: number) => {
    const index = selectedIds.indexOf(id);
    const nextId = selectedIds[index + 1] ?? selectedIds[index - 1] ?? null;
    pendingSelectedRemovalRef.current = { id: nextId, label: getSelectedName(id) };
    setSelectedValueFocusId(nextId);
    removeId(id);
  };
  const handleSelectedValueKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, id: number) => {
    const currentIndex = selectedIds.indexOf(id);
    if (currentIndex < 0) return;
    if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
      event.preventDefault();
      const direction = event.key === "ArrowRight" ? 1 : -1;
      focusSelectedValue(selectedIds[(currentIndex + direction + selectedIds.length) % selectedIds.length]);
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      focusSelectedValue(event.key === "Home" ? selectedIds[0] : selectedIds[selectedIds.length - 1]);
    } else if (event.key === "Delete" || event.key === "Backspace") {
      event.preventDefault();
      removeSelectedValue(id);
    }
  };

  return (
    <div className="flex min-h-full flex-1 flex-col gap-2">
      <div className="space-y-2">
        <div className="text-sm font-medium text-secondary">Match</div>
        <div className="flex flex-col gap-2 md:flex-row md:items-center">
          <div className="flex flex-wrap gap-2" role="group" aria-label="Match mode" onKeyDown={handleMatchModeKeyDown}>
            {matchModifiers.map((m) => (
              <button
                type="button"
                key={m}
                data-match-modifier={m}
                onClick={() => selectMatchModifier(m)}
                aria-pressed={m === modifier}
                tabIndex={m === modifier ? 0 : -1}
                className={`min-h-9 flex-1 whitespace-nowrap rounded-lg border px-3 py-1.5 text-sm md:flex-none ${
                  m === modifier
                    ? "border-accent bg-accent text-white"
                    : "border-border text-secondary hover:border-accent/50 hover:text-foreground"
                }`}
              >
                {getMultiIdModifierLabel(m, entityType, MODIFIER_LABELS[m])}
              </button>
            ))}
          </div>
          {!isNullModifier && (entityType === "tags" || hierarchyToggleLabel) && (
            <label className="flex min-h-9 cursor-pointer select-none items-center gap-2 text-sm text-secondary md:ml-auto">
              <input
                type="checkbox"
                checked={includeHierarchy}
                onChange={(e) => {
                  onChange(buildCriterion(includedIds, excludedIds, modifier, e.target.checked));
                }}
                className="h-5 w-5 accent-accent"
              />
              {hierarchyToggleLabel ?? "Include sub-tags"}
            </label>
          )}
        </div>
      </div>
      {isNullModifier ? (
        <div className="rounded border border-border/70 bg-input px-2 py-2 text-xs text-muted">
          This criterion will match entities with {modifier === "IS_NULL" ? "no" : "at least one"} linked {entityType} item.
        </div>
      ) : (
        <>
      {/* Search + add */}
      <div className="relative">
        <input
          ref={searchInputRef}
          type="text"
          role="combobox"
          aria-label={`Search ${entityType}`}
          aria-autocomplete="list"
          aria-controls={`multi-id-results-${entityType}`}
          aria-expanded={!isNullModifier}
          aria-activedescendant={activeResultIndex >= 0 ? `multi-id-result-${entityType}-${navigableEntities[activeResultIndex]?.id}` : undefined}
          data-filter-primary-control
          value={searchText}
          onChange={(e) => {
            setSearchText(e.target.value);
            setActiveResultIndex(-1);
          }}
          onKeyDown={(event) => {
            if (isPlaceholderData && (event.key === "ArrowDown" || event.key === "ArrowUp" || (event.key === "Enter" && !event.ctrlKey && !event.metaKey))) {
              event.preventDefault();
              return;
            }
            if (event.key === "ArrowDown" && navigableEntities.length > 0) {
              event.preventDefault();
              setActiveResultIndex((current) => Math.min(navigableEntities.length - 1, current + 1));
            } else if (event.key === "ArrowUp" && navigableEntities.length > 0) {
              event.preventDefault();
              setActiveResultIndex((current) => current <= 0 ? 0 : current - 1);
            } else if (event.key === "Enter" && !event.ctrlKey && !event.metaKey && navigableEntities.length > 0) {
              event.preventDefault();
              const entity = navigableEntities[activeResultIndex >= 0 ? activeResultIndex : 0] as any;
              if (event.shiftKey && supportsExclude) addExclude(entity.id);
              else addInclude(entity.id);
            }
          }}
          placeholder={`Search ${entityType}...`}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
        />
      </div>
      <p className="hidden text-xs text-muted md:block">Use ↑/↓ to choose, Enter to include, Shift+Enter to exclude. Ctrl/⌘+Enter applies.</p>
      <div ref={resultsRef} id={`multi-id-results-${entityType}`} role="listbox" aria-label={`${labels.plural} results`} tabIndex={-1} className="max-h-64 shrink-0 overflow-y-auto rounded-lg border border-border bg-input" aria-busy={(isPlaceholderData || isFetching) || undefined}>
        {!entities && trimmedSearchText && (
          <div className="px-2 py-2 text-xs text-muted text-center">Searching…</div>
        )}
        {entityType === "tags" ? (
          <GroupedTagOptionList
            tags={navigableEntities as any[]}
            maxItems={50}
            className="border-0 bg-transparent"
            preserveOrder={Boolean(searchText.trim())}
            groupToggleTabIndex={-1}
            groupHeadersInteractive={false}
            renderTag={(entity: any) => {
              const isIncluded = includedIds.includes(entity.id);
              const isExcluded = excludedIds.includes(entity.id);
              return (
                <div
                  id={`multi-id-result-${entityType}-${entity.id}`}
                  role="option"
                  aria-label={getName(entity)}
                  aria-selected={isIncluded || isExcluded}
                  aria-disabled={isPlaceholderData || undefined}
                  onClick={() => { if (!isPlaceholderData) isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                  className={`flex min-h-11 w-full items-center gap-1 px-1 text-sm ${isPlaceholderData ? "cursor-wait" : "cursor-pointer"} ${activeResultIndex >= 0 && navigableEntities[activeResultIndex]?.id === entity.id ? "bg-accent/15 ring-1 ring-inset ring-accent" : ""} ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
                >
                  <button
                    type="button"
                    tabIndex={-1}
                    onClick={(event) => { event.stopPropagation(); isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                    className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-green-500/10 hover:text-green-400 disabled:cursor-wait ${isIncluded ? "text-green-400" : "text-muted"}`}
                    title="Include"
                    aria-label={`Include ${getName(entity)}`}
                    disabled={isPlaceholderData}
                  >
                    <Plus className="w-3 h-3" />
                  </button>
                  <span className="min-w-0 flex-1 truncate">{getName(entity)}</span>
                  {supportsExclude && (
                    <button
                      type="button"
                      tabIndex={-1}
                      onClick={(event) => { event.stopPropagation(); isExcluded ? removeId(entity.id) : addExclude(entity.id); }}
                      className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-red-500/10 hover:text-red-400 disabled:cursor-wait ${isExcluded ? "text-red-400" : "text-muted"}`}
                      title="Exclude"
                      aria-label={`Exclude ${getName(entity)}`}
                      disabled={isPlaceholderData}
                    >
                      <Minus className="w-3 h-3" />
                    </button>
                  )}
                </div>
              );
            }}
          />
        ) : navigableEntities.map((entity: any) => {
          const isIncluded = includedIds.includes(entity.id);
          const isExcluded = excludedIds.includes(entity.id);
          return (
            <div
              key={entity.id}
              id={`multi-id-result-${entityType}-${entity.id}`}
              role="option"
              aria-label={getName(entity)}
              aria-selected={isIncluded || isExcluded}
              aria-disabled={isPlaceholderData || undefined}
              onClick={() => { if (!isPlaceholderData) isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
              className={`flex min-h-11 w-full items-center gap-1 px-1 text-sm ${isPlaceholderData ? "cursor-wait" : "cursor-pointer"} ${activeResultIndex >= 0 && navigableEntities[activeResultIndex]?.id === entity.id ? "bg-accent/15 ring-1 ring-inset ring-accent" : ""} ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
            >
              <button
                type="button"
                tabIndex={-1}
                onClick={(event) => { event.stopPropagation(); isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-green-500/10 hover:text-green-400 disabled:cursor-wait ${isIncluded ? "text-green-400" : "text-muted"}`}
                title="Include"
                aria-label={`Include ${getName(entity)}`}
                disabled={isPlaceholderData}
              >
                <Plus className="w-3 h-3" />
              </button>
              <span className="min-w-0 flex-1 truncate">{getName(entity)}</span>
              {supportsExclude && (
                <button
                  type="button"
                  tabIndex={-1}
                  onClick={(event) => { event.stopPropagation(); isExcluded ? removeId(entity.id) : addExclude(entity.id); }}
                  className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-red-500/10 hover:text-red-400 disabled:cursor-wait ${isExcluded ? "text-red-400" : "text-muted"}`}
                  title="Exclude"
                  aria-label={`Exclude ${getName(entity)}`}
                  disabled={isPlaceholderData}
                >
                  <Minus className="w-3 h-3" />
                </button>
              )}
            </div>
          );
        })}
        {entities && filteredEntities.length === 0 && (
          <div className="px-2 py-2 text-xs text-muted text-center">No results</div>
        )}
      </div>
      {/* Selected items: included */}
      {includedIds.length > 0 && (
        <div className="flex flex-wrap gap-1" role="group" aria-label={`Included ${labels.plural}`}>
          {includedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex h-8 items-center overflow-hidden rounded-md border border-green-700 bg-green-900/50 pl-2 text-sm text-green-300 focus-within:ring-2 focus-within:ring-accent">
                <span className="max-w-56 truncate">{getSelectedName(id, entity)}</span>
                <button
                  type="button"
                  ref={(element) => {
                    if (element) selectedButtonRefs.current.set(id, element);
                    else selectedButtonRefs.current.delete(id);
                  }}
                  onClick={() => removeSelectedValue(id)}
                  onFocus={() => setSelectedValueFocusId(id)}
                  onKeyDown={(event) => handleSelectedValueKeyDown(event, id)}
                  tabIndex={selectedValueFocusId === id || (selectedValueFocusId == null && id === selectedIds[0]) ? 0 : -1}
                  className="ml-2 inline-flex h-8 w-8 items-center justify-center border-l border-green-700 hover:bg-red-500/10 hover:text-red-300 focus:outline-none"
                  aria-label={`Remove ${getSelectedName(id, entity)}`}
                  aria-describedby={selectedValueInstructionsId}
                  aria-keyshortcuts="ArrowLeft ArrowRight Home End Delete Backspace"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Selected items: excluded */}
      {supportsExclude && excludedIds.length > 0 && (
        <div className="flex flex-wrap gap-1" role="group" aria-label={`Excluded ${labels.plural}`}>
          {excludedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex h-8 items-center overflow-hidden rounded-md border border-red-700 bg-red-900/50 pl-2 text-sm text-red-300 focus-within:ring-2 focus-within:ring-accent">
                <span className="max-w-56 truncate">{getSelectedName(id, entity)}</span>
                <button
                  type="button"
                  ref={(element) => {
                    if (element) selectedButtonRefs.current.set(id, element);
                    else selectedButtonRefs.current.delete(id);
                  }}
                  onClick={() => removeSelectedValue(id)}
                  onFocus={() => setSelectedValueFocusId(id)}
                  onKeyDown={(event) => handleSelectedValueKeyDown(event, id)}
                  tabIndex={selectedValueFocusId === id || (selectedValueFocusId == null && id === selectedIds[0]) ? 0 : -1}
                  className="ml-2 inline-flex h-8 w-8 items-center justify-center border-l border-red-700 hover:bg-red-500/10 hover:text-red-200 focus:outline-none"
                  aria-label={`Remove ${getSelectedName(id, entity)}`}
                  aria-describedby={selectedValueInstructionsId}
                  aria-keyshortcuts="ArrowLeft ArrowRight Home End Delete Backspace"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {selectedIds.length > 0 ? (
        <p id={selectedValueInstructionsId} className="text-xs text-muted">Selected {labels.plural}: use ←/→ to review; Delete removes.</p>
      ) : null}
      <span className="sr-only" role="status" aria-live="polite">{selectionAnnouncement}</span>
        </>
      )}
      <ConfirmDialog
        open={pendingNullModifier !== null}
        title={`Clear selected ${labels.plural}?`}
        message={`Switching to ${pendingNullModifier ? getMultiIdModifierLabel(pendingNullModifier, entityType, MODIFIER_LABELS[pendingNullModifier]) : "this match mode"} will clear ${selectedCount} selected ${selectedCount === 1 ? labels.singular : labels.plural}.`}
        confirmLabel="Clear selection"
        onConfirm={() => {
          if (pendingNullModifier) {
            onChange({ modifier: pendingNullModifier });
          }
          setPendingNullModifier(null);
        }}
        onCancel={() => setPendingNullModifier(null)}
      />
    </div>
  );
}

const MULTI_ID_ENTITY_LABELS: Record<EntityType, { singular: string; plural: string }> = {
  tags: { singular: "tag", plural: "tags" },
  tagGroups: { singular: "tag group", plural: "tag groups" },
  performers: { singular: "performer", plural: "performers" },
  studios: { singular: "studio", plural: "studios" },
  groups: { singular: "group", plural: "groups" },
  galleries: { singular: "gallery", plural: "galleries" },
  videos: { singular: "video", plural: "videos" },
  audios: { singular: "audio", plural: "audios" },
  faces: { singular: "face", plural: "faces" },
};

function getMultiIdEntityLabels(entityType: EntityType) {
  return MULTI_ID_ENTITY_LABELS[entityType];
}

async function getMultiIdEntityLabel(entityType: EntityType, id: number): Promise<{ id: number; label: string }> {
  switch (entityType) {
    case "tags": {
      const tag = await tagsApi.get(id);
      return { id, label: tag.name };
    }
    case "tagGroups": {
      const tagGroup = await tagGroupsApi.get(id);
      return { id, label: tagGroup.name };
    }
    case "performers": {
      const performer = await performersApi.get(id);
      return { id, label: performer.name };
    }
    case "studios": {
      const studio = await studiosApi.get(id);
      return { id, label: studio.name };
    }
    case "groups": {
      const group = await groupsApi.get(id);
      return { id, label: group.name };
    }
    case "galleries": {
      const gallery = await galleriesApi.get(id);
      return { id, label: gallery.title?.trim() || "Untitled gallery" };
    }
    case "videos": {
      const video = await videosApi.get(id);
      return { id, label: video.title?.trim() || video.code?.trim() || video.files?.[0]?.basename || "Untitled video" };
    }
    case "audios": {
      const audio = await audiosApi.get(id);
      return { id, label: audio.title?.trim() || audio.files?.[0]?.basename || "Untitled audio" };
    }
    case "faces": {
      const face = await facesApi.get(id);
      return { id, label: face.label?.trim() || face.performerName?.trim() || `Face #${id}` };
    }
  }
}
