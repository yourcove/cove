import { useState, useRef, useEffect, useCallback, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { videos, performers, studios, tags, galleries, groups, savedFilters } from "../api/client";
import type { AffinityHostType, EntityEngagement, Video, Performer, Studio, Tag, Gallery, Group, SavedFilter, FindFilter } from "../api/types";
import { formatDuration, formatFileSize, getResolutionLabel, RatingBadge } from "../components/shared";
import { RatingBanner } from "../components/Rating";
import { ChevronLeft, ChevronRight, Settings2, Plus, Trash2, Film, User, Building2, Tag as TagIcon, Images, Clapperboard, GripVertical, Headphones, Layers } from "lucide-react";
import { createRouteLinkProps } from "../components/cardNavigation";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { readAuthenticatedUserHomePageContent, updateAuthenticatedUserUiPreferences } from "../utils/userUiPreferences";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { withSeededRandomSort } from "../utils/seededRandomSort";

// ─── Types ───────────────────────────────────────────────────────────────────

type FilterMode = "videos" | "performers" | "studios" | "tags" | "galleries" | "groups";

interface CustomFilter {
  type: "custom";
  mode: FilterMode;
  sortBy: string;
  direction: "asc" | "desc";
  header: string;
}

interface SavedFilterRow {
  type: "saved";
  savedFilterId: number;
}

interface ContinueWatchingRowConfig {
  type: "continueWatching";
}

type FrontPageContent = CustomFilter | SavedFilterRow | ContinueWatchingRowConfig;

const DEFAULT_SORT_BY_MODE: Record<FilterMode, string> = {
  videos: "date",
  performers: "latest_video_date",
  studios: "latest_video_date",
  tags: "latest_video_date",
  galleries: "date",
  groups: "date",
};

function normalizeFilterMode(mode: string | undefined): FilterMode | null {
  const normalized = mode?.toLowerCase();
  if (
    normalized === "videos" ||
    normalized === "performers" ||
    normalized === "studios" ||
    normalized === "tags" ||
    normalized === "galleries" ||
    normalized === "groups"
  ) {
    return normalized;
  }
  return null;
}

function parseJsonObject<T extends object>(json: string | undefined): T | undefined {
  if (!json) return undefined;
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as T : undefined;
  } catch {
    return undefined;
  }
}

// ─── Default content (matches standard defaults) ───────────────────────

