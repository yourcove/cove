import { Search, X } from "lucide-react";
import { useCallback, useEffect, useState } from "react";

export type ListSearchCommitSource = "debounce" | "submit" | "clear";

interface ListSearchControlProps {
  query?: string;
  onQueryChange: (query: string | undefined, source: ListSearchCommitSource) => void;
  placeholder?: string;
  className?: string;
  searchMode?: string;
  searchModes?: { value: string; label: string; title?: string }[];
  onSearchModeChange?: (mode: string) => void;
}

const LIST_SEARCH_DEBOUNCE_MS = 350;

export function ListSearchControl({ query, onQueryChange, placeholder = "Search names, titles, tags...", className = "", searchMode, searchModes, onSearchModeChange }: ListSearchControlProps) {
  const [searchText, setSearchText] = useState(query ?? "");

  useEffect(() => {
    setSearchText((currentSearchText) => (
      currentSearchText.trim() === (query ?? "").trim()
        ? currentSearchText
        : query ?? ""
    ));
  }, [query]);

  const commitSearch = useCallback((rawSearchText: string, source: ListSearchCommitSource) => {
    const normalizedSearch = rawSearchText.trim();
    if (normalizedSearch === (query ?? "").trim()) return;
    onQueryChange(normalizedSearch || undefined, source);
  }, [onQueryChange, query]);

  useEffect(() => {
    if (searchText.trim() === (query ?? "").trim()) return;
    const timeout = window.setTimeout(() => commitSearch(searchText, "debounce"), LIST_SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timeout);
  }, [commitSearch, query, searchText]);

  const clearSearch = () => {
    setSearchText("");
    commitSearch("", "clear");
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        commitSearch(searchText, "submit");
      }}
      className={`flex w-full shrink-0 items-center gap-1 ${className}`}
    >
      {searchModes && searchModes.length > 1 && onSearchModeChange ? (
        <select
          value={searchMode ?? searchModes[0]?.value ?? "text"}
          onChange={(event) => onSearchModeChange(event.target.value)}
          className="min-h-10 max-w-[6.5rem] rounded-lg border border-border bg-card/70 px-2 py-2 text-sm text-foreground focus:border-accent focus:outline-none sm:min-h-[30px] sm:max-w-[5.75rem] sm:py-1.5 sm:text-xs"
          aria-label="Search mode"
          title="Search mode"
        >
          {searchModes.map((mode) => (
            <option key={mode.value} value={mode.value} title={mode.title}>{mode.label}</option>
          ))}
        </select>
      ) : null}
      <div className="relative min-w-0 flex-1">
        <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted" />
        <input
          type="text"
          value={searchText}
          onChange={(event) => setSearchText(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Escape" && searchText.trim().length > 0) {
              event.preventDefault();
              clearSearch();
            }
          }}
          placeholder={placeholder}
          aria-label="Search list"
          data-list-search="true"
          className="min-h-10 w-full rounded-lg border border-border bg-card/70 py-2 pl-8 pr-8 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none sm:min-h-0 sm:py-1.5 sm:pl-7 sm:pr-7 sm:text-xs"
        />
        {searchText.trim().length > 0 ? (
          <button
            type="button"
            onClick={clearSearch}
            className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded p-0.5 text-muted hover:bg-card/80 hover:text-foreground focus:outline-none focus:ring-1 focus:ring-accent"
            aria-label="Clear search"
            title="Clear search"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        ) : null}
      </div>
    </form>
  );
}
