import { useEffect, useMemo, useState } from "react";
import { ArrowDown, ArrowUp, ChevronDown, ChevronUp, GripVertical, Plus, Shuffle, X } from "lucide-react";
import type { FindFilter, SortClause } from "../api/types";
import { reshuffleRandomSort, withSeededRandomSort } from "../utils/seededRandomSort";
import { defaultSortDirection, getSortClauses, MAX_SORT_CLAUSES, withSortClauses } from "../utils/sortClauses";
import { toolbarIconButtonClass, toolbarSegmentClass, toolbarSelectClass } from "./listToolbarStyles";

interface MultiSortControlProps {
  filter: FindFilter;
  onFilterChange: (filter: FindFilter) => void;
  options: { value: string; label: string }[];
  multiSortKeys?: readonly string[];
}

const SUGGESTED_SECONDARY_SORTS = [
  "date",
  "studio",
  "title",
  "created_at",
  "updated_at",
];

function optionLabel(options: MultiSortControlProps["options"], key: string) {
  return options.find((option) => option.value === key)?.label ?? key;
}

export function MultiSortControl({ filter, onFilterChange, options, multiSortKeys }: MultiSortControlProps) {
  const [open, setOpen] = useState(false);
  const storedClauses = getSortClauses(filter);
  const multiSortAvailable = (multiSortKeys?.length ?? 0) > 0;
  const clauses = multiSortAvailable
    ? storedClauses
    : filter.sort
      ? [{ key: filter.sort, direction: filter.direction ?? defaultSortDirection(filter.sort) }]
      : storedClauses.slice(0, 1);
  const primary = clauses[0] ?? {
    key: options[0]?.value ?? "",
    direction: filter.direction ?? "asc",
  };
  const allowedKeys = useMemo(() => new Set(multiSortKeys ?? []), [multiSortKeys]);
  const multiOptions = useMemo(
    () => options.filter((option) => allowedKeys.has(option.value)),
    [allowedKeys, options],
  );
  const canCombinePrimary = allowedKeys.has(primary.key) && multiOptions.length > 1;
  const hasAdditionalSorts = clauses.length > 1;
  const fullSortSummary = clauses
    .map((clause, index) => `${index + 1}. ${optionLabel(options, clause.key)} ${clause.direction === "asc" ? "ascending" : "descending"}`)
    .join("; ");

  useEffect(() => {
    if (!open) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [open]);

  const applyClauses = (nextClauses: SortClause[]) => {
    const nextFilter = withSortClauses({ ...filter, page: 1 }, nextClauses);
    onFilterChange(withSeededRandomSort(filter, nextFilter));
  };

  const changePrimary = (key: string) => {
    const existingIndex = clauses.findIndex((clause) => clause.key === key);
    let nextClauses: SortClause[];

    if (!allowedKeys.has(key)) {
      nextClauses = [{ key, direction: defaultSortDirection(key) }];
      setOpen(false);
    } else if (existingIndex > 0) {
      nextClauses = [
        clauses[existingIndex],
        ...clauses.filter((_, index) => index !== existingIndex),
      ];
    } else {
      const direction = existingIndex === 0 ? clauses[0].direction : defaultSortDirection(key);
      nextClauses = [
        { key, direction },
        ...clauses.slice(1).filter((clause) => clause.key !== key),
      ];
    }

    applyClauses(nextClauses);
  };

  const updateClause = (index: number, patch: Partial<SortClause>) => {
    const current = clauses[index];
    if (!current) return;
    const nextKey = patch.key ?? current.key;
    const duplicateIndex = clauses.findIndex((clause, clauseIndex) => clauseIndex !== index && clause.key === nextKey);
    const next = clauses.map((clause) => ({ ...clause }));
    next[index] = {
      key: nextKey,
      direction: patch.direction ?? (patch.key ? defaultSortDirection(nextKey) : current.direction),
    };
    if (duplicateIndex >= 0) {
      next[duplicateIndex] = current;
    }
    applyClauses(next);
  };

  const moveClause = (index: number, offset: -1 | 1) => {
    const destination = index + offset;
    if (destination < 0 || destination >= clauses.length) return;
    const next = clauses.map((clause) => ({ ...clause }));
    [next[index], next[destination]] = [next[destination], next[index]];
    applyClauses(next);
  };

  const moveClauseTo = (index: number, destination: number) => {
    if (destination < 0 || destination >= clauses.length || destination === index) return;
    const next = clauses.map((clause) => ({ ...clause }));
    const [moved] = next.splice(index, 1);
    next.splice(destination, 0, moved);
    applyClauses(next);
  };

  const addClause = () => {
    const selected = new Set(clauses.map((clause) => clause.key));
    const suggestedKey = SUGGESTED_SECONDARY_SORTS.find((key) => allowedKeys.has(key) && !selected.has(key));
    const nextOption = multiOptions.find((option) => option.value === suggestedKey)
      ?? multiOptions.find((option) => !selected.has(option.value));
    if (!nextOption || clauses.length >= MAX_SORT_CLAUSES) return;
    applyClauses([
      ...clauses,
      { key: nextOption.value, direction: defaultSortDirection(nextOption.value) },
    ]);
  };

  const removeClause = (index: number) => {
    if (clauses.length <= 1) return;
    applyClauses(clauses.filter((_, clauseIndex) => clauseIndex !== index));
  };

  const canAdd = clauses.length < Math.min(MAX_SORT_CLAUSES, multiOptions.length);

  return (
    <div className={`${toolbarSegmentClass} relative`}>
      {hasAdditionalSorts ? (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="inline-flex min-h-10 max-w-[18rem] items-center gap-1.5 overflow-hidden rounded-md px-2.5 py-2 text-sm text-foreground transition-colors hover:bg-card/80 focus:outline-none focus:ring-1 focus:ring-accent sm:min-h-[30px] sm:py-1 sm:text-xs"
          title={fullSortSummary}
          aria-label={`Edit sort order: ${fullSortSummary}`}
        >
          {clauses.slice(0, 2).map((clause, index) => (
            <span key={clause.key} className="inline-flex min-w-0 items-center gap-1">
              {index > 0 && <span className="shrink-0 text-muted" aria-hidden="true">·</span>}
              <span className="truncate">{index + 1}. {optionLabel(options, clause.key)}</span>
              {clause.direction === "asc"
                ? <ArrowUp className="h-3 w-3 shrink-0" aria-hidden="true" />
                : <ArrowDown className="h-3 w-3 shrink-0" aria-hidden="true" />}
            </span>
          ))}
          {clauses.length > 2 && (
            <span className="shrink-0 font-semibold text-accent">+{clauses.length - 2}</span>
          )}
        </button>
      ) : (
        <>
          <select
            value={primary.key}
            onChange={(event) => changePrimary(event.target.value)}
            className={`${toolbarSelectClass} min-w-[8.5rem] max-w-[10rem]`}
            aria-label="Primary sort"
          >
            {options.map((option) => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </select>

          {primary.key !== "relevance" && (
            <button
              type="button"
              onClick={() => applyClauses([
                { ...primary, direction: primary.direction === "desc" ? "asc" : "desc" },
                ...clauses.slice(1),
              ])}
              className={toolbarIconButtonClass}
              title={primary.direction === "desc" ? "Sort descending" : "Sort ascending"}
              aria-label={primary.direction === "desc" ? "Sort descending" : "Sort ascending"}
            >
              {primary.direction === "desc" ? <ArrowDown className="h-3.5 w-3.5" /> : <ArrowUp className="h-3.5 w-3.5" />}
            </button>
          )}

          {primary.key === "random" && (
            <button
              type="button"
              onClick={() => onFilterChange(reshuffleRandomSort(filter))}
              className={toolbarIconButtonClass}
              title="Shuffle"
              aria-label="Shuffle"
            >
              <Shuffle className="h-3.5 w-3.5" />
            </button>
          )}

          {canCombinePrimary && (
            <button
              type="button"
              onClick={() => setOpen(true)}
              className={`${toolbarIconButtonClass} gap-0.5`}
              title="Add another sort"
              aria-label="Add another sort"
            >
              <Plus className="h-3.5 w-3.5" />
            </button>
          )}
        </>
      )}

      {open && (
        <>
          <button
            type="button"
            className="fixed inset-0 z-40 cursor-default bg-black/35 sm:bg-transparent"
            aria-label="Close sort order"
            onClick={() => setOpen(false)}
          />
          <div
            role="dialog"
            aria-label="Sort order"
            className="fixed inset-x-3 bottom-3 z-50 rounded-xl border border-border bg-surface p-3 shadow-2xl shadow-black/50 sm:absolute sm:inset-x-auto sm:bottom-auto sm:right-0 sm:top-[calc(100%+0.5rem)] sm:w-[30rem]"
          >
            <div className="mb-3 flex items-center justify-between gap-3">
              <h2 className="text-sm font-semibold text-foreground">Sort order</h2>
              <button type="button" onClick={() => setOpen(false)} className={toolbarIconButtonClass} aria-label="Close">
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="space-y-2">
              {clauses.map((clause, index) => (
                <div key={`${clause.key}-${index}`} className="flex min-w-0 items-center gap-1.5 rounded-lg border border-border bg-card/60 p-1.5">
                  <div className="hidden flex-col gap-0.5 sm:flex">
                    <button
                      type="button"
                      onClick={() => moveClause(index, -1)}
                      disabled={index === 0}
                      className="inline-flex min-h-6 min-w-6 items-center justify-center text-muted hover:text-foreground disabled:opacity-30"
                      aria-label={`Move ${optionLabel(options, clause.key)} earlier`}
                    >
                      <ChevronUp className="h-4 w-4" />
                    </button>
                    <button
                      type="button"
                      onClick={() => moveClause(index, 1)}
                      disabled={index === clauses.length - 1}
                      className="inline-flex min-h-6 min-w-6 items-center justify-center text-muted hover:text-foreground disabled:opacity-30"
                      aria-label={`Move ${optionLabel(options, clause.key)} later`}
                    >
                      <ChevronDown className="h-4 w-4" />
                    </button>
                  </div>
                  <GripVertical className="hidden h-4 w-4 shrink-0 text-muted/60 sm:block" aria-hidden="true" />
                  <select
                    value={index}
                    onChange={(event) => moveClauseTo(index, Number(event.target.value))}
                    className="h-10 w-10 shrink-0 rounded-md border border-border/60 bg-input px-1 text-center text-xs font-semibold text-muted sm:hidden"
                    aria-label={`Priority for ${optionLabel(options, clause.key)}`}
                  >
                    {clauses.map((_, priority) => (
                      <option key={priority} value={priority}>{priority + 1}</option>
                    ))}
                  </select>
                  <span className="hidden w-5 shrink-0 text-center text-xs font-semibold text-muted sm:block">{index + 1}.</span>
                  <select
                    value={clause.key}
                    onChange={(event) => updateClause(index, { key: event.target.value })}
                    className={`${toolbarSelectClass} min-w-0 flex-1`}
                    aria-label={`Sort level ${index + 1}`}
                  >
                    {multiOptions.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                  <button
                    type="button"
                    onClick={() => updateClause(index, { direction: clause.direction === "asc" ? "desc" : "asc" })}
                    className={`${toolbarIconButtonClass} shrink-0 gap-1 px-2`}
                    aria-label={`${optionLabel(options, clause.key)} ${clause.direction === "asc" ? "ascending" : "descending"}`}
                    title={clause.direction === "asc" ? "Ascending" : "Descending"}
                  >
                    {clause.direction === "asc" ? <ArrowUp className="h-3.5 w-3.5" /> : <ArrowDown className="h-3.5 w-3.5" />}
                  </button>
                  <button
                    type="button"
                    onClick={() => removeClause(index)}
                    disabled={clauses.length === 1}
                    className={`${toolbarIconButtonClass} shrink-0 disabled:opacity-25`}
                    aria-label={`Remove ${optionLabel(options, clause.key)} sort`}
                  >
                    <X className="h-3.5 w-3.5" />
                  </button>
                </div>
              ))}
            </div>

            <button
              type="button"
              onClick={addClause}
              disabled={!canAdd}
              className="mt-3 inline-flex min-h-10 w-full items-center justify-center gap-1.5 rounded-lg border border-dashed border-border px-3 py-2 text-sm text-secondary transition-colors hover:border-accent/60 hover:text-accent disabled:cursor-not-allowed disabled:opacity-40 sm:min-h-0 sm:py-1.5 sm:text-xs"
              aria-label="Add sort"
            >
              <Plus className="h-3.5 w-3.5" />
              Add sort
            </button>
          </div>
        </>
      )}
    </div>
  );
}