const DEFAULT_CONTENT: FrontPageContent[] = [
  { type: "continueWatching" },
  { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recently Released Videos" },
  { type: "custom", mode: "studios", sortBy: "created_at", direction: "desc", header: "Recently Added Studios" },
  { type: "custom", mode: "groups", sortBy: "date", direction: "desc", header: "Recently Released Groups" },
  { type: "custom", mode: "performers", sortBy: "created_at", direction: "desc", header: "Recently Added Performers" },
  { type: "custom", mode: "galleries", sortBy: "date", direction: "desc", header: "Recently Released Galleries" },
];

// ─── Premade filter options (for adding new rows) ────────────────────────────

const PREMADE_FILTERS: CustomFilter[] = [
  { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recently Released Videos" },
  { type: "custom", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
  { type: "custom", mode: "galleries", sortBy: "date", direction: "desc", header: "Recently Released Galleries" },
  { type: "custom", mode: "galleries", sortBy: "created_at", direction: "desc", header: "Recently Added Galleries" },
  { type: "custom", mode: "groups", sortBy: "date", direction: "desc", header: "Recently Released Groups" },
  { type: "custom", mode: "groups", sortBy: "created_at", direction: "desc", header: "Recently Added Groups" },
  { type: "custom", mode: "studios", sortBy: "created_at", direction: "desc", header: "Recently Added Studios" },
  { type: "custom", mode: "performers", sortBy: "created_at", direction: "desc", header: "Recently Added Performers" },
];

const STORAGE_KEY = "cove-front-page-content";
// One-time flag so we add the Continue Watching row to pre-existing layouts exactly once.
// After this, the user is free to remove it and it won't be re-added.
const CONTINUE_WATCHING_MIGRATION_KEY = "cove-front-page-continue-watching-migrated";

function loadContent(): FrontPageContent[] {
  try {
    // Prefer the user's account-stored layout (follows them across browsers); fall back to the
    // browser-local value for signed-out use and as a one-time migration source.
    const stored = readAuthenticatedUserHomePageContent() ?? localStorage.getItem(STORAGE_KEY);
    if (stored) {
      const content = JSON.parse(stored) as FrontPageContent[];
      // Migrate layouts saved before Continue Watching became a customizable row: it used to be
      // hardcoded at the top, so preserve that behavior by inserting it once.
      const migrated = localStorage.getItem(CONTINUE_WATCHING_MIGRATION_KEY) === "true";
      if (!migrated) {
        localStorage.setItem(CONTINUE_WATCHING_MIGRATION_KEY, "true");
        if (!content.some((item) => item.type === "continueWatching")) {
          return [{ type: "continueWatching" }, ...content];
        }
      }
      return content;
    }
  } catch { /* ignore */ }
  return DEFAULT_CONTENT;
}

function saveContent(content: FrontPageContent[]) {
  const json = JSON.stringify(content);
  // Browser-local copy (fallback / signed-out), plus the account-backed copy when signed in.
  localStorage.setItem(STORAGE_KEY, json);
  updateAuthenticatedUserUiPreferences((current) => ({ ...(current ?? {}), homePageContent: json }));
}

// ─── Home Page Component ─────────────────────────────────────────────────────

interface Props {
  onNavigate: (r: any) => void;
}

export function HomePage({ onNavigate }: Props) {
  const [content, setContent] = useState<FrontPageContent[]>(loadContent);
  const [isEditing, setIsEditing] = useState(false);

  const updateContent = useCallback((newContent: FrontPageContent[]) => {
    setContent(newContent);
    saveContent(newContent);
  }, []);

  if (isEditing) {
    return (
      <FrontPageEditor
        content={content}
        onSave={(c) => { updateContent(c); setIsEditing(false); }}
        onCancel={() => setIsEditing(false)}
      />
    );
  }

  return (
    <div className="space-y-6">
      {content.map((item, i) => (
        item.type === "continueWatching"
          ? <ContinueWatchingRow key={i} onNavigate={onNavigate} />
          : <RecommendationRow key={i} content={item} onNavigate={onNavigate} />
      ))}
      <div className="flex justify-end pb-4">
        <button
          onClick={() => setIsEditing(true)}
          className="px-4 py-2 text-sm bg-card border border-border rounded hover:bg-surface text-foreground"
        >
          Customize
        </button>
      </div>
    </div>
  );
}

function ContinueWatchingRow({ onNavigate }: { onNavigate: (r: any) => void }) {
  const { data: groupData } = useQuery({
    queryKey: ["front-page-continue-watching-group"],
    queryFn: () => groups.find({ page: 1, perPage: 100, sort: "name", direction: "asc" }),
  });
  const continueGroup = groupData?.items.find((group) => group.querySourceKey === "continue-watching");
  const { data: items = [], isLoading } = useQuery({
    queryKey: ["front-page-continue-watching", continueGroup?.id],
    queryFn: () => groups.items.list(continueGroup!.id),
    enabled: !!continueGroup,
  });
  const playableItems = items.filter((item) => item.hostType === "video" || item.hostType === "audio" || item.hostType === "segment").slice(0, 12);
  if (!isLoading && playableItems.length === 0) return null;

  return (
    <RecommendationRowShell header="Continue Watching" viewAllPage="groups" onNavigate={onNavigate} loading={isLoading} count={playableItems.length}>
      {playableItems.map((item) => (
        <ContinueWatchingCard key={`${item.groupId}-${item.id}`} item={item} onNavigate={onNavigate} />
      ))}
    </RecommendationRowShell>
  );
}

function ContinueWatchingCard({ item, onNavigate }: { item: { hostType?: string; hostId?: number; videoId?: number | null; videoTitle?: string; title?: string; startSec?: number }; onNavigate: (r: any) => void }) {
  const hostType = item.hostType ?? "video";
  const hostId = item.hostId ?? item.videoId ?? 0;
  const videoId = item.videoId ?? (hostType === "video" ? hostId : 0);
  const title = item.title || item.videoTitle || "Untitled";
  const route = hostType === "audio"
    ? { page: "audio", id: hostId }
    : hostType === "segment"
      ? { page: "segment", id: hostId }
      // Only pass an explicit seekTo when we actually have a position (segments carry startSec).
      // Continue-watching video items have no startSec, so omit it and let VideoDetailPage resume
      // from the engagement resumeTime — passing seekTo: 0 would force playback back to the start.
      : item.startSec && item.startSec > 0
        ? { page: "video", id: videoId, seekTo: item.startSec }
        : { page: "video", id: videoId };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[220px] cursor-pointer overflow-hidden rounded border border-border bg-card transition-colors hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-black">
        {videoId > 0 ? (
          <img src={`/api/stream/video/${videoId}/screenshot`} alt={title} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-accent">
            {hostType === "audio" ? <Headphones className="h-10 w-10" /> : <Layers className="h-10 w-10" />}
          </div>
        )}
        <div className="absolute bottom-1 left-1 rounded bg-black/70 px-1.5 py-0.5 text-xs text-white">
          Resume
        </div>
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
      </div>
    </a>
  );
}

// ─── Recommendation Row (dispatcher) ────────────────────────────────────────

function RecommendationRow({ content, onNavigate }: { content: FrontPageContent; onNavigate: (r: any) => void }) {
  if (content.type === "continueWatching") {
    return <ContinueWatchingRow onNavigate={onNavigate} />;
  }
  if (content.type === "saved") {
    return <SavedFilterRecommendationRow savedFilterId={content.savedFilterId} onNavigate={onNavigate} />;
  }
  return <CustomFilterRecommendationRow filter={content} onNavigate={onNavigate} />;
}

// ─── Custom Filter Row ──────────────────────────────────────────────────────

function CustomFilterRecommendationRow({ filter, onNavigate }: { filter: CustomFilter; onNavigate: (r: any) => void }) {
  const findFilter = useMemo(
    () => withSeededRandomSort({}, { perPage: 25, sort: filter.sortBy, direction: filter.direction }),
    [filter],
  );

  const fetchFn = useMemo((): (() => Promise<any>) => {
    switch (filter.mode) {
      case "videos": return () => videos.find(findFilter);
      case "performers": return () => performers.find(findFilter);
      case "studios": return () => studios.find(findFilter);
      case "tags": return () => tags.find(findFilter);
      case "galleries": return () => galleries.find(findFilter);
      case "groups": return () => groups.find(findFilter);
    }
  }, [filter.mode, findFilter]);

  const { data, isLoading } = useQuery<any>({
    queryKey: ["front-page", filter.mode, findFilter],
    queryFn: fetchFn,
  });

  const items = data?.items ?? [];
  const engagementHostType = getRecommendationEngagementHostType(filter.mode);
  const { engagementById } = useEntityEngagementBatch(engagementHostType ?? "video", engagementHostType ? items.map((item: any) => item.id) : []);
  if (!isLoading && items.length === 0) return null;

  return (
    <RecommendationRowShell
      header={filter.header}
      viewAllPage={filter.mode}
      onNavigate={onNavigate}
      loading={isLoading}
      count={items.length}
    >
      {items.map((item: any) => (
        <EntityCard key={item.id} item={item} engagement={engagementById.get(item.id)} mode={filter.mode} onNavigate={onNavigate} />
      ))}
    </RecommendationRowShell>
  );
}

// ─── Saved Filter Row ───────────────────────────────────────────────────────

function SavedFilterRecommendationRow({ savedFilterId, onNavigate }: { savedFilterId: number; onNavigate: (r: any) => void }) {
  const { data: filter } = useQuery({
    queryKey: ["saved-filter", savedFilterId],
    queryFn: () => savedFilters.get(savedFilterId),
  });

  const mode = normalizeFilterMode(filter?.mode);
  const parsedFilter = useMemo(() => parseJsonObject<FindFilter>(filter?.findFilter) ?? {}, [filter?.findFilter]);
  const parsedObjectFilter = useMemo(() => parseJsonObject<Record<string, unknown>>(filter?.objectFilter), [filter?.objectFilter]);
  const parsedUIOptions = useMemo(() => parseJsonObject<Record<string, unknown>>(filter?.uiOptions), [filter?.uiOptions]);
  const hasObjectFilter = !!parsedObjectFilter && Object.keys(parsedObjectFilter).length > 0;
  const findFilter = useMemo((): FindFilter | undefined => {
    if (!mode) return undefined;
    return withSeededRandomSort({}, {
      ...parsedFilter,
      page: 1,
      perPage: 25,
      sort: parsedFilter.sort ?? DEFAULT_SORT_BY_MODE[mode],
      direction: parsedFilter.direction ?? "desc",
    });
  }, [mode, parsedFilter]);

  const fetchFn = useMemo((): (() => Promise<any>) => {
    if (!mode) return () => Promise.resolve({ items: [], totalCount: 0 });
    const fetchMap: Record<string, () => Promise<any>> = {
      videos: hasObjectFilter ? () => videos.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => videos.find(findFilter),
      performers: hasObjectFilter ? () => performers.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => performers.find(findFilter),
      studios: hasObjectFilter ? () => studios.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => studios.find(findFilter),
      tags: hasObjectFilter ? () => tags.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => tags.find(findFilter),
      galleries: hasObjectFilter ? () => galleries.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => galleries.find(findFilter),
      groups: hasObjectFilter ? () => groups.findFiltered({ findFilter, objectFilter: parsedObjectFilter }) : () => groups.find(findFilter),
    };
    return fetchMap[mode] ?? (() => Promise.resolve({ items: [], totalCount: 0 }));
  }, [mode, findFilter, parsedObjectFilter, hasObjectFilter]);

  const { data, isLoading } = useQuery<any>({
    queryKey: ["front-page-saved", savedFilterId, mode, findFilter, parsedObjectFilter],
    queryFn: fetchFn,
    enabled: !!mode,
  });

  const items = (data as any)?.items ?? [];
  const engagementHostType = getRecommendationEngagementHostType(mode ?? undefined);
  const { engagementById } = useEntityEngagementBatch(engagementHostType ?? "video", engagementHostType ? items.map((item: any) => item.id) : []);
  if (!filter || !mode || (!isLoading && items.length === 0)) return null;

  return (
    <RecommendationRowShell
      header={filter.name}
      viewAllPage={mode ?? "videos"}
      viewAllFilter={{
        ...parsedFilter,
        q: parsedFilter.q ?? "",
        page: 1,
        sort: parsedFilter.sort ?? DEFAULT_SORT_BY_MODE[mode],
        direction: parsedFilter.direction ?? "desc",
      }}
      viewAllObjectFilter={parsedObjectFilter ?? {}}
      viewAllView={typeof parsedUIOptions?.displayMode === "string" ? parsedUIOptions.displayMode : undefined}
      onNavigate={onNavigate}
      loading={isLoading}
      count={items.length}
    >
      {items.map((item: any) => (
        <EntityCard key={item.id} item={item} engagement={engagementById.get(item.id)} mode={mode!} onNavigate={onNavigate} />
      ))}
    </RecommendationRowShell>
  );
}

// ─── Recommendation Row Shell (horizontal carousel) ─────────────────────────

function RecommendationRowShell({
  header,
  viewAllPage,
  viewAllFilter,
  viewAllObjectFilter,
  viewAllView,
  onNavigate,
  loading,
  count,
  children,
}: {
  header: string;
  viewAllPage: string;
  viewAllFilter?: FindFilter;
  viewAllObjectFilter?: Record<string, unknown>;
  viewAllView?: string;
  onNavigate: (r: any) => void;
  loading: boolean;
  count: number;
  children: React.ReactNode;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(false);
  const [currentPage, setCurrentPage] = useState(0);
  const [totalPages, setTotalPages] = useState(1);

  const updateScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    setCanScrollLeft(el.scrollLeft > 5);
    setCanScrollRight(el.scrollLeft + el.clientWidth < el.scrollWidth - 5);
    // Calculate pages
    if (el.clientWidth > 0) {
      const pages = Math.ceil(el.scrollWidth / el.clientWidth);
      setTotalPages(pages);
      setCurrentPage(Math.round(el.scrollLeft / el.clientWidth));
    }
  }, []);

  useEffect(() => {
    updateScrollState();
    const el = scrollRef.current;
    if (el) {
      el.addEventListener("scroll", updateScrollState);
      const resizeObserver = new ResizeObserver(updateScrollState);
      resizeObserver.observe(el);
      return () => { el.removeEventListener("scroll", updateScrollState); resizeObserver.disconnect(); };
    }
  }, [updateScrollState, count]);

  const scroll = (dir: "left" | "right") => {
    const el = scrollRef.current;
    if (!el) return;
    const scrollAmount = el.clientWidth * 0.85;
    el.scrollBy({ left: dir === "left" ? -scrollAmount : scrollAmount, behavior: "smooth" });
  };

  return (
    <div className="recommendation-row">
      {/* Header */}
      <div className="flex items-center justify-between mb-2 px-1">
        <h2 className="text-base font-semibold text-foreground">{header}</h2>
        <button
          onClick={() => onNavigate({
            page: viewAllPage,
            ...(viewAllFilter ? { listFilter: viewAllFilter } : {}),
            ...(viewAllObjectFilter !== undefined ? { listObjectFilter: viewAllObjectFilter } : {}),
            ...(viewAllView ? { listView: viewAllView } : {}),
          })}
          className="inline-flex min-h-9 items-center rounded-md px-2 text-sm text-muted hover:text-accent sm:min-h-0 sm:px-0 sm:text-xs"
        >
          View All
        </button>
      </div>

      {/* Scrollable cards */}
      <div className="relative group/row">
        {/* Left arrow */}
        {canScrollLeft && (
          <button
            onClick={() => scroll("left")}
            className="absolute left-0 top-0 bottom-0 z-20 w-8 flex items-center justify-center bg-gradient-to-r from-background/90 to-transparent opacity-0 group-hover/row:opacity-100 transition-opacity"
          >
            <ChevronLeft className="w-6 h-6 text-white" />
          </button>
        )}

        <div
          ref={scrollRef}
          className="flex gap-2 overflow-x-auto scrollbar-hide scroll-smooth px-1"
          style={{ scrollSnapType: "x mandatory" }}
        >
          {loading
            ? Array.from({ length: 6 }).map((_, i) => (
                <div key={i} className="flex-shrink-0 w-[200px] aspect-video bg-card rounded animate-pulse" />
              ))
            : children}
        </div>

        {/* Right arrow */}
        {canScrollRight && (
          <button
            onClick={() => scroll("right")}
            className="absolute right-0 top-0 bottom-0 z-20 w-8 flex items-center justify-center bg-gradient-to-l from-background/90 to-transparent opacity-0 group-hover/row:opacity-100 transition-opacity"
          >
            <ChevronRight className="w-6 h-6 text-white" />
          </button>
        )}
      </div>

      {/* Page dots */}
      {totalPages > 1 && (
        <div className="flex justify-center gap-1.5 mt-2">
          {Array.from({ length: totalPages }).map((_, i) => (
            <button
              key={i}
              onClick={() => {
                const el = scrollRef.current;
                if (el) el.scrollTo({ left: i * el.clientWidth, behavior: "smooth" });
              }}
              className="flex h-8 w-8 items-center justify-center rounded-full sm:h-1 sm:w-6"
              aria-label={`Go to carousel page ${i + 1}`}
            >
              <span className={`h-1.5 w-6 rounded-full transition-colors sm:h-full sm:w-full ${i === currentPage ? "bg-foreground" : "bg-muted/40"}`} />
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// ─── Entity Card (renders appropriate card based on mode) ───────────────────

function EntityCard({ item, engagement, mode, onNavigate }: { item: any; engagement?: EntityEngagement; mode: FilterMode; onNavigate: (r: any) => void }) {
  switch (mode) {
    case "videos": return <VideoRecommendationCard video={item} engagement={engagement} onNavigate={onNavigate} />;
    case "performers": return <PerformerRecommendationCard performer={item} engagement={engagement} onNavigate={onNavigate} />;
    case "studios": return <StudioRecommendationCard studio={item} engagement={engagement} onNavigate={onNavigate} />;
    case "tags": return <TagRecommendationCard tag={item} onNavigate={onNavigate} />;
    case "galleries": return <GalleryRecommendationCard gallery={item} engagement={engagement} onNavigate={onNavigate} />;
    case "groups": return <GroupRecommendationCard group={item} engagement={engagement} onNavigate={onNavigate} />;
    default: return null;
  }
}

// ─── Video Card ─────────────────────────────────────────────────────────────

function VideoRecommendationCard({ video, engagement, onNavigate }: { video: Video; engagement?: EntityEngagement; onNavigate: (r: any) => void }) {
  const file = video.files[0];
  const duration = file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const screenshotUrl = videos.screenshotUrl(video.id);
  const screenshotAlt = video.imagePath ? video.title || "" : "";
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, () => onNavigate({ page: "video", id: video.id }));
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-black">
        <img src={screenshotUrl} alt={screenshotAlt} className="w-full h-full object-cover" loading="lazy" />
        {/* Resolution + duration overlay */}
        <div className="absolute bottom-0 right-0 flex items-center gap-0.5 p-1 text-xs text-white">
          {resLabel && <span className="bg-black/70 px-1 py-0.5 rounded font-bold">{resLabel}</span>}
          {duration > 0 && <span className="bg-black/70 px-1 py-0.5 rounded">{formatDuration(duration)}</span>}
        </div>
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">
          {video.title || file?.basename || "Untitled"}
        </p>
        {video.date && <p className="text-xs text-muted">{video.date}</p>}
      </div>
      {/* Bottom stats */}
      <div className="flex items-center gap-2 px-2 pb-1.5 text-xs text-muted">
        {video.tags.length > 0 && (
          <span className="flex items-center gap-0.5"><TagIcon className="w-2.5 h-2.5" />{video.tags.length}</span>
        )}
        {video.performers.length > 0 && (
          <span className="flex items-center gap-0.5"><User className="w-2.5 h-2.5" />{video.performers.length}</span>
        )}
      </div>
    </a>
  );
}

// ─── Performer Card ─────────────────────────────────────────────────────────

function PerformerRecommendationCard({ performer, engagement, onNavigate }: { performer: Performer; engagement?: EntityEngagement; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "performer", id: performer.id }, () => onNavigate({ page: "performer", id: performer.id }));
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-[2/3] bg-surface">
        {performer.imagePath ? (
          <img src={performer.imagePath} alt={performer.name} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <div className="w-full h-full flex items-center justify-center">
            <User className="w-10 h-10 text-muted" />
          </div>
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{performer.name}</p>
        {performer.disambiguation && <p className="text-xs text-muted truncate">{performer.disambiguation}</p>}
      </div>
    </a>
  );
}

// ─── Studio Card ────────────────────────────────────────────────────────────

function StudioRecommendationCard({ studio, engagement, onNavigate }: { studio: Studio; engagement?: EntityEngagement; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "studio", id: studio.id }, () => onNavigate({ page: "studio", id: studio.id }));
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center p-4">
        {studio.imagePath ? (
          <img src={studio.imagePath} alt={studio.name} className="h-full w-full object-contain" loading="lazy" />
        ) : (
          <Building2 className="w-10 h-10 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{studio.name}</p>
      </div>
    </a>
  );
}

// ─── Tag Card ───────────────────────────────────────────────────────────────

function TagRecommendationCard({ tag, onNavigate }: { tag: Tag; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "tag", id: tag.id }, () => onNavigate({ page: "tag", id: tag.id }));

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center">
        {tag.imagePath ? (
          <img src={tag.imagePath} alt={tag.name} className="max-w-full max-h-full object-contain" loading="lazy" />
        ) : (
          <TagIcon className="w-8 h-8 text-muted" />
        )}
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{tag.name}</p>
      </div>
    </a>
  );
}

// ─── Gallery Card ───────────────────────────────────────────────────────────

function GalleryRecommendationCard({ gallery, engagement, onNavigate }: { gallery: Gallery; engagement?: EntityEngagement; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "gallery", id: gallery.id }, () => onNavigate({ page: "gallery", id: gallery.id }));
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center">
        {gallery.coverPath ? (
          <img src={`/api/galleries/${gallery.id}/cover`} alt={gallery.title || ""} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <Images className="w-8 h-8 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{getGalleryDisplayTitle(gallery)}</p>
        {gallery.date && <p className="text-xs text-muted">{gallery.date}</p>}
      </div>
    </a>
  );
}

// ─── Group Card ─────────────────────────────────────────────────────────────

function GroupRecommendationCard({ group, engagement, onNavigate }: { group: Group; engagement?: EntityEngagement; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "group", id: group.id }, () => onNavigate({ page: "group", id: group.id }));
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-[2/3] bg-surface flex items-center justify-center">
        {group.frontImagePath ? (
          <img src={group.frontImagePath} alt={group.name} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <Clapperboard className="w-8 h-8 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{group.name}</p>
        {group.date && <p className="text-xs text-muted">{group.date}</p>}
      </div>
    </a>
  );
}

function getRecommendationEngagementHostType(mode: FilterMode | undefined): AffinityHostType | null {
  switch (mode) {
    case "videos":
      return "video";
    case "performers":
      return "performer";
    case "studios":
      return "studio";
    case "galleries":
      return "gallery";
    case "groups":
      return "group";
    default:
      return null;
  }
}

// ─── Front Page Editor ──────────────────────────────────────────────────────

function FrontPageEditor({
  content,
  onSave,
  onCancel,
}: {
  content: FrontPageContent[];
  onSave: (content: FrontPageContent[]) => void;
  onCancel: () => void;
}) {
  const [items, setItems] = useState<FrontPageContent[]>([...content]);
  const [showAddModal, setShowAddModal] = useState(false);

  const { data: allSavedFilters } = useQuery({
    queryKey: ["saved-filters-all"],
    queryFn: () => savedFilters.list(),
  });

  const savedFilterById = useMemo(() => new Map(allSavedFilters?.map((filter) => [filter.id, filter] as const) ?? []), [allSavedFilters]);

  const moveItem = (fromIndex: number, toIndex: number) => {
    if (toIndex < 0 || toIndex >= items.length) return;
    const newItems = [...items];
    const [moved] = newItems.splice(fromIndex, 1);
    newItems.splice(toIndex, 0, moved);
    setItems(newItems);
  };

  const removeItem = (index: number) => {
    setItems(items.filter((_, i) => i !== index));
  };

  const addItem = (item: FrontPageContent) => {
    setItems([...items, item]);
    setShowAddModal(false);
  };

  return (
    <div className="max-w-3xl mx-auto">
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-xl font-semibold text-foreground">Customize Front Page</h1>
        <div className="flex gap-2">
          <button onClick={onCancel} className="px-4 py-2 text-sm text-muted hover:text-foreground">
            Cancel
          </button>
          <button
            onClick={() => onSave(items)}
            className="px-4 py-2 text-sm bg-accent text-white rounded hover:bg-accent-hover"
          >
            Save
          </button>
        </div>
      </div>

      <div className="space-y-2">
        {items.map((item, i) => (
          <div key={i} className="flex items-center gap-2 p-3 bg-card border border-border rounded">
            <div className="flex flex-col gap-0.5">
              <button
                onClick={() => moveItem(i, i - 1)}
                disabled={i === 0}
                className="text-muted hover:text-foreground disabled:opacity-30"
              >
                <ChevronLeft className="w-4 h-4 rotate-90" />
              </button>
              <button
                onClick={() => moveItem(i, i + 1)}
                disabled={i === items.length - 1}
                className="text-muted hover:text-foreground disabled:opacity-30"
              >
                <ChevronRight className="w-4 h-4 rotate-90" />
              </button>
            </div>
            <GripVertical className="w-4 h-4 text-muted" />
            <div className="flex-1">
              <p className="text-sm text-foreground">
                {item.type === "custom" ? item.header : item.type === "continueWatching" ? "Continue Watching" : getSavedFilterLabel(item, savedFilterById)}
              </p>
              <p className="text-xs text-muted">
                {item.type === "custom" ? `${item.mode} • ${item.sortBy} • ${item.direction}` : item.type === "continueWatching" ? "Premade filter" : "Saved filter"}
              </p>
            </div>
            <button onClick={() => removeItem(i)} className="text-red-400 hover:text-red-300 p-1">
              <Trash2 className="w-4 h-4" />
            </button>
          </div>
        ))}
      </div>

      <button
        onClick={() => setShowAddModal(true)}
        className="mt-4 flex items-center gap-2 px-4 py-2 text-sm text-accent hover:text-accent-hover border border-border rounded hover:border-accent/50"
      >
        <Plus className="w-4 h-4" />
        Add Row
      </button>

      {/* Add Row Modal */}
      {showAddModal && (
        <div className="fixed inset-0 bg-black/70 flex items-center justify-center z-50" onClick={() => setShowAddModal(false)}>
          <div className="bg-surface border border-border rounded-lg p-6 max-w-md w-full mx-4 max-h-[80vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
            <h3 className="text-lg font-semibold text-foreground mb-4">Add Content Row</h3>

            <h4 className="text-sm font-medium text-muted mb-2">Premade Filters</h4>
            <div className="space-y-1 mb-4">
              {!items.some((item) => item.type === "continueWatching") && (
                <button
                  onClick={() => addItem({ type: "continueWatching" })}
                  className="block w-full text-left px-3 py-2 text-sm text-foreground hover:bg-card rounded"
                >
                  Continue Watching
                </button>
              )}
              {PREMADE_FILTERS.map((f, i) => (
                <button
                  key={i}
                  onClick={() => addItem(f)}
                  className="block w-full text-left px-3 py-2 text-sm text-foreground hover:bg-card rounded"
                >
                  {f.header}
                </button>
              ))}
            </div>

            {allSavedFilters && allSavedFilters.length > 0 && (
              <>
                <h4 className="text-sm font-medium text-muted mb-2">Saved Filters</h4>
                <div className="space-y-1">
                  {allSavedFilters.map((sf) => (
                    <button
                      key={sf.id}
                      onClick={() => addItem({ type: "saved", savedFilterId: sf.id })}
                      className="block w-full text-left px-3 py-2 text-sm text-foreground hover:bg-card rounded"
                    >
                      <span className="text-muted text-xs mr-2">{sf.mode}:</span>
                      {sf.name}
                    </button>
                  ))}
                </div>
              </>
            )}

            <div className="flex justify-end mt-4">
              <button onClick={() => setShowAddModal(false)} className="px-4 py-2 text-sm text-muted hover:text-foreground">
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function getSavedFilterLabel(item: SavedFilterRow, savedFilterById: Map<number, SavedFilter>) {
  return savedFilterById.get(item.savedFilterId)?.name ?? `Saved Filter #${item.savedFilterId}`;
}
