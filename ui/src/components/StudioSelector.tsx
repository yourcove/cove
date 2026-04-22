import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { studios as studiosApi } from "../api/client";

interface StudioSelectorProps {
  value?: number;
  onChange: (value: number | undefined) => void;
  placeholder?: string;
}

export function StudioSelector({ value, onChange, placeholder = "Search studios..." }: StudioSelectorProps) {
  const [searchText, setSearchText] = useState("");
  const trimmedSearch = searchText.trim();

  const { data: searchResults, isLoading } = useQuery({
    queryKey: ["studio-selector", trimmedSearch],
    queryFn: async () => {
      const response = await studiosApi.find({
        q: trimmedSearch || undefined,
        perPage: 25,
        sort: "name",
        direction: "asc",
      });

      return response.items;
    },
    staleTime: 60000,
    enabled: trimmedSearch.length >= 1,
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
    () => (searchResults ?? []).filter((studio) => studio.id !== value),
    [searchResults, value],
  );

  return (
    <div className="space-y-2">
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
        type="text"
        value={searchText}
        onChange={(e) => setSearchText(e.target.value)}
        placeholder={placeholder}
        className="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
      />

      {trimmedSearch && (
        <div className="max-h-32 overflow-y-auto rounded border border-border bg-surface">
          {isLoading ? (
            <div className="px-3 py-2 text-sm text-muted">Loading...</div>
          ) : visibleResults.length === 0 ? (
            <div className="px-3 py-2 text-sm text-muted">No studios found</div>
          ) : (
            visibleResults.map((studio) => (
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
            ))
          )}
        </div>
      )}
    </div>
  );
}