import { useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { studios as studiosApi } from "../api/client";
import { rankByLabel } from "../utils/searchRanking";
import { AutocompleteDropdown } from "./AutocompleteDropdown";

interface StudioSelectorProps {
  value?: number;
  onChange: (value: number | undefined) => void;
  placeholder?: string;
}

export function StudioSelector({ value, onChange, placeholder = "Search studios..." }: StudioSelectorProps) {
  const [searchText, setSearchText] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const trimmedSearch = searchText.trim();
  const queryClient = useQueryClient();

  const { data: searchResults, isLoading, isFetching } = useQuery({
    queryKey: ["studio-selector", trimmedSearch],
    queryFn: async () => {
      const response = await studiosApi.find({
        q: trimmedSearch || undefined,
        perPage: 100,
        sort: "name",
        direction: "asc",
      });

      return response.items;
    },
    staleTime: 60000,
    enabled: trimmedSearch.length >= 1,
    placeholderData: (previousData) => previousData,
  });

  const selectedResult = searchResults?.find((studio) => studio.id === value);

  const { data: selectedStudio } = useQuery({
    queryKey: ["studio-selector", "selected", value],
    queryFn: async () => studiosApi.get(value as number),
    enabled: typeof value === "number" && !selectedResult,
    staleTime: 60000,
  });

  const selectedLabel = selectedResult?.name ?? selectedStudio?.name;
  const visibleResults = useMemo(
    () => rankByLabel((searchResults ?? []).filter((studio) => studio.id !== value), trimmedSearch, (studio) => studio.name).slice(0, 25),
    [searchResults, trimmedSearch, value],
  );

  const exactMatchExists = trimmedSearch && (searchResults ?? []).some((s) => s.name.toLowerCase() === trimmedSearch.toLowerCase());
  const createMutation = useMutation({
    mutationFn: (name: string) => studiosApi.create({ name }),
    onSuccess: (result) => {
      onChange(result.id);
      setSearchText("");
      queryClient.invalidateQueries({ queryKey: ["studios"] });
    },
  });
  const showCreateOption = trimmedSearch && !isFetching && !exactMatchExists;

  return (
    <div className="relative flex flex-col gap-2">
      {selectedLabel && (
        <div className="flex flex-wrap gap-1">
          <span className="inline-flex items-center gap-1 rounded border border-border bg-card px-2 py-0.5 text-[10px] text-foreground">
            {selectedLabel}
            <button onClick={() => onChange(undefined)} className="hover:text-red-400" aria-label="Clear selected studio">
              <X className="h-2.5 w-2.5" />
            </button>
          </span>
        </div>
      )}

      <input
        ref={inputRef}
        type="text"
        value={searchText}
        onChange={(e) => setSearchText(e.target.value)}
        placeholder={placeholder}
        className="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
      />

      {trimmedSearch && (
        <AutocompleteDropdown anchorRef={inputRef} maxHeight={128} className="rounded border border-border bg-surface">
          {isLoading ? (
            <div className="px-3 py-2 text-sm text-muted">Loading...</div>
          ) : visibleResults.length === 0 && !showCreateOption ? (
            <div className="px-3 py-2 text-sm text-muted">No studios found</div>
          ) : null}
          {visibleResults.map((studio) => (
            <button
              key={studio.id}
              onClick={() => {
                onChange(studio.id);
                setSearchText("");
              }}
              className="flex w-full items-center gap-1 px-3 py-2 text-left text-sm text-foreground hover:bg-card"
            >
              <Plus className="h-3 w-3" />
              {studio.name}
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
      )}
    </div>
  );
}
