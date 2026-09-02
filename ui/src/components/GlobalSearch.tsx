import { useEffect, useId, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import { BookOpenText, Building2, Film, FolderOpen, Headphones, ImageIcon, Layers, Loader2, Search, Tag, Users } from "lucide-react";
import { globalSearch } from "../api/client";
import type { GlobalSearchEntityType, InteractionHostType } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity } from "../auth/visibility";
import { trackInteraction } from "../utils/interactionTracking";
import { getServerAvailability, subscribeToServerAvailability } from "../state/serverAvailability";
import { SERVER_UNAVAILABLE_DETAIL } from "../utils/requestFailure";

interface Props {
  navigate: (r: any) => void;
}

type SearchGroup = {
  key: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  items: { id: number; title: string; subtitle?: string; route: any; hostType: InteractionHostType }[];
};

type SearchPresentation = {
  key: GlobalSearchEntityType;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  hostType: InteractionHostType;
  page: string;
};

const GLOBAL_SEARCH_DEBOUNCE_MS = 100;
const SEARCH_PRESENTATIONS: Record<GlobalSearchEntityType, SearchPresentation> = {
  video: { key: "video", label: "Videos", icon: Film, hostType: "video", page: "video" },
  performer: { key: "performer", label: "Performers", icon: Users, hostType: "performer", page: "performer" },
  studio: { key: "studio", label: "Studios", icon: Building2, hostType: "studio", page: "studio" },
  tag: { key: "tag", label: "Tags", icon: Tag, hostType: "tag", page: "tag" },
  gallery: { key: "gallery", label: "Galleries", icon: FolderOpen, hostType: "gallery", page: "gallery" },
  image: { key: "image", label: "Images", icon: ImageIcon, hostType: "image", page: "image" },
  group: { key: "group", label: "Groups", icon: Layers, hostType: "group", page: "group" },
  audio: { key: "audio", label: "Audios", icon: Headphones, hostType: "audio", page: "audio" },
  text: { key: "text", label: "Texts", icon: BookOpenText, hostType: "text", page: "text" },
};

