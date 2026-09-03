import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ComponentType,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Film, Search, Users } from "lucide-react";
import { savedFilters as savedFiltersApi } from "../api/client";
import type { RelatedFilterCriterion } from "../api/types";
import type { RelatedFilterChipFacet } from "./ActiveObjectFilterChips";
import { getRelatedCriteria } from "./filterCriteriaCatalogs";
import {
  getCriterionFilterValue,
  isCriterionValueValid,
  removeCriterionFilterValue,
  setCriterionFilterValue,
} from "./filterCriterionState";
import { LabeledControl } from "./filterEditorControls";
import { getFirstEditorControl } from "./filterEditorFocus";
import type { CriterionDefinition } from "./filterCriteriaTypes";

export interface RelatedCriterionEditorProps {
  criterion: CriterionDefinition;
  value: unknown;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
  onChange: (value: unknown) => void;
}

export function parseSavedFilterObject(value: string | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

export function RelatedFilterWorkspace({
  criterion,
  value,
  onChange,
  selection,
  onSelectionChange,
  CriterionEditorComponent,
}: {
  criterion: CriterionDefinition;
  value?: RelatedFilterCriterion;
  onChange: (v: unknown) => void;
  selection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null;
  onSelectionChange: (selection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null) => void;
  CriterionEditorComponent: ComponentType<RelatedCriterionEditorProps>;
}) {
  const entityType = criterion.entityType!;
  const singular = entityType === "performers" ? "performer" : "video";
  const plural = entityType === "performers" ? "performers" : "videos";
  const resultPlural = entityType === "performers" ? "videos" : "performers";
  const relativePronoun = entityType === "performers" ? "who" : "that";
  const EntityIcon = entityType === "performers" ? Users : Film;
  const nestedCriteria = useMemo(() => [
    ...(criterion.relatedContextCriteria ?? []),
    ...(criterion.relatedCriteria?.() ?? getRelatedCriteria(entityType)),
  ], [criterion, entityType]);
  const [criteriaSearch, setCriteriaSearch] = useState("");
  const [navigatorFocusKey, setNavigatorFocusKey] = useState<string | null>(null);
  const workspaceRef = useRef<HTMLDivElement>(null);
  const criteriaSearchRef = useRef<HTMLInputElement>(null);
  const criterionButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const relationshipModeRef = useRef<HTMLSelectElement>(null);
  const matchAnyRef = useRef<HTMLButtonElement>(null);
  const initialSelectionRef = useRef(selection);
  const related = value ?? {};
  const relationshipMode = related.mode ?? (related.exclude ? "none" : "atLeastOne");
  const conditionOperator = related.conditionOperator === "or" ? "or" : "and";
  const objectFilter = related.objectFilter && typeof related.objectFilter === "object"
    ? related.objectFilter as Record<string, unknown>
    : {};
  const selectedCriterion = selection?.facet === "criterion"
    ? nestedCriteria.find((candidate) => candidate.id === selection.nestedCriterionId)
    : undefined;
  const editingSearch = selection?.facet === "search";
  const editingExistence = selection?.facet === "existence";
  const hasEditor = Boolean(selectedCriterion || editingSearch || editingExistence);
  const { data: savedFilters = [], isPending: savedFiltersPending, isError: savedFiltersError } = useQuery({
    queryKey: ["saved-filters", entityType],
    queryFn: () => savedFiltersApi.list(entityType),
  });
  const selectedSavedFilterId = savedFilters.find((savedFilter) => savedFilter.name === related._savedFilterName)?.id ?? "";

  useEffect(() => {
    const initialSelection = initialSelectionRef.current;
    if (initialSelection?.facet === "criterion" || initialSelection?.facet === "search") return;
    const timeout = window.setTimeout(() => {
      if (initialSelection?.facet === "mode") relationshipModeRef.current?.focus();
      else if (initialSelection?.facet === "existence") matchAnyRef.current?.focus();
      else criteriaSearchRef.current?.focus();
    }, 0);
    return () => window.clearTimeout(timeout);
  }, []);

  const update = (patch: Partial<RelatedFilterCriterion>, clearSavedFilterName = false) => {
    const next: Record<string, unknown> = { ...related, ...patch };
    if (clearSavedFilterName) delete next._savedFilterName;
    for (const [key, item] of Object.entries(next)) {
      if (item === undefined) delete next[key];
    }
    onChange(next);
  };

  const select = (nextSelection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string }) => {
    onSelectionChange(nextSelection);
    window.setTimeout(() => {
      const panel = workspaceRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstEditorControl(panel)?.focus();
    }, 0);
  };

  const updateNestedCriterion = (nestedCriterion: CriterionDefinition, nextValue: unknown) => {
    if (criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)) {
      update({ [nestedCriterion.filterKey]: nextValue, _matchAll: undefined } as Partial<RelatedFilterCriterion>, true);
      return;
    }
    update({
      objectFilter: setCriterionFilterValue(objectFilter, nestedCriterion, nextValue),
      _matchAll: undefined,
    }, true);
  };

  const removeNestedCriterion = (nestedCriterion: CriterionDefinition) => {
    if (criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)) {
      update({ [nestedCriterion.filterKey]: undefined } as Partial<RelatedFilterCriterion>, true);
      return;
    }
    update({ objectFilter: removeCriterionFilterValue(objectFilter, nestedCriterion) }, true);
  };

  const getNestedValue = (nestedCriterion: CriterionDefinition) => criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)
    ? (related as Record<string, unknown>)[nestedCriterion.filterKey]
    : getCriterionFilterValue(objectFilter, nestedCriterion);

  const chooseSavedFilter = (id: string) => {
    const savedFilter = savedFilters.find((candidate) => String(candidate.id) === id);
    if (!savedFilter) return;
    const findFilter = parseSavedFilterObject(savedFilter.findFilter);
    const savedObjectFilter = parseSavedFilterObject(savedFilter.objectFilter);
    const q = typeof findFilter.q === "string" ? findFilter.q.trim() : "";
    const hasObjectFilter = Object.keys(savedObjectFilter).length > 0;
    onChange({
      ...(q ? { findFilter: { q } } : {}),
      ...(hasObjectFilter ? { objectFilter: savedObjectFilter } : {}),
      ...(related.mode ? { mode: related.mode } : {}),
      ...(related.conditionOperator ? { conditionOperator: related.conditionOperator } : {}),
      ...(related.exclude ? { exclude: true } : {}),
      _savedFilterName: savedFilter.name,
      ...(!q && !hasObjectFilter ? { _matchAll: true } : {}),
    });
    onSelectionChange(null);
  };

  const toggleMatchAll = () => {
    if (related._matchAll) update({ _matchAll: undefined });
    else onChange({
      ...(related.mode ? { mode: related.mode } : {}),
      ...(related.conditionOperator ? { conditionOperator: related.conditionOperator } : {}),
      ...(related.exclude ? { exclude: true } : {}),
      _matchAll: true,
    });
  };

  const filteredCriteria = useMemo(() => {
    const query = criteriaSearch.trim().toLowerCase();
    return nestedCriteria
      .filter((candidate) => !query || candidate.label.toLowerCase().includes(query))
      .slice()
      .sort((left, right) => left.label.localeCompare(right.label));
  }, [criteriaSearch, nestedCriteria]);
  const activeCriteria = filteredCriteria.filter((candidate) => isCriterionValueValid(getNestedValue(candidate), candidate));
  const inactiveCriteria = filteredCriteria.filter((candidate) => !activeCriteria.includes(candidate));
  const activeConditionCount = nestedCriteria.filter((candidate) => isCriterionValueValid(getNestedValue(candidate), candidate)).length
    + (related.findFilter?.q?.trim() ? 1 : 0);
  const showTextSearch = !criteriaSearch.trim() || "text search".includes(criteriaSearch.trim().toLowerCase());
  const visibleNavigatorKeys = [
    ...(showTextSearch ? ["search"] : []),
    ...activeCriteria.map((candidate) => candidate.id),
    ...inactiveCriteria.map((candidate) => candidate.id),
  ];
  const selectedNavigatorKey = editingSearch ? "search" : selectedCriterion?.id;
  const rovingNavigatorKey = navigatorFocusKey && visibleNavigatorKeys.includes(navigatorFocusKey)
    ? navigatorFocusKey
    : selectedNavigatorKey && visibleNavigatorKeys.includes(selectedNavigatorKey)
    ? selectedNavigatorKey
    : visibleNavigatorKeys[0];
  const handleNavigatorKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, key: string) => {
    const index = visibleNavigatorKeys.indexOf(key);
    if (event.key === "ArrowUp" && index === 0) {
      event.preventDefault();
      criteriaSearchRef.current?.focus();
      return;
    }
    let nextIndex: number | undefined;
    if (event.key === "ArrowDown") nextIndex = Math.min(visibleNavigatorKeys.length - 1, index + 1);
    if (event.key === "ArrowUp") nextIndex = Math.max(0, index - 1);
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = visibleNavigatorKeys.length - 1;
    if (nextIndex === undefined || nextIndex < 0) return;
    event.preventDefault();
    criterionButtonRefs.current.get(visibleNavigatorKeys[nextIndex])?.focus();
  };

  const renderCriterionRow = (nestedCriterion: CriterionDefinition) => {
    const active = isCriterionValueValid(getNestedValue(nestedCriterion), nestedCriterion);
    const selected = selectedCriterion?.id === nestedCriterion.id;
    return (
      <button
        ref={(element) => { if (element) criterionButtonRefs.current.set(nestedCriterion.id, element); else criterionButtonRefs.current.delete(nestedCriterion.id); }}
        key={nestedCriterion.id}
        type="button"
        role="tab"
        aria-selected={selected}
        data-active={active ? "true" : "false"}
        tabIndex={nestedCriterion.id === rovingNavigatorKey ? 0 : -1}
        onClick={() => select({ facet: "criterion", nestedCriterionId: nestedCriterion.id })}
        onFocus={() => setNavigatorFocusKey(nestedCriterion.id)}
        onKeyDown={(event) => handleNavigatorKeyDown(event, nestedCriterion.id)}
        className={`flex min-h-11 w-full items-center gap-3 rounded-lg border px-3 py-2 text-left text-sm transition ${selected ? "border-accent bg-accent/15 text-foreground" : active ? "border-accent/30 bg-accent/5 text-foreground hover:bg-card" : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground"}`}
      >
        <span className="min-w-0 flex-1 truncate font-medium">{nestedCriterion.label}</span>
        {active ? <span className="h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden="true" /> : null}
      </button>
    );
  };

  return (
    <div ref={workspaceRef} className="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex flex-col gap-3 border-b border-border px-4 py-3 md:flex-row md:items-center md:justify-between md:px-6">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">Relationship</h3>
          <p className="text-xs text-muted">{conditionOperator === "or" ? "At least one condition below" : "All conditions below"} must match the same related {singular}.</p>
        </div>
        <div className="flex flex-col gap-2 sm:flex-row">
          <select
            ref={relationshipModeRef}
            aria-label="Relationship match mode"
            value={relationshipMode}
            onChange={(event) => {
              const mode = event.target.value as RelatedFilterCriterion["mode"];
              update({ mode: mode === "atLeastOne" ? undefined : mode, exclude: undefined });
            }}
            className="min-h-11 shrink-0 rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          >
            <option value="atLeastOne">At least one matching {singular}</option>
            <option value="every">Every {singular} matches</option>
            <option value="none">No {singular} matches</option>
          </select>
          {activeConditionCount >= 2 || conditionOperator === "or" ? (
            <select
              aria-label="Related condition operator"
              value={conditionOperator}
              onChange={(event) => update({ conditionOperator: event.target.value === "or" ? "or" : undefined }, true)}
              className="min-h-11 shrink-0 rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            >
              <option value="and">Match all conditions</option>
              <option value="or">Match any condition</option>
            </select>
          ) : null}
        </div>
      </div>

      <div className="grid min-h-0 flex-1 overflow-hidden md:grid-cols-[20rem_minmax(0,1fr)]">
        <aside className={`${hasEditor ? "hidden md:flex" : "flex"} min-h-0 flex-col border-border md:border-r`} aria-label={`${criterion.label} criteria`}>
          <div className="space-y-3 border-b border-border p-3 md:p-4">
            <LabeledControl label={`Saved ${singular} filter`}>
              <select
                data-filter-primary-control
                aria-label={`Saved ${singular} filter`}
                value={selectedSavedFilterId}
                onChange={(event) => chooseSavedFilter(event.target.value)}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              >
                <option value="">Choose a saved filter…</option>
                {savedFilters.map((savedFilter) => <option key={savedFilter.id} value={savedFilter.id}>{savedFilter.name}</option>)}
              </select>
            </LabeledControl>
            {savedFiltersPending ? <p className="text-xs text-muted">Loading saved filters…</p> : null}
            {savedFiltersError ? <p className="text-xs text-red-300">Saved filters are unavailable. You can still build the filter here.</p> : null}
            <label className="relative block">
              <span className="sr-only">Search {singular} filter criteria</span>
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
              <input
                ref={criteriaSearchRef}
                type="search"
                aria-label={`Search ${singular} filter criteria`}
                value={criteriaSearch}
                onChange={(event) => setCriteriaSearch(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key !== "ArrowDown" || visibleNavigatorKeys.length === 0) return;
                  event.preventDefault();
                  criterionButtonRefs.current.get(visibleNavigatorKeys[0])?.focus();
                }}
                placeholder={`Search ${singular} filters`}
                className="min-h-11 w-full rounded-lg border border-border bg-input py-2 pl-10 pr-3 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
              />
            </label>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-2 md:p-3" role="tablist" aria-label={`Available ${singular} filters`} aria-orientation="vertical">
            {showTextSearch ? (
              <section className="mb-4" aria-label="Quick">
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">Quick</h4>
                <button
                  ref={(element) => { if (element) criterionButtonRefs.current.set("search", element); else criterionButtonRefs.current.delete("search"); }}
                  type="button"
                  role="tab"
                  aria-selected={editingSearch}
                  data-active={related.findFilter?.q?.trim() ? "true" : "false"}
                  tabIndex={rovingNavigatorKey === "search" ? 0 : -1}
                  onClick={() => select({ facet: "search" })}
                  onFocus={() => setNavigatorFocusKey("search")}
                  onKeyDown={(event) => handleNavigatorKeyDown(event, "search")}
                  className={`flex min-h-11 w-full items-center gap-3 rounded-lg border px-3 py-2 text-left text-sm transition ${editingSearch ? "border-accent bg-accent/15 text-foreground" : related.findFilter?.q?.trim() ? "border-accent/30 bg-accent/5 text-foreground hover:bg-card" : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground"}`}
                >
                  <Search className="h-4 w-4 shrink-0" />
                  <span className="font-medium">Text search</span>
                </button>
              </section>
            ) : null}
            {activeCriteria.length > 0 ? (
              <section className="mb-4" aria-label="Active">
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">Active</h4>
                <div className="space-y-1">{activeCriteria.map(renderCriterionRow)}</div>
              </section>
            ) : null}
            {inactiveCriteria.length > 0 ? (
              <section className="mb-4" aria-label={`All ${singular} filters`}>
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">All {singular} filters</h4>
                <div className="space-y-1">{inactiveCriteria.map(renderCriterionRow)}</div>
              </section>
            ) : null}
            {!showTextSearch && filteredCriteria.length === 0 ? <div className="px-4 py-10 text-center text-sm text-muted">No filters match “{criteriaSearch}”.</div> : null}
          </div>
        </aside>

        <main className={`${hasEditor ? "flex" : "hidden md:flex"} min-h-0 min-w-0 flex-col`}>
          {hasEditor ? (
            <div role="tabpanel" aria-label={editingSearch ? "Text search" : editingExistence ? `Any ${singular}` : selectedCriterion?.label} className="flex min-h-0 flex-1 flex-col overflow-y-auto p-4 md:p-6">
              <div className="mb-4 flex items-center gap-2 md:hidden">
                <button
                  type="button"
                  data-mobile-only-control
                  onClick={() => { onSelectionChange(null); window.setTimeout(() => criteriaSearchRef.current?.focus(), 0); }}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
                  aria-label="Back to related filter criteria"
                >
                  <ArrowLeft className="h-5 w-5" />
                </button>
                <span className="text-sm font-semibold text-foreground">{editingSearch ? "Text search" : editingExistence ? `Any ${singular}` : selectedCriterion?.label}</span>
              </div>
              {editingSearch ? (
                <div className="max-w-2xl space-y-4">
                  <div>
                    <h4 className="text-base font-semibold text-foreground">Search related {plural}</h4>
                    <p className="mt-1 text-sm text-secondary">Match the related {singular}'s name, title, tags, or other searchable text.</p>
                  </div>
                  <LabeledControl label={`Search related ${plural}`}>
                    <input
                      data-filter-primary-control
                      type="search"
                      aria-label={`Search related ${plural}`}
                      value={related.findFilter?.q ?? ""}
                      onChange={(event) => update({
                        findFilter: event.target.value ? { q: event.target.value } : undefined,
                        _matchAll: undefined,
                      }, true)}
                      placeholder={`Search ${plural}`}
                      className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
                    />
                  </LabeledControl>
                </div>
              ) : editingExistence ? (
                <div className="max-w-2xl space-y-4">
                  <div>
                    <h4 className="text-base font-semibold text-foreground">Any related {singular}</h4>
                    <p className="mt-1 text-sm text-secondary">Require at least one related {singular} without adding another condition.</p>
                  </div>
                  <button
                    ref={matchAnyRef}
                    data-filter-primary-control
                    type="button"
                    aria-pressed={related._matchAll === true}
                    onClick={toggleMatchAll}
                    className={`min-h-11 w-fit rounded-lg border px-4 py-2 text-sm ${related._matchAll ? "border-accent bg-accent/15 text-foreground" : "border-border text-secondary hover:bg-card hover:text-foreground"}`}
                  >
                    Match any related {singular}
                  </button>
                </div>
              ) : selectedCriterion ? (
                <div className="max-w-2xl space-y-4">
                  <div className="flex items-center justify-between gap-3">
                    <h4 className="text-base font-semibold text-foreground">{selectedCriterion.label}</h4>
                    {isCriterionValueValid(getNestedValue(selectedCriterion), selectedCriterion) ? (
                      <button type="button" onClick={() => removeNestedCriterion(selectedCriterion)} className="text-sm text-muted hover:text-foreground">Remove</button>
                    ) : null}
                  </div>
                  <CriterionEditorComponent
                    criterion={selectedCriterion}
                    value={getNestedValue(selectedCriterion)}
                    auxiliaryToggleChecked={selectedCriterion.auxiliaryToggleKey ? Boolean(objectFilter[selectedCriterion.auxiliaryToggleKey]) : undefined}
                    onAuxiliaryToggleChange={(checked) => {
                      if (!selectedCriterion.auxiliaryToggleKey) return;
                      const nextObjectFilter = { ...objectFilter };
                      if (checked) nextObjectFilter[selectedCriterion.auxiliaryToggleKey] = true;
                      else delete nextObjectFilter[selectedCriterion.auxiliaryToggleKey];
                      update({ objectFilter: nextObjectFilter, _matchAll: undefined }, true);
                    }}
                    onChange={(nextValue) => updateNestedCriterion(selectedCriterion, nextValue)}
                  />
                </div>
              ) : null}
            </div>
          ) : (
            <div className="flex flex-1 items-center justify-center p-8 text-center">
              <div className="max-w-sm">
                <EntityIcon className="mx-auto mb-3 h-8 w-8 text-muted" />
                <h4 className="text-lg font-semibold text-foreground">
                  {relationshipMode === "every" ? `Find ${resultPlural} where every ${singular} matches` : relationshipMode === "none" ? `Find ${resultPlural} where no ${singular} matches` : `Find ${resultPlural} by ${singular}`}
                </h4>
                <p className="mt-1 text-sm text-secondary">
                  {relationshipMode === "every"
                    ? `Show ${resultPlural} with at least one ${singular}, where every related ${singular} matches all filters.`
                    : relationshipMode === "none"
                      ? `Show ${resultPlural} with at least one ${singular}, where no related ${singular} matches all filters.`
                      : `Show ${resultPlural} with a ${singular} ${relativePronoun} matches a saved filter, the filters you add here, or both. All filters must match the same ${singular}.`}
                </p>
                {value ? (
                  <button type="button" onClick={() => onChange(undefined)} className="mt-5 text-sm text-muted hover:text-foreground">Clear related filter</button>
                ) : null}
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
