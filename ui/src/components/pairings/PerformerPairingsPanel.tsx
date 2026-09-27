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
import { buildCurrentUrl, navigateToUrl } from "../../router/location";
import { videoQueueItem } from "../../hooks/useVideoQueueNavigation";
import { useOptionalAppConfig } from "../../state/AppConfigContext";
import { useOptionalVideoQueue } from "../../state/VideoQueueContext";
import {
  buildPairings,
  facetPairings,
  filterPairings,
  formatShownRows,
  isPairingSort,
  isPairingTier,
  lineupVideos,
  PAIRING_ROWS_FIRST,
  PAIRING_ROWS_STEP,
  PAIRING_SORT_OPTIONS,
  PAIRING_TAB_KEY,
  PAIRING_TIERS,
  PAIRING_URL_PARAMS,
  parseIdList,
  parseShownRows,
  sharedVideosRoute,
  sortPairings,
  summarizePairings,
  UNKNOWN_GENDER,
  type PairingTier,
} from "../../utils/performerPairings";
import { getLoadError } from "../../utils/queryLoadState";
import { performerGenderLabel } from "../EntityCards";
import { ListLoadError } from "../ListLoadError";
import { toolbarSelectClass } from "../listToolbarStyles";
import { CoStarPortrait, FilterChip, RouteLink, type PairingNavigate } from "./pairingParts";
import { RankedPairingsView } from "./RankedPairingsView";

interface PerformerPairingsPanelProps {
  performer: { id: number; name: string };
  onNavigate: PairingNavigate;
}

export function PerformerPairingsPanel({ performer, onNavigate }: PerformerPairingsPanelProps) {
  const [tierParam, setTierParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.tier);
  const [gender, setGender] = useDetailStringUrlState(PAIRING_URL_PARAMS.gender);
  const [sortParam, setSortParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.sort);
  const [duoOnly, setDuoOnly] = useDetailBooleanUrlState(PAIRING_URL_PARAMS.duoOnly);
  const [favoritesOnly, setFavoritesOnly] = useDetailBooleanUrlState(PAIRING_URL_PARAMS.favoritesOnly);
  const [queryParam, setQueryParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.query);
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
    const timer = window.setTimeout(() => setQueryParam(draft.trim() ? draft : null), 250);
    return () => window.clearTimeout(timer);
  }, [draft, query, setQueryParam]);
  const [shownParam, setShownParam] = useDetailStringUrlState(PAIRING_URL_PARAMS.shownRows);
  const shownRows = useMemo(() => parseShownRows(shownParam), [shownParam]);
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
  const showMore = useCallback(
    (moreTier: PairingTier) => {
      const current = parseShownRows(new URLSearchParams(window.location.search).get(PAIRING_URL_PARAMS.shownRows));
      setShownParam(
        formatShownRows({ ...current, [moreTier]: (current[moreTier] ?? PAIRING_ROWS_FIRST) + PAIRING_ROWS_STEP }),
      );
    },
    [setShownParam],
  );
  // One URL change for all of them; the lineup and the rows shown stay.
  const clearFilters = () => {
    const params = new URLSearchParams(window.location.search);
    for (const key of [
      PAIRING_URL_PARAMS.query,
      PAIRING_URL_PARAMS.tier,
      PAIRING_URL_PARAMS.gender,
      PAIRING_URL_PARAMS.duoOnly,
      PAIRING_URL_PARAMS.favoritesOnly,
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
    const shown = formatShownRows(shownRows);
    if (shown) params.set(PAIRING_URL_PARAMS.shownRows, shown);
    return params.toString();
  }, [data, draft, duoOnly, favoritesOnly, gender, lineupIds, selectedIds, shownRows, sort, tier]);
  useRememberDetailTabUrlParams(PAIRING_TAB_KEY, rememberedParams);

  return (
    <div className="space-y-6">
      <section aria-label="Appears With filters" className="space-y-3 rounded-xl border border-border bg-card p-3">
        <div className="flex flex-wrap items-center gap-2">
          <FilterChip
            pressed={tier == null}
            onClick={() => setTierParam(null)}
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
              onClick={() => setTierParam(tier === option.value ? null : option.value)}
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
              onChange={(event) => setSortParam(event.target.value === "together" ? null : event.target.value)}
              className={toolbarSelectClass}
            >
              {PAIRING_SORT_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <FilterChip pressed={gender == null} onClick={() => setGender(null)}>
            Everyone {data ? <span className="text-muted">{genderTotal}</span> : null}
          </FilterChip>
          {genderChips.map(({ gender: key, count }) => (
            <FilterChip key={key} pressed={gender === key} onClick={() => setGender(gender === key ? null : key)}>
              {key === UNKNOWN_GENDER ? "No gender set" : (performerGenderLabel(key) ?? key)}{" "}
              <span className="text-muted">{count}</span>
            </FilterChip>
          ))}
          <span aria-hidden="true" className="mx-1 h-5 w-px bg-border" />
          <FilterChip
            pressed={duoOnly}
            onClick={() => setDuoOnly(!duoOnly)}
            title="Count only videos with just the two of them"
          >
            Duos only
          </FilterChip>
          <FilterChip pressed={favoritesOnly} onClick={() => setFavoritesOnly(!favoritesOnly)}>
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
        <RankedPairingsView
          performerId={performer.id}
          pairings={sorted}
          duoOnly={duoOnly}
          careerFirstYear={summary?.firstYear ?? null}
          careerLastYear={summary?.lastYear ?? null}
          selectedIds={selectedSet}
          shownRows={shownRows}
          onShowMore={showMore}
          onToggleSelected={toggleSelected}
          onPlay={playVideos}
          onNavigate={onNavigate}
        />
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