export function GlobalSearch({ navigate }: Props) {
  const [term, setTerm] = useState("");
  const [committedTerm, setCommittedTerm] = useState("");
  const [open, setOpen] = useState(false);
  const [activeResultIndex, setActiveResultIndex] = useState(-1);
  const [desktopPanelStyle, setDesktopPanelStyle] = useState<{ left: number; top: number; width: number } | null>(null);
  const normalizedTerm = term.trim();
  const containerRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const lastTrackedSearchKey = useRef("");
  const searchId = useId();
  const { hasPermission, permissions } = useAuth();
  const serverAvailability = useSyncExternalStore(
    subscribeToServerAvailability,
    getServerAvailability,
    getServerAvailability,
  );

  useEffect(() => {
    if (normalizedTerm.length < 2) {
      setCommittedTerm(normalizedTerm);
      return;
    }
    const timeout = window.setTimeout(() => setCommittedTerm(normalizedTerm), GLOBAL_SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timeout);
  }, [normalizedTerm]);

  const readableEntities = useMemo(() => ({
    videos: canReadEntity("video", hasPermission),
    performers: canReadEntity("performer", hasPermission),
    studios: canReadEntity("studio", hasPermission),
    tags: canReadEntity("tag", hasPermission),
    galleries: canReadEntity("gallery", hasPermission),
    images: canReadEntity("image", hasPermission),
    groups: canReadEntity("group", hasPermission),
    audios: canReadEntity("audio", hasPermission),
    texts: canReadEntity("text", hasPermission),
  }), [hasPermission, permissions]);

  const searchableLabels = useMemo(() => {
    const labels: string[] = [];
    if (readableEntities.videos) labels.push("videos");
    if (readableEntities.performers) labels.push("performers");
    if (readableEntities.studios) labels.push("studios");
    if (readableEntities.tags) labels.push("tags");
    if (readableEntities.galleries) labels.push("galleries");
    if (readableEntities.images) labels.push("images");
    if (readableEntities.groups) labels.push("groups");
    if (readableEntities.audios) labels.push("audios");
    if (readableEntities.texts) labels.push("texts");
    return labels;
  }, [readableEntities]);

  useEffect(() => {
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      const inSearchControl = containerRef.current?.contains(target);
      const inSearchPanel = panelRef.current?.contains(target);

      if (!inSearchControl && !inSearchPanel) {
        setOpen(false);
        setActiveResultIndex(-1);
      }
    };
    document.addEventListener("mousedown", onPointerDown);
    return () => document.removeEventListener("mousedown", onPointerDown);
  }, []);

  useEffect(() => {
    if (!open) {
      setDesktopPanelStyle(null);
      return;
    }

    const updatePanelPosition = () => {
      const trigger = containerRef.current;
      if (!trigger) return;

      const rect = trigger.getBoundingClientRect();
      const viewportWidth = window.innerWidth;
      const width = Math.min(480, Math.max(280, viewportWidth - 32));
      const left = Math.min(Math.max(16, rect.right - width), Math.max(16, viewportWidth - width - 16));
      setDesktopPanelStyle({ left, top: rect.bottom + 8, width });
    };

    updatePanelPosition();
    window.addEventListener("resize", updatePanelPosition);
    window.addEventListener("scroll", updatePanelPosition, true);
    return () => {
      window.removeEventListener("resize", updatePanelPosition);
      window.removeEventListener("scroll", updatePanelPosition, true);
    };
  }, [open]);

  const { data, isFetching, isError, refetch } = useQuery({
    queryKey: ["global-search", committedTerm, searchableLabels.join(",")],
    enabled: committedTerm.length >= 2 && searchableLabels.length > 0,
    queryFn: ({ signal }) => globalSearch.find(committedTerm, 8, signal),
  });

  const resultMatchesInput = committedTerm === normalizedTerm;
  const groupsData = useMemo<SearchGroup[]>(() => resultMatchesInput ? (data?.groups ?? []).flatMap((group) => {
    const presentation = SEARCH_PRESENTATIONS[group.type];
    if (!presentation) return [];
    return [{
      key: presentation.key,
      label: presentation.label,
      icon: presentation.icon,
      items: group.items.map((item) => ({
        id: item.id,
        title: item.title,
        subtitle: item.subtitle ?? undefined,
        route: { page: presentation.page, id: item.id },
        hostType: presentation.hostType,
      })),
    }];
  }) : [], [data, resultMatchesInput]);
  const failedLabels = resultMatchesInput
    ? (data?.failedTypes ?? []).map((type) => SEARCH_PRESENTATIONS[type]?.label ?? type)
    : [];
  const allSearchesFailed = searchableLabels.length > 0 && failedLabels.length === searchableLabels.length;
  const searchFailed = resultMatchesInput && (isError || allSearchesFailed);
  const isWaiting = normalizedTerm.length >= 2 && (!resultMatchesInput || isFetching);
  const flatResults = useMemo(() => groupsData.flatMap((group) => group.items), [groupsData]);

  useEffect(() => {
    if (activeResultIndex < 0) return;
    const activeResult = Array.from(panelRef.current?.querySelectorAll<HTMLElement>(`[data-result-index="${activeResultIndex}"]`) ?? [])
      .find((element) => element.offsetParent !== null);
    activeResult?.scrollIntoView({ block: "nearest" });
  }, [activeResultIndex]);

  useEffect(() => {
    if (!open || committedTerm.length < 2 || !resultMatchesInput || isFetching || searchFailed || searchableLabels.length === 0) {
      return;
    }

    const resultCount = flatResults.length;
    const searchKey = `${committedTerm}|${searchableLabels.join(",")}|${resultCount}`;
    if (lastTrackedSearchKey.current === searchKey) {
      return;
    }

    lastTrackedSearchKey.current = searchKey;
    trackInteraction({
      hostType: "search",
      kind: "searchQuery",
      meta: {
        query: committedTerm,
        resultCount,
        scopes: searchableLabels,
        source: "globalSearch",
      },
    });
  }, [committedTerm, flatResults.length, isFetching, open, resultMatchesInput, searchableLabels, searchFailed]);

  const handleSelect = (item: SearchGroup["items"][number], rank: number) => {
    trackInteraction({
      hostType: item.hostType,
      hostId: item.id,
      kind: "searchSelect",
      meta: {
        query: committedTerm,
        rank,
        source: "globalSearch",
      },
    });
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    navigate(item.route);
    setOpen(false);
    setTerm("");
    setActiveResultIndex(-1);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Escape") {
      event.preventDefault();
      setOpen(false);
      setTerm("");
      setCommittedTerm("");
      setActiveResultIndex(-1);
      event.currentTarget.blur();
      return;
    }

    if (event.key === "Tab") {
      setOpen(false);
      setActiveResultIndex(-1);
      return;
    }

    if ((event.key === "ArrowDown" || event.key === "ArrowUp") && flatResults.length > 0) {
      event.preventDefault();
      setActiveResultIndex((current) => {
        if (event.key === "ArrowDown") return (current + 1) % flatResults.length;
        return current <= 0 ? flatResults.length - 1 : current - 1;
      });
      return;
    }

    if (event.key === "Enter" && normalizedTerm.length >= 2) {
      event.preventDefault();
      if (!resultMatchesInput) {
        setCommittedTerm(normalizedTerm);
        return;
      }
      const selectedIndex = activeResultIndex >= 0 ? activeResultIndex : 0;
      if (flatResults[selectedIndex]) handleSelect(flatResults[selectedIndex], selectedIndex + 1);
    }
  };

  const renderResults = (listboxId: string, optionIdPrefix: string) => (
    <>
      <div className="border-b border-border px-3 py-2 text-[11px] uppercase tracking-wider text-muted">
        Global Search
      </div>
      {searchableLabels.length === 0 ? (
        <div className="px-4 py-6 text-sm text-secondary">No searchable libraries are available for this account.</div>
      ) : normalizedTerm.length < 2 ? (
        <div className="px-4 py-6 text-sm text-secondary">Type at least 2 characters to search {searchableLabels.join(", ")}.</div>
      ) : isWaiting ? (
        <div className="flex items-center gap-2 px-4 py-6 text-sm text-secondary">
          <Loader2 className="h-4 w-4 animate-spin" /> Searching...
        </div>
      ) : searchFailed ? (
        <div role="alert" className="space-y-2 px-4 py-5 text-sm">
          <p className="font-medium text-foreground">Search could not be completed.</p>
          <p className="text-secondary">
            {serverAvailability === "available" ? "Cove could not load any searchable library." : SERVER_UNAVAILABLE_DETAIL}
          </p>
          <button
            type="button"
            onClick={() => void refetch()}
            className="rounded-md border border-border px-2.5 py-1 text-xs font-semibold text-foreground hover:border-accent hover:text-accent"
          >
            Try again
          </button>
        </div>
      ) : (
        <div id={listboxId} role="listbox" aria-label="Global search results" className="max-h-[28rem] overflow-y-auto">
          {failedLabels.length > 0 ? (
            <div className="border-b border-border px-3 py-2 text-xs text-amber-300">
              Search failed for {failedLabels.join(", ")}.
            </div>
          ) : null}
          {groupsData.length === 0 ? (
            <div className="px-4 py-6 text-sm text-secondary">
              {failedLabels.length > 0
                ? <>No results found in the searches that completed for &ldquo;{normalizedTerm}&rdquo;.</>
                : <>No results found for &ldquo;{normalizedTerm}&rdquo;.</>}
            </div>
          ) : groupsData.map((group) => {
            const Icon = group.icon;
            return (
              <div key={group.key} className="border-b border-border last:border-b-0">
                <div className="flex items-center gap-2 px-3 py-2 text-[11px] font-semibold uppercase tracking-wider text-muted">
                  <Icon className="h-3.5 w-3.5" />
                  {group.label}
                </div>
                <div className="pb-2">
                  {group.items.map((item) => {
                    const resultIndex = flatResults.findIndex((result) => result.hostType === item.hostType && result.id === item.id);
                    const isActive = resultIndex === activeResultIndex;
                    return (
                    <button
                      key={`${group.key}-${item.id}`}
                      type="button"
                      role="option"
                      id={`${optionIdPrefix}-${resultIndex}`}
                      aria-selected={isActive}
                      tabIndex={-1}
                      data-result-index={resultIndex}
                      onMouseMove={() => setActiveResultIndex(resultIndex)}
                      onClick={() => handleSelect(item, resultIndex + 1)}
                      className={`flex w-full items-start gap-3 px-3 py-2 text-left hover:bg-surface ${isActive ? "bg-accent/15 ring-1 ring-inset ring-accent" : ""}`}
                    >
                      <Icon className="mt-0.5 h-4 w-4 shrink-0 text-accent" />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-sm text-foreground">{item.title}</span>
                        {item.subtitle && <span className="block truncate text-xs text-secondary">{item.subtitle}</span>}
                      </span>
                    </button>
                    );
                  })}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </>
  );

  const renderedPanel = open && typeof document !== "undefined" ? createPortal(
    <>
      <div className="pointer-events-none fixed inset-0 z-40 bg-black/60" />
      <div ref={panelRef}>
        {/* Mobile: full-width search input dropdown */}
        <div className="md:hidden fixed left-4 right-4 top-14 z-[60]">
          <div className="overflow-hidden rounded-lg border border-border bg-surface shadow-xl">
            <div className="p-2 border-b border-border">
              <div className="relative">
                <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                <input
                  value={term}
                  onChange={(event) => {
                    setTerm(event.target.value);
                    setActiveResultIndex(-1);
                  }}
                  onKeyDown={handleKeyDown}
                  role="combobox"
                  aria-autocomplete="list"
                  aria-expanded={open}
                  aria-controls={`${searchId}-mobile-listbox`}
                  aria-activedescendant={activeResultIndex >= 0 ? `${searchId}-mobile-option-${activeResultIndex}` : undefined}
                  aria-label="Search all..."
                  placeholder="Search all..."
                  className="w-full rounded-lg border border-border bg-input py-1.5 pl-9 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
                />
              </div>
            </div>
            {renderResults(`${searchId}-mobile-listbox`, `${searchId}-mobile-option`)}
          </div>
        </div>

        {/* Desktop: results dropdown */}
        {desktopPanelStyle ? (
          <div
            className="hidden md:block fixed z-[60] overflow-hidden rounded-lg border border-border bg-surface shadow-xl"
            style={desktopPanelStyle}
          >
            {renderResults(`${searchId}-desktop-listbox`, `${searchId}-desktop-option`)}
          </div>
        ) : null}
      </div>
    </>,
    document.body,
  ) : null;

  return (
    <div ref={containerRef} className="relative">
      {/* Mobile: icon button that opens the search */}
      <button
        onClick={() => {
          setOpen(!open);
          if (open) setActiveResultIndex(-1);
        }}
        className="md:hidden p-1.5 rounded border border-border bg-input text-secondary hover:text-foreground hover:border-accent"
        title="Search"
      >
        <Search className="h-4 w-4" />
      </button>

      {/* Desktop: always-visible search input */}
      <div className="relative z-[60] hidden md:block">
        <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
        <input
          value={term}
          onChange={(event) => {
            setTerm(event.target.value);
            setOpen(true);
            setActiveResultIndex(-1);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={handleKeyDown}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={open}
          aria-controls={`${searchId}-desktop-listbox`}
          aria-activedescendant={activeResultIndex >= 0 ? `${searchId}-desktop-option-${activeResultIndex}` : undefined}
          aria-label="Search all..."
          placeholder="Search all..."
          className="w-72 rounded-lg border border-border bg-input py-1.5 pl-9 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
        />
      </div>

      {open && <div className="pointer-events-none fixed inset-0 z-50 bg-black/60" />}
      {renderedPanel}
    </div>
  );
}
