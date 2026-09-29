import { useQuery } from "@tanstack/react-query";
import { Heart, Play, Search, Users, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { performers } from "../../api/client";
import type { PerformerPairingCoStar, PerformerPairingVideo } from "../../api/types";
import {
  useDetailBooleanUrlState,
  useDetailStringUrlState,
  useRememberDetailTabUrlParams,
} from "../../hooks/useDetailListUrlState";
import { useRegisterKeyboardActions, type KeyboardActionRegistration } from "../../keyboard/KeyboardShortcutProvider";
import { buildCurrentUrl, navigateToUrl } from "../../router/location";
import { videoQueueItem } from "../../hooks/useVideoQueueNavigation";
import { useOptionalAppConfig } from "../../state/AppConfigContext";
import { useOptionalVideoQueue } from "../../state/VideoQueueContext";
import {
  buildPairings,
  facetPairings,
  filterPairings,
  isPairingSort,
  isPairingTier,
  lineupVideos,
  PAIRING_PER_PAGE_DEFAULT,
  PAIRING_SORT_OPTIONS,
  PAIRING_TAB_KEY,
  PAIRING_TIERS,
  PAIRING_URL_PARAMS,
  parseIdList,
  parsePositiveInt,
  sharedVideosRoute,
  sortPairings,
  summarizePairings,
  UNKNOWN_GENDER,
} from "../../utils/performerPairings";
import { getLoadError } from "../../utils/queryLoadState";
import { performerGenderLabel } from "../EntityCards";
import { ListLoadError } from "../ListLoadError";
import { toolbarSelectClass } from "../listToolbarStyles";
import { PageSizeSelect } from "../PageSizeSelect";
import { PaginationControls } from "../PaginationControls";
import { CoStarPortrait, FilterChip, RouteLink, type PairingNavigate } from "./pairingParts";
import { RankedPairingsView } from "./RankedPairingsView";

interface PerformerPairingsPanelProps {
  performer: { id: number; name: string };
  onNavigate: PairingNavigate;
}

// Sets or removes the given parameters in one URL change and goes back to the first page, since
// the page the viewer was on means nothing once the list is filtered or ordered differently. Picking
// a choice that is already made changes nothing, so the page stays.
function changeListParams(changes: Record<string, string | null>) {
  const params = new URLSearchParams(window.location.search);
  let changed = false;
  for (const [key, value] of Object.entries(changes)) {
    if ((params.get(key) || null) === (value || null)) continue;
    changed = true;
    if (value) params.set(key, value);
    else params.delete(key);
  }
  if (!changed) return;
  params.delete(PAIRING_URL_PARAMS.page);
  navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
}

export function PerformerPairingsPanel({ performer, onNavigate }: PerformerPairingsPanelProps) {
  const [tierParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.tier);
  const [gender] = useDetailStringUrlState(PAIRING_URL_PARAMS.gender);
  const [sortParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.sort);
  const [duoOnly] = useDetailBooleanUrlState(PAIRING_URL_PARAMS.duoOnly);
  const [favoritesOnly] = useDetailBooleanUrlState(PAIRING_URL_PARAMS.favoritesOnly);
  const [queryParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.query);
  const [pageParam, setPageParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.page);
  const [perPageParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.perPage);
  const query = queryParam ?? "";
  // The box shows what is typed at once; the URL, and so the list, follow when typing pauses. When the
  // URL changes by other means (Back, Clear filters), the box takes the new search.
  const [draft, setDraft] = useState(query);
  const [draftSource, setDraftSource] = useState(query);
  if (query !== draftSource) {
    setDraftSource(query);
    if (draft.trim() !== query.trim()) setDraft(query);
  }
  useEffect(() => {
    if (draft.trim() === query.trim()) return;
    const timer = window.setTimeout(
      () => changeListParams({ [PAIRING_URL_PARAMS.query]: draft.trim() ? draft : null }),
      250,
    );
    return () => window.clearTimeout(timer);
  }, [draft, query]);
  // The lineup lives in the URL too, so it is still there after going back from its videos.
  const [lineupParam, setLineupParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.lineup);
  const selectedIds = useMemo(() => parseIdList(lineupParam), [lineupParam]);
  const tier = isPairingTier(tierParam) ? tierParam : null;
  const sort = isPairingSort(sortParam) ? sortParam : "together";

  const pairingsQuery = useQuery({
    queryKey: ["performer-appears-with", performer.id, "pairings"],
    queryFn: () => performers.pairings(performer.id),
    // Names, favorites and images come from the co-stars too; coming back from editing one refreshes them.
    refetchOnMount: "always",
  });
  const data = pairingsQuery.data;
  // A failed refetch keeps the list already shown; only a first load that fails shows the error.
  const loadError = getLoadError(data, pairingsQuery.error);
  const summary = useMemo(() => (data ? summarizePairings(data) : null), [data]);
  const allPairings = useMemo(() => (data ? buildPairings(data, duoOnly) : []), [data, duoOnly]);
  const pairings = useMemo(
    () => filterPairings(allPairings, { favoritesOnly, query }),
    [allPairings, favoritesOnly, query],
  );
  const facets = useMemo(() => facetPairings(pairings, { gender, tier }), [pairings, gender, tier]);
  const sorted = useMemo(() => sortPairings(facets.visible, sort), [facets.visible, sort]);
  const perPage = parsePositiveInt(perPageParam) ?? PAIRING_PER_PAGE_DEFAULT;
  const totalPages = Math.max(1, Math.ceil(sorted.length / perPage));
  // A page past the end, left by a refetch that dropped co-stars or by an edited URL, shows the last one.
  const urlPage = parsePositiveInt(pageParam);
  const page = Math.min(urlPage ?? 1, totalPages);
  const pagePairings = useMemo(() => sorted.slice((page - 1) * perPage, page * perPage), [page, perPage, sorted]);
  const goToPage = (nextPage: number) => {
    const target = Math.max(1, Math.min(totalPages, nextPage));
    setPageParam(target > 1 ? String(target) : null);
  };
  // Once the co-stars are in, the URL follows the clamped page, so a later refetch that adds co-stars
  // does not move the list to the page the URL still asked for.
  useEffect(() => {
    if (data && urlPage != null && urlPage !== page) setPageParam(page > 1 ? String(page) : null);
  }, [data, page, setPageParam, urlPage]);
  // The same paging keys as the other lists; the most recently mounted list handles them.
  const pagingKeyboardEnabled = Boolean(data) && totalPages > 1;
  const pagingKeyboardActions = useMemo<KeyboardActionRegistration[]>(() => {
    const goTo = (nextPage: number) => {
      const target = Math.max(1, Math.min(totalPages, nextPage));
      if (target !== page) setPageParam(target > 1 ? String(target) : null);
    };
    return [
      { id: "list.page.previous", action: () => goTo(page - 1) },
      { id: "list.page.next", action: () => goTo(page + 1) },
      { id: "list.page.back10", action: () => goTo(page - 10) },
      { id: "list.page.forward10", action: () => goTo(page + 10) },
      { id: "list.page.first", action: () => goTo(1) },
      { id: "list.page.last", action: () => goTo(totalPages) },
    ].map((registration) => ({ ...registration, surface: "list" as const, enabled: pagingKeyboardEnabled }));
  }, [page, pagingKeyboardEnabled, setPageParam, totalPages]);
  useRegisterKeyboardActions(pagingKeyboardActions);
  const tierTotal = facets.tierCounts.frequent + facets.tierCounts.recurring + facets.tierCounts.once;
  const genderTotal = facets.genderCounts.reduce((total, entry) => total + entry.count, 0);
  // The chosen gender keeps its chip at 0, so the filter stays visible and can be switched off.
  const genderChips =
    gender == null || facets.genderCounts.some((entry) => entry.gender === gender)
      ? facets.genderCounts
      : [...facets.genderCounts, { gender, count: 0 }];

  const coStarsById = useMemo(() => new Map((data?.coStars ?? []).map((coStar) => [coStar.id, coStar])), [data]);
  // A refetch can drop a ticked co-star (merged, deleted or hidden); the lineup then leaves them out.
  const lineupCoStars = useMemo(
    () =>
      selectedIds.map((id) => coStarsById.get(id)).filter((coStar): coStar is PerformerPairingCoStar => coStar != null),
    [coStarsById, selectedIds],
  );
  const lineupIds = useMemo(() => lineupCoStars.map((coStar) => coStar.id), [lineupCoStars]);
  const selectedSet = useMemo(() => new Set(lineupIds), [lineupIds]);
  const lineup = useMemo(
    () => (data ? lineupVideos(data, lineupIds, duoOnly) : { all: [], any: [] }),
    [data, lineupIds, duoOnly],
  );

  // setQueue is stable while the queue context object is not, and rows compare their callbacks.
  const setQueue = useOptionalVideoQueue()?.setQueue;
  const continuePlaylist = useOptionalAppConfig()?.config?.ui?.continuePlaylistDefault ?? false;
  const playVideos = useCallback(
    (list: PerformerPairingVideo[]) => {
      if (list.length === 0) return;
      const ids = list.map((video) => video.id);
      setQueue?.(
        ids,
        ids[0],
        list.map((video) => videoQueueItem(video)),
        { autoplay: continuePlaylist },
      );
      onNavigate({ page: "video", id: ids[0] });
    },
    [continuePlaylist, onNavigate, setQueue],
  );
  const setSelectedIds = useCallback(
    (ids: number[]) => setLineupParam(ids.length > 0 ? ids.join(",") : null),
    [setLineupParam],
  );
  // Reads the lineup from the URL rather than from render state, so the callback stays stable and
  // rows that did not change are not re-rendered on every tick.
  const toggleSelected = useCallback(
    (coStarId: number) => {
      // Co-stars no longer in the response (merged, deleted or hidden) drop out on the next tick.
      const current = parseIdList(new URLSearchParams(window.location.search).get(PAIRING_URL_PARAMS.lineup)).filter(
        (id) => coStarsById.has(id),
      );
      setSelectedIds(current.includes(coStarId) ? current.filter((id) => id !== coStarId) : [...current, coStarId]);
    },
    [coStarsById, setSelectedIds],
  );
  // One URL change for all of them; the lineup and the page size stay.
  const clearFilters = () => {
    const params = new URLSearchParams(window.location.search);
    for (const key of [
      PAIRING_URL_PARAMS.query,
      PAIRING_URL_PARAMS.tier,
      PAIRING_URL_PARAMS.gender,
      PAIRING_URL_PARAMS.duoOnly,
      PAIRING_URL_PARAMS.favoritesOnly,
      PAIRING_URL_PARAMS.page,
    ]) {
      params.delete(key);
    }
    if (navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true })) setDraft("");
  };

  // What the tab puts back when it is picked again after being left, the search being typed included.
  const rememberedParams = useMemo(() => {
    const params = new URLSearchParams();
    if (tier) params.set(PAIRING_URL_PARAMS.tier, tier);
    if (gender) params.set(PAIRING_URL_PARAMS.gender, gender);
    if (sort !== "together") params.set(PAIRING_URL_PARAMS.sort, sort);
    if (duoOnly) params.set(PAIRING_URL_PARAMS.duoOnly, "true");
    if (favoritesOnly) params.set(PAIRING_URL_PARAMS.favoritesOnly, "true");
    if (draft.trim()) params.set(PAIRING_URL_PARAMS.query, draft);
    const lineupToKeep = data ? lineupIds : selectedIds;
    if (lineupToKeep.length > 0) params.set(PAIRING_URL_PARAMS.lineup, lineupToKeep.join(","));
    // Before the co-stars load there is nothing to clamp the page against, so the URL's is kept. A search
    // still being typed would have gone back to the first page, so it comes back on the first page.
    const pageToKeep = draft.trim() !== query.trim() ? 1 : data ? page : (urlPage ?? 1);
    if (pageToKeep > 1) params.set(PAIRING_URL_PARAMS.page, String(pageToKeep));
    if (perPage !== PAIRING_PER_PAGE_DEFAULT) params.set(PAIRING_URL_PARAMS.perPage, String(perPage));
    return params.toString();
  }, [data, draft, duoOnly, favoritesOnly, gender, lineupIds, page, perPage, query, selectedIds, sort, tier, urlPage]);
  useRememberDetailTabUrlParams(PAIRING_TAB_KEY, rememberedParams);

  return (
    <div className="space-y-6">
      <section aria-label="Appears With filters" className="space-y-3 rounded-xl border border-border bg-card p-3">
        <div className="flex flex-wrap items-center gap-2">
          <FilterChip
            pressed={tier == null}
            onClick={() => changeListParams({ [PAIRING_URL_PARAMS.tier]: null })}
            title={
              summary ? `In ${summary.sharedVideoCount} of ${performer.name}’s ${summary.videoCount} videos` : undefined
            }
          >
            All {data ? <span className="text-muted">{tierTotal}</span> : null}
          </FilterChip>
          {PAIRING_TIERS.map((option) => (
            <FilterChip
              key={option.value}
              pressed={tier === option.value}
              onClick={() =>
                changeListParams({ [PAIRING_URL_PARAMS.tier]: tier === option.value ? null : option.value })
              }
              title={option.rule}
            >
              {option.label} {data ? <span className="text-muted">{facets.tierCounts[option.value]}</span> : null}
            </FilterChip>
          ))}
          <span className="flex-1" />
          <label className="flex min-h-9 w-64 max-w-full items-center gap-2 rounded-md border border-border/60 bg-input px-2.5 text-muted focus-within:border-accent">
            <Search aria-hidden="true" className="h-4 w-4 shrink-0" />
            <input
              type="search"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              placeholder="Search co-stars…"
              aria-label="Search co-stars"
              className="min-w-0 flex-1 bg-transparent py-1.5 text-sm text-foreground outline-none"
            />
          </label>
          <label className="flex items-center gap-2 text-xs text-muted">
            Sort
            <select
              value={sort}
              onChange={(event) =>
                changeListParams({
                  [PAIRING_URL_PARAMS.sort]: event.target.value === "together" ? null : event.target.value,
                })
              }
              className={toolbarSelectClass}
            >
              {PAIRING_SORT_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <PageSizeSelect
            perPage={perPage}
            allowInfinite={false}
            infinitePageSize={false}
            onChange={(next) =>
              changeListParams({
                [PAIRING_URL_PARAMS.perPage]: next === PAIRING_PER_PAGE_DEFAULT ? null : String(next),
              })
            }
          />
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <FilterChip pressed={gender == null} onClick={() => changeListParams({ [PAIRING_URL_PARAMS.gender]: null })}>
            Everyone {data ? <span className="text-muted">{genderTotal}</span> : null}
          </FilterChip>
          {genderChips.map(({ gender: key, count }) => (
            <FilterChip
              key={key}
              pressed={gender === key}
              onClick={() => changeListParams({ [PAIRING_URL_PARAMS.gender]: gender === key ? null : key })}
            >
              {key === UNKNOWN_GENDER ? "No gender set" : (performerGenderLabel(key) ?? key)}{" "}
              <span className="text-muted">{count}</span>
            </FilterChip>
          ))}
          <span aria-hidden="true" className="mx-1 h-5 w-px bg-border" />
          <FilterChip
            pressed={duoOnly}
            onClick={() => changeListParams({ [PAIRING_URL_PARAMS.duoOnly]: duoOnly ? null : "true" })}
            title="Count only videos with just the two of them"
          >
            Duos only
          </FilterChip>
          <FilterChip
            pressed={favoritesOnly}
            onClick={() => changeListParams({ [PAIRING_URL_PARAMS.favoritesOnly]: favoritesOnly ? null : "true" })}
          >
            <Heart aria-hidden="true" className="h-3 w-3" />
            Favorites
          </FilterChip>
        </div>
      </section>

      {loadError ? (
        <ListLoadError
          error={loadError}
          onRetry={() => {
            void pairingsQuery.refetch();
          }}
        />
      ) : !data ? (
        <div className="flex flex-col items-center justify-center py-12 text-muted">
          <Users aria-hidden="true" className="mb-3 h-10 w-10 animate-pulse" />
          <p>Loading co-stars…</p>
        </div>
      ) : data.coStars.length === 0 ? (
        <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-card/40 py-12 text-muted">
          <Users aria-hidden="true" className="mb-3 h-12 w-12 opacity-60" />
          <p>{performer.name} has no videos with other performers yet.</p>
        </div>
      ) : sorted.length === 0 ? (
        <div className="flex flex-col items-center justify-center gap-3 rounded-xl border border-dashed border-border bg-card/40 py-12 text-muted">
          <p>No co-stars match these filters.</p>
          <button
            type="button"
            onClick={clearFilters}
            className="rounded-lg border border-border bg-card px-4 py-2 text-sm font-semibold text-foreground hover:border-accent/60"
          >
            Clear filters
          </button>
        </div>
      ) : (
        <div className="space-y-3">
          {totalPages > 1 ? (
            <nav aria-label="Co-star pages" className="flex flex-wrap items-center justify-center gap-1">
              <PaginationControls page={page} totalPages={totalPages} goTo={goToPage} />
            </nav>
          ) : null}
          <RankedPairingsView
            performerId={performer.id}
            pairings={pagePairings}
            firstRank={(page - 1) * perPage + 1}
            duoOnly={duoOnly}
            careerFirstYear={summary?.firstYear ?? null}
            careerLastYear={summary?.lastYear ?? null}
            selectedIds={selectedSet}
            onToggleSelected={toggleSelected}
            onPlay={playVideos}
            onNavigate={onNavigate}
          />
          {totalPages > 1 ? (
            <nav aria-label="Co-star pages, bottom" className="flex flex-wrap items-center justify-center gap-1 py-2">
              <PaginationControls page={page} totalPages={totalPages} goTo={goToPage} />
            </nav>
          ) : null}
        </div>
      )}

      {lineupCoStars.length > 0 ? (
        <section
          aria-label="Lineup"
          className="sticky bottom-4 z-20 flex flex-wrap items-center gap-x-5 gap-y-3 rounded-xl border border-accent/50 bg-surface/95 px-4 py-3 shadow-2xl backdrop-blur"
        >
          <div className="flex min-w-0 flex-1 items-center gap-3">
            <div className="flex shrink-0 -space-x-2">
              {lineupCoStars.slice(0, 5).map((coStar) => (
                <CoStarPortrait
                  key={coStar.id}
                  coStar={coStar}
                  className="h-10 w-8 rounded-md border-2 border-surface"
                />
              ))}
            </div>
            <div className="min-w-0">
              <p className="text-[11px] font-bold uppercase tracking-wider text-accent">Lineup</p>
              <p className="truncate text-sm font-semibold text-foreground">
                {[performer.name, ...lineupCoStars.map((coStar) => coStar.name)].join(" + ")}
              </p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm text-secondary">
              {lineupIds.length === 1 ? "Together" : "All of them"}:{" "}
              <strong className="text-foreground">
                {lineup.all.length} {lineup.all.length === 1 ? "video" : "videos"}
              </strong>
            </span>
            {lineup.all.length > 0 ? (
              <>
                <RouteLink
                  route={sharedVideosRoute(performer.id, lineupIds, { match: "all", duoOnly })}
                  onNavigate={onNavigate}
                  className="inline-flex h-9 items-center rounded-lg bg-accent px-3 text-sm font-semibold text-white hover:bg-accent-hover"
                >
                  Open
                </RouteLink>
                <button
                  type="button"
                  onClick={() => playVideos(lineup.all)}
                  aria-label="Play the videos with all of them"
                  className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-border px-3 text-sm font-semibold text-foreground hover:border-accent/60"
                >
                  <Play aria-hidden="true" className="h-3.5 w-3.5 fill-current" />
                  Play
                </button>
              </>
            ) : (
              <span className="text-xs text-muted">
                {!duoOnly
                  ? "Remove someone to widen the lineup"
                  : lineupIds.length > 1
                    ? "Duos only is on, so no video has all of them"
                    : "Duos only is on, and they have no duos together"}
              </span>
            )}
          </div>
          {lineupIds.length > 1 ? (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm text-secondary">
                Any of them:{" "}
                <strong className="text-foreground">
                  {lineup.any.length} {lineup.any.length === 1 ? "video" : "videos"}
                </strong>
              </span>
              <RouteLink
                route={sharedVideosRoute(performer.id, lineupIds, { match: "any", duoOnly })}
                onNavigate={onNavigate}
                className="inline-flex h-9 items-center rounded-lg border border-border px-3 text-sm font-semibold text-foreground hover:border-accent/60"
              >
                Open
              </RouteLink>
            </div>
          ) : null}
          <button
            type="button"
            onClick={() => setSelectedIds([])}
            aria-label="Clear the lineup"
            className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-border text-secondary hover:text-foreground"
          >
            <X aria-hidden="true" className="h-4 w-4" />
          </button>
        </section>
      ) : null}
    </div>
  );
}
