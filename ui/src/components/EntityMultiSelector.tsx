import { useMemo, useRef, useState } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { faces as facesApi, performers as performersApi, tags as tagsApi } from "../api/client";
import type { Face, Performer, Tag } from "../api/types";
import { rankSearchOptions } from "../utils/searchRanking";
import { AutocompleteDropdown } from "./AutocompleteDropdown";

type EntitySelectorType = "tags" | "performers" | "faces";

interface EntityMultiSelectorProps {
  entityType: EntitySelectorType;
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  emptyMessage?: string;
}

interface SearchOption {
  id: number;
  label: string;
  secondaryLabel?: string;
}

export function EntityMultiSelector({
  entityType,
  values,
  onChange,
  placeholder,
  emptyMessage,
}: EntityMultiSelectorProps) {
  const [searchText, setSearchText] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const trimmedSearch = searchText.trim();

  const { data: searchResults, isLoading } = useQuery({
    queryKey: ["entity-multi-selector", entityType, trimmedSearch],
    queryFn: () => searchEntities(entityType, trimmedSearch),
    enabled: trimmedSearch.length >= 1,
    staleTime: 60_000,
    placeholderData: (previousData) => previousData,
  });

  const searchOptions = useMemo(
    () => rankSearchOptions(searchResults ?? [], trimmedSearch).slice(0, 25),
    [searchResults, trimmedSearch],
  );
  const missingSelectedIds = useMemo(
    () => values.filter((value) => !searchOptions.some((option) => option.id === value)),
    [searchOptions, values],
  );

  const selectedQueries = useQueries({
    queries: missingSelectedIds.map((id) => ({
      queryKey: ["entity-multi-selector", entityType, "selected", id],
      queryFn: () => getEntity(entityType, id),
      staleTime: 60_000,
    })),
  });

  const selectedOptions = useMemo(() => {
    const optionMap = new Map<number, SearchOption>();
    for (const option of searchOptions) {
      optionMap.set(option.id, option);
    }

    for (const query of selectedQueries) {
      if (!query.data) {
        continue;
      }

      optionMap.set(query.data.id, query.data);
    }

    return values
      .map((value) => optionMap.get(value))
      .filter((option): option is SearchOption => option != null);
  }, [searchOptions, selectedQueries, values]);

  const visibleResults = useMemo(
    () => searchOptions.filter((option) => !values.includes(option.id)),
    [searchOptions, values],
  );

  return (
    <div className="relative flex flex-col gap-2">
      {selectedOptions.length > 0 ? (
        <div className="flex flex-wrap gap-1">
          {selectedOptions.map((option) => (
            <span key={option.id} className="inline-flex items-center gap-1 rounded border border-border bg-card px-2 py-0.5 text-[10px] text-foreground">
              <span>{option.label}</span>
              {option.secondaryLabel ? <span className="text-muted">{option.secondaryLabel}</span> : null}
              <button
                type="button"
                onClick={() => onChange(values.filter((value) => value !== option.id))}
                className="hover:text-red-400"
                aria-label={`Remove ${option.label}`}
              >
                <X className="h-2.5 w-2.5" />
              </button>
            </span>
          ))}
        </div>
      ) : null}

      <input
        ref={inputRef}
        type="text"
        value={searchText}
        onChange={(event) => setSearchText(event.target.value)}
        placeholder={placeholder ?? `Search ${entityType}...`}
        className="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
      />

      {trimmedSearch ? (
        <AutocompleteDropdown anchorRef={inputRef} className="rounded border border-border bg-surface">
          {isLoading ? <div className="px-3 py-2 text-sm text-muted">Loading...</div> : null}
          {!isLoading && visibleResults.length === 0 ? (
            <div className="px-3 py-2 text-sm text-muted">{emptyMessage ?? `No ${entityType} found`}</div>
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
        </AutocompleteDropdown>
      ) : null}
    </div>
  );
}

async function searchEntities(entityType: EntitySelectorType, searchText: string) {
  switch (entityType) {
    case "tags": {
      const response = await tagsApi.find({ q: searchText || undefined, perPage: 100, sort: "name", direction: "asc" });
      return response.items.map(toTagOption);
    }
    case "performers": {
      const response = await performersApi.find({ q: searchText || undefined, perPage: 100, sort: "name", direction: "asc" });
      return response.items.map(toPerformerOption);
    }
    case "faces": {
      const response = await facesApi.list({ q: searchText || undefined, merged: false, page: 1, perPage: 100 });
      return response.items.map(toFaceOption);
    }
  }
}

async function getEntity(entityType: EntitySelectorType, id: number) {
  switch (entityType) {
    case "tags":
      return toTagOption(await tagsApi.get(id));
    case "performers":
      return toPerformerOption(await performersApi.get(id));
    case "faces":
      return toFaceOption(await facesApi.get(id));
  }
}

function toTagOption(tag: Tag): SearchOption {
  return {
    id: tag.id,
    label: tag.name,
  };
}

function toPerformerOption(performer: Performer): SearchOption {
  return {
    id: performer.id,
    label: performer.name,
    secondaryLabel: performer.disambiguation ? `(${performer.disambiguation})` : undefined,
  };
}

function toFaceOption(face: Face): SearchOption {
  const label = face.label?.trim() || face.performerName?.trim() || "Unidentified face";
  const secondaryLabel = face.performerName && face.performerName !== label
    ? `Linked to ${face.performerName}`
    : face.primarySourceKey
      ? face.primarySourceKey
      : undefined;

  return {
    id: face.id,
    label,
    secondaryLabel,
  };
}
