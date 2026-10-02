import { useCallback, useEffect, useMemo, useState, useRef, type RefObject } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { videos, scrapeAttempts, system } from "../api/client";
import type {
  ApplyVideoScrapeAttemptRequest,
  Video,
  MetadataServer,
  MetadataServerEntityCandidate,
  MetadataServerVideoMatch,
  MetadataServerVideoImportRequest,
  ScrapeAttempt,
  ScraperSummary,
  ScrapeCollectionItemSelection,
  VideoCoverComparison,
} from "../api/types";
import { useAppConfig, useOptionalAppConfig } from "../state/AppConfigContext";
import { getApiValidationFailureDetail } from "../utils/requestFailure";
import { formatDuration, getResolutionLabel } from "./shared";
import { createNestedRouteLinkProps } from "./cardNavigation";
import {
  buildFragmentDraft,
  findDefaultKind,
  getVideoNameSearchInput,
  supportsScrapeKind,
  type CollectionMode,
  type InputKind,
} from "./videoScrapeUtils";
import {
  buildMatchInfo,
  buildRelationSelectionPayload,
  relationKey,
  type ScrapeRelationActionMap,
} from "./ScrapeRelationChoices";
import { invalidateVideoMetadataQueries } from "./videoMetadataQueryInvalidation";
import { MetadataDiff, scalarStatus, summarizeDiff, type DiffSelection } from "./MetadataDiff";
import { MetadataDiffSummary } from "./MetadataDiffSummary";
import { ReviewCoverPanel } from "./ReviewCoverPanel";
import { metadataServerLabel } from "./MetadataServerLinks";
import {
  applyTaggerSelectionChange,
  buildTaggerReview,
  isHandEdited,
  type TaggerRelationshipEdits,
  type TaggerRelationshipKey,
  type TaggerReviewInput,
} from "./VideoTaggerReview";
import {
  DEFAULT_TAGGER_DENYLIST,
  RemoteRefreshButtons,
  TaggerSettingsPanel,
  TaggerToolbar,
  cleanTaggerQueryString,
  type TaggerQueryMode,
  type TaggerRunAllOption,
  DismissibleMenu,
} from "./TaggerShared";
import {
  Search,
  Loader2,
  Check,
  X,
  AlertCircle,
  Fingerprint,
  Upload,
  CloudUpload,
  MoreHorizontal,
  ChevronDown,
  ExternalLink,
} from "lucide-react";
import { useMetadataServerDraftSubmit } from "../hooks/useMetadataServerDraftSubmit";
import { toggleOptionsFromEvent, withOrderedToggle, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import {
  PERFORMER_GENDER_OPTIONS,
  buildAllowedGenderKeys,
  isGenderOptionChecked,
  isPerformerGenderAllowed,
  toggleGenderOption,
} from "../utils/performerGenders";
import { VideoPreviewThumbnail } from "./VideoPreviewThumbnail";
import type { EntityMediaFit } from "./EntityMedia";

interface VideoTaggerProps {
  videos: Video[];
  onNavigate?: (videoId: number) => void;
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (videoId: number, options?: MultiSelectToggleOptions) => void;
  mode?: "bulk" | "detail";
  // Identifies the list the videos came from (page, sort, search and filters); a new value is a new
  // list, which starts again with unmatched videos shown.
  resetKey?: string;
}

interface TaggerConfig {
  selectedEndpoint: string;
  setCoverImage: boolean;
  setTags: boolean;
  setPerformers: boolean;
  setStudio: boolean;
  onlyExistingTags: boolean;
  onlyExistingPerformers: boolean;
  onlyExistingStudio: boolean;
  markOrganized: boolean;
  bulkMatchStrategy: VideoMetadataSearchStrategy;
  queryMode: TaggerQueryMode;
  defaultScraperInputKind: InputKind | "auto";
  denylist: string[];
  performerGenders: string[];
  // Stamped on every save so a one-time upgrade of the stored settings runs once and never undoes a
  // choice the user made afterwards.
  configVersion: number;
}

const TAGGER_CONFIG_VERSION = 2;

// Drops the performers whose gender the settings exclude, from both the candidates and the plain name
// list the collection modes and summaries read, so nothing downstream can reintroduce them.
function filterMatchPerformersByGender<T extends MetadataServerVideoMatch>(match: T, allowed: Set<string> | null): T {
  if (allowed == null) return match;
  // A match with no candidates falls back to bare names, which state no gender; the "Unknown" rule
  // decides all of them together, exactly as it would for the candidates the fallback stands in for.
  if (match.performerCandidates.length === 0) {
    return isPerformerGenderAllowed(undefined, allowed) ? match : { ...match, performerNames: [] };
  }
  const kept = match.performerCandidates.filter((candidate) => isPerformerGenderAllowed(candidate.gender, allowed));
  if (kept.length === match.performerCandidates.length) return match;
  // A name is dropped only when a surviving candidate no longer claims it, so two performers sharing a
  // name cannot take each other's entry off the list. A name no candidate claims at all states no
  // gender, so the "Unknown" rule decides it, the same rule the candidate-less match above uses.
  const nameKey = (name: string) => name.trim().toLowerCase();
  const keptNames = new Set(kept.map((candidate) => nameKey(candidate.name)));
  const claimedNames = new Set(match.performerCandidates.map((candidate) => nameKey(candidate.name)));
  const keepUnclaimed = isPerformerGenderAllowed(undefined, allowed);
  return {
    ...match,
    performerCandidates: kept,
    performerNames: match.performerNames.filter((name) =>
      claimedNames.has(nameKey(name)) ? keptNames.has(nameKey(name)) : keepUnclaimed,
    ),
  };
}

// The gender list predates the "Unknown" option, and the setting did nothing at all back then, so no
// saved config records a choice about it. Add it once, on the upgrade, rather than reading its absence
// as a decision to hide every performer whose gender the metadata server does not state. The gate is a
// floor, not an equality: a later version bump must not run this migration a second time.
const PERFORMER_GENDERS_MIGRATION_VERSION = 2;

function upgradeSavedPerformerGenders(saved: Partial<TaggerConfig>) {
  const genders = saved.performerGenders;
  if (!genders) return [...PERFORMER_GENDER_OPTIONS];
  if ((saved.configVersion ?? 0) >= PERFORMER_GENDERS_MIGRATION_VERSION) return genders;
  // Every gender unchecked was as inert as every gender checked while the setting did nothing, and the
  // user saw every performer either way; adding "Unknown" to an empty list would invent a filter that
  // hides all of them but the gender-less ones.
  if (genders.length === 0) return [...PERFORMER_GENDER_OPTIONS];
  return isGenderOptionChecked(genders, "Unknown") ? genders : [...genders, "Unknown"];
}

type VideoMetadataSearchStrategy =
  | "remote-id-and-fingerprint-text"
  | "remote-id-fingerprint"
  | "remote-id"
  | "fingerprint";

const VIDEO_METADATA_SEARCH_STRATEGIES: TaggerRunAllOption[] = [
  {
    value: "remote-id-and-fingerprint-text",
    label: "Linked ID + Fingerprint → Text",
    description: "Compare linked and fingerprint candidates, then use text only if neither matches.",
  },
  {
    value: "remote-id-fingerprint",
    label: "Linked ID → Fingerprint",
    description: "Avoid potentially inaccurate text matches.",
  },
  { value: "remote-id", label: "Linked ID only", description: "Refresh videos already linked to this source." },
  { value: "fingerprint", label: "Fingerprint only", description: "Ignore saved links and identify by file content." },
];

function isVideoMetadataSearchStrategy(value?: string): value is VideoMetadataSearchStrategy {
  return VIDEO_METADATA_SEARCH_STRATEGIES.some((option) => option.value === value);
}

interface VideoSearchState {
  loading: boolean;
  results?: UnifiedVideoMatch[];
  error?: string;
  warning?: string;
  selectedIndex?: number;
  saved?: boolean;
  excludedPerformers?: Set<string>;
  excludedTags?: Set<string>;
  skipStudio?: boolean;
  forceIncludedPerformers?: Set<string>;
  forceIncludedTags?: Set<string>;
  forceIncludeStudio?: boolean;
  fieldStrategies?: Record<string, VideoFieldStrategy>;
  collectionModes?: Record<string, CollectionMode>;
  // Hand edits made in the review beside the scrape, as the edit form would make them.
  tagEdits?: TaggerRelationshipEdits;
  performerEdits?: TaggerRelationshipEdits;
}

/** What one Apply all did, in enough detail that every row it covered is accounted for. */
interface ApplyAllOutcome {
  saved: number;
  skipped: number;
  /** Rows a cancellation stopped before they were started, and which are still unsaved. */
  notAttempted: number;
  reasons: string[];
  failureCount: number;
  cancelled: boolean;
}

/** Which rows one Apply all covered and how far it got, before any of them were resolved. */
interface ApplyAllRun {
  targetIds: number[];
  /** Rows an import was actually sent for. */
  startedIds: number[];
  /** Rows whose result went away between the click and their turn, so nothing was sent for them. */
  skippedIds: number[];
  cancelled: boolean;
}

/**
 * Reads a finished run against current row state, so the notice describes the situation now rather
 * than when the batch ended. A row put right afterwards stops counting as a failure, and once
 * nothing is left outstanding there is nothing to show.
 */
function summariseApplyAllRun(
  run: ApplyAllRun | null,
  states: Record<number, VideoSearchState | undefined>,
): ApplyAllOutcome | null {
  if (!run) return null;
  const isSaved = (videoId: number) => states[videoId]?.saved === true;
  const saved = run.startedIds.filter(isSaved).length;
  // The row owns the reason; reading it back keeps one account of why an import failed.
  const reasons = run.startedIds
    .filter((videoId) => !isSaved(videoId))
    .map((videoId) => states[videoId]?.error)
    .filter((reason): reason is string => Boolean(reason));
  const skipped = run.skippedIds.filter((videoId) => !isSaved(videoId)).length;
  const notAttempted = run.targetIds.filter(
    (videoId) => !run.startedIds.includes(videoId) && !run.skippedIds.includes(videoId) && !isSaved(videoId),
  ).length;
  if (reasons.length === 0 && skipped === 0 && notAttempted === 0) return null;
  return { saved, skipped, notAttempted, reasons, failureCount: reasons.length, cancelled: run.cancelled };
}

/** How many distinct reasons the batch notice names before it summarises the rest. */
const APPLY_ALL_REASON_LIMIT = 3;

/**
 * One sentence for what an Apply all did. Every row the batch covered lands in exactly one count, so
 * the arithmetic always adds up to what the user asked for. Reasons are capped because a conflict
 * names the entity it collided with, so a large batch can fail for as many distinct reasons as rows.
 */
function describeApplyAllOutcome(outcome: ApplyAllOutcome): string {
  const parts = [`Applied ${outcome.saved}`];
  if (outcome.failureCount > 0) parts.push(`failed ${outcome.failureCount}`);
  if (outcome.skipped > 0) parts.push(`skipped ${outcome.skipped}`);
  if (outcome.notAttempted > 0) parts.push(`not attempted ${outcome.notAttempted}`);
  const counts = `${outcome.cancelled ? "Cancelled. " : ""}${parts.join(", ")}.`;

  const distinct = [...new Set(outcome.reasons)];
  if (distinct.length === 0) return counts;
  const named = distinct.slice(0, APPLY_ALL_REASON_LIMIT).map(endWithStop).join(" ");
  const remaining = distinct.length - APPLY_ALL_REASON_LIMIT;
  return remaining > 0
    ? `${counts} ${named} And ${remaining} other reason${remaining === 1 ? "" : "s"}.`
    : `${counts} ${named}`;
}

/** Server messages are not guaranteed to be punctuated, and these are joined into a sentence. */
function endWithStop(text: string): string {
  return /[.!?]$/.test(text.trim()) ? text.trim() : `${text.trim()}.`;
}

/**
 * A tagger precondition that stopped a request being sent. These already read as an explanation, so
 * they are shown as written; everything else is a transport or server failure, which the shared
 * formatter words better than its raw message does.
 */
class TaggerPreconditionError extends Error {}

/**
 * Why an attempt failed, in the most specific wording available. Only this component's own
 * preconditions bypass the shared formatter: routing timeouts, dropped connections and API errors
 * through it is what keeps "API request timed out after 120000 ms" off the screen.
 */
function taggerFailureReason(err: unknown): string {
  if (err instanceof TaggerPreconditionError && err.message.trim()) return err.message;
  return getApiValidationFailureDetail(err);
}

type VideoFieldStrategy = "ignore" | "merge" | "overwrite";

type TaggerSource =
  | { kind: "metadata-server"; value: string; label: string; endpoint: string }
  | { kind: "scraper"; value: string; label: string; scraper: ScraperSummary };

interface UnifiedVideoMatch extends MetadataServerVideoMatch {
  sourceKind: "metadata-server" | "scraper";
  scrapeAttemptId?: string;
  selectedCandidateIndex?: number;
  rawResult?: Record<string, unknown>;
}

const sourceValue = (kind: "metadata-server" | "scraper", id: string) => `${kind}:${id}`;

function normalizeEndpoint(endpoint?: string | null): string {
  return (endpoint ?? "").trim().replace(/\/+$/, "").toLowerCase();
}

function resolveSource(value: string, sources: TaggerSource[]): TaggerSource | undefined {
  return (
    sources.find((source) => source.value === value) ??
    sources.find((source) => source.kind === "metadata-server" && source.endpoint === value) ??
    sources[0]
  );
}

// YAML scrapers emit relationship fields (Tags, Performers, Studio) as `{ Name, URL }` objects, and
// Studio as a one-item list of them. Extension scrapers emit plain strings. Both shapes must resolve
// to names here, otherwise the tagger silently skips the relation.
function asRelationName(value: Record<string, unknown>): string | undefined {
  for (const key of ["Name", "name", "Title", "title"]) {
    const candidate = value[key];
    if (typeof candidate === "string" && candidate.trim()) return candidate.trim();
  }
  return undefined;
}

function asString(value: unknown): string | undefined {
  if (typeof value === "string") return value.trim() || undefined;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  if (Array.isArray(value)) return value.length > 0 ? asString(value[0]) : undefined;
  if (value && typeof value === "object") return asRelationName(value as Record<string, unknown>);
  return undefined;
}

function asStringList(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.flatMap(asStringList).filter(Boolean);
  }
  if (value && typeof value === "object") {
    const name = asRelationName(value as Record<string, unknown>);
    return name ? [name] : [];
  }
  const text = asString(value);
  if (!text) return [];
  return text
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function dedupeIgnoringCase(values: string[]) {
  return values.filter(
    (value, index, items) => items.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index,
  );
}

function pickString(result: Record<string, unknown>, ...keys: string[]) {
  const entries = Object.entries(result);
  for (const key of keys) {
    const entry = entries.find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const value = asString(entry[1]);
    if (value) return value;
  }
  return undefined;
}

function pickStringList(result: Record<string, unknown>, ...keys: string[]) {
  const entries = Object.entries(result);
  for (const key of keys) {
    const entry = entries.find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const values = asStringList(entry[1]);
    if (values.length > 0) return [...new Set(values)];
  }
  return [];
}

function parseAttemptResults(attempt: ScrapeAttempt): Record<string, unknown>[] {
  try {
    if (attempt.candidateResultsJson) {
      const candidates = JSON.parse(attempt.candidateResultsJson);
      if (Array.isArray(candidates))
        return candidates.filter(
          (item): item is Record<string, unknown> => item && typeof item === "object" && !Array.isArray(item),
        );
    }
    if (attempt.resultJson) {
      const result = JSON.parse(attempt.resultJson);
      if (result && typeof result === "object" && !Array.isArray(result)) return [result as Record<string, unknown>];
    }
  } catch {
    return [];
  }
  return [];
}

function toCandidates(names: string[]) {
  // existsLocally is a placeholder here; scraper results don't know local state at parse time. The
  // row enriches it from the backend resolve-relations matcher (see the resolvedRelations query).
  return names.map((name) => ({ remoteId: name, name, existsLocally: false }));
}

interface PerformerChoice {
  key: string;
  label: string;
  candidate: MetadataServerEntityCandidate;
}

function formatPerformerIdentity(name: string, disambiguation?: string | null) {
  const normalizedDisambiguation = disambiguation?.trim();
  return normalizedDisambiguation ? `${name} (${normalizedDisambiguation})` : name;
}

function performerChoiceKey(candidate: MetadataServerEntityCandidate) {
  return `remote-performer:${candidate.remoteId}`;
}

function getPerformerChoices(result: MetadataServerVideoMatch): PerformerChoice[] {
  const candidates: MetadataServerEntityCandidate[] =
    result.performerCandidates.length > 0
      ? result.performerCandidates
      : result.performerNames.map((name) => ({ remoteId: name, name, existsLocally: false }));
  return candidates.map((candidate) => ({
    key: performerChoiceKey(candidate),
    label: formatPerformerIdentity(candidate.name, candidate.disambiguation),
    candidate,
  }));
}

function getCurrentPerformerChoiceKeys(video: Video, choices: PerformerChoice[]) {
  const linkedIds = new Set(video.performers.map((performer) => performer.id));
  return choices
    .filter((choice) => {
      if (choice.candidate.localId != null) return linkedIds.has(choice.candidate.localId);
      const candidateIdentity = relationKey(
        formatPerformerIdentity(choice.candidate.name, choice.candidate.disambiguation),
      );
      return video.performers.some(
        (performer) =>
          relationKey(formatPerformerIdentity(performer.name, performer.disambiguation)) === candidateIdentity,
      );
    })
    .map((choice) => choice.key);
}

function toScraperVideoMatch(
  attempt: ScrapeAttempt,
  result: Record<string, unknown>,
  index: number,
  scraper: ScraperSummary,
): UnifiedVideoMatch {
  const title = pickString(result, "Title", "Name");
  const imageUrl = pickString(result, "Image", "ImageUrl", "ImageURL");
  const performerNames = dedupeIgnoringCase(pickStringList(result, "Performers", "Performer", "PerformerNames"));
  const tagNames = dedupeIgnoringCase(pickStringList(result, "Tags", "Tag", "TagNames"));
  const studioName = pickString(result, "Studio", "StudioName");
  return {
    sourceKind: "scraper",
    scrapeAttemptId: attempt.id,
    selectedCandidateIndex: index,
    rawResult: result,
    endpoint: scraper.id,
    serverName: scraper.name,
    id: `${attempt.id}:${index}`,
    title,
    code: pickString(result, "Code"),
    date: pickString(result, "Date", "ReleaseDate"),
    director: pickString(result, "Director"),
    details: pickString(result, "Details", "Description", "Synopsis"),
    studioName,
    imageUrl,
    duration: undefined,
    performerNames,
    tagNames,
    urls: pickStringList(result, "URLs", "Url", "URL"),
    fingerprintAlgorithms: [],
    matchCount: 0,
    fingerprints: [],
    studioCandidate: studioName ? { remoteId: studioName, name: studioName, existsLocally: false } : undefined,
    performerCandidates: toCandidates(performerNames),
    tagCandidates: toCandidates(tagNames),
  };
}

function getVideoTagNames(video: Video) {
  return video.tags.map((tag) => tag.name).filter(Boolean);
}

function normalizeDecisionValue(value?: string | null) {
  return value?.trim() ?? "";
}

function buildDefaultVideoFieldStrategies(video: Video, result: UnifiedVideoMatch): Record<string, VideoFieldStrategy> {
  const fields = [
    { key: "title", current: video.title, scraped: result.title },
    { key: "code", current: video.code, scraped: result.code },
    { key: "details", current: video.details, scraped: result.details },
    { key: "director", current: video.director, scraped: result.director },
    { key: "date", current: video.date, scraped: result.date },
  ];
  const strategies: Record<string, VideoFieldStrategy> = {};
  for (const field of fields) {
    if (!field.scraped) continue;
    const current = normalizeDecisionValue(field.current);
    // A value the person typed by hand is kept unless they choose otherwise; anything else gives way.
    strategies[field.key] =
      current === normalizeDecisionValue(field.scraped) || (current && isHandEdited(video, field.key))
        ? "ignore"
        : "overwrite";
  }
  return strategies;
}

function getVideoFieldStrategies(video: Video, result: UnifiedVideoMatch, state: VideoSearchState | undefined) {
  return { ...buildDefaultVideoFieldStrategies(video, result), ...state?.fieldStrategies };
}

// Default cover decision: an auto-generated frame cover (no explicit imagePath) is treated as "not set",
// so it defaults to Replace; an explicitly set cover defaults to Keep. The global "Set video cover image"
// toggle, when off, keeps the cover regardless.
function defaultVideoImageStrategy(video: Video, taggerConfig: TaggerConfig): VideoFieldStrategy {
  if (!taggerConfig.setCoverImage) return "ignore";
  return video.imagePath ? "ignore" : "overwrite";
}

// Whether the scraped cover should replace the video's current cover. The per-result "image" decision
// (when the user toggled it) wins; otherwise the explicit-cover default applies.
function getVideoImageReplace(
  video: Video,
  result: UnifiedVideoMatch,
  state: VideoSearchState | undefined,
  taggerConfig: TaggerConfig,
) {
  return (
    (getVideoFieldStrategies(video, result, state).image ?? defaultVideoImageStrategy(video, taggerConfig)) ===
    "overwrite"
  );
}

/**
 * How the selected result's cover compares with the one the video already carries. Only the server
 * can answer it: it alone reads both images' pixels and their real sizes, where the browser sees two
 * unrelated URLs and a delivery-resized copy of its own cover.
 *
 * Asked only for a cover the person actually set; an auto-generated frame is replaceable anyway, and
 * will not resemble a studio's artwork.
 */
function useCoverComparison(video: Video, imageUrl?: string | null): VideoCoverComparison | undefined {
  const enabled = !!imageUrl && !!video.imagePath;
  const { data } = useQuery({
    queryKey: ["video-cover-comparison", video.id, imageUrl],
    queryFn: () => videos.compareCover(video.id, imageUrl ?? ""),
    enabled,
    // The comparison downloads the candidate cover, so it is kept for as long as the visit lasts.
    staleTime: 30 * 60 * 1000,
    retry: false,
  });
  return enabled ? data : undefined;
}

/** The suggestion an "upgrade" verdict is worth making, as the cover panel's sentence. */
function coverComparisonNote(comparison?: VideoCoverComparison) {
  if (comparison?.verdict !== "upgrade" || !comparison.current || !comparison.candidate) return undefined;
  const size = (image: { width: number; height: number }) => `${image.width}×${image.height}`;
  return `same cover, larger here (${size(comparison.candidate)} vs ${size(comparison.current)})`;
}

function buildDefaultVideoCollectionModes(
  result: UnifiedVideoMatch,
  state: VideoSearchState | undefined,
  taggerConfig: TaggerConfig,
): Record<string, CollectionMode> {
  return {
    urls: result.urls.length > 0 ? "merge" : "skip",
    tags: taggerConfig.setTags && result.tagNames.length > 0 ? "merge" : "skip",
    performers: taggerConfig.setPerformers && result.performerNames.length > 0 ? "merge" : "skip",
    studio: taggerConfig.setStudio && !state?.skipStudio && result.studioName ? "replace" : "skip",
  };
}

function getVideoCollectionModes(
  result: UnifiedVideoMatch,
  state: VideoSearchState | undefined,
  taggerConfig: TaggerConfig,
) {
  return { ...buildDefaultVideoCollectionModes(result, state, taggerConfig), ...state?.collectionModes };
}

function collectionModeToFieldStrategy(mode: CollectionMode): VideoFieldStrategy {
  if (mode === "replace") return "overwrite";
  if (mode === "merge") return "merge";
  return "ignore";
}

function buildVideoFieldStrategies(
  video: Video,
  result: UnifiedVideoMatch,
  state: VideoSearchState | undefined,
  taggerConfig: TaggerConfig,
) {
  const scalarStrategies = getVideoFieldStrategies(video, result, state);
  const collectionModes = getVideoCollectionModes(result, state, taggerConfig);
  return {
    ...scalarStrategies,
    urls: collectionModeToFieldStrategy(collectionModes.urls),
    tags: collectionModeToFieldStrategy(collectionModes.tags),
    performers: collectionModeToFieldStrategy(collectionModes.performers),
    studio: collectionModeToFieldStrategy(collectionModes.studio),
  };
}

function buildVideoRelationActionMap(
  names: string[],
  currentNames: string[],
  existingNames: string[],
  excludedNames: Set<string> | undefined,
  forceCreateNames: Set<string> | undefined,
  createMissing: boolean,
): ScrapeRelationActionMap {
  const current = new Set(currentNames.map(relationKey));
  const existing = new Set(existingNames.map(relationKey));
  const excluded = new Set(Array.from(excludedNames ?? []).map(relationKey));
  const forced = new Set(Array.from(forceCreateNames ?? []).map(relationKey));
  const actions: ScrapeRelationActionMap = {};

  for (const name of names) {
    const key = relationKey(name);
    if (!key) continue;
    if (excluded.has(key)) actions[key] = "exclude";
    else if (forced.has(key)) actions[key] = "create";
    else if (current.has(key) || existing.has(key)) actions[key] = "include";
    else actions[key] = createMissing ? "create" : "exclude";
  }

  return actions;
}

function buildVideoRelationSelections(
  names: string[],
  currentNames: string[],
  existingNames: string[],
  excludedNames: Set<string> | undefined,
  forceCreateNames: Set<string> | undefined,
  createMissing: boolean,
): ScrapeCollectionItemSelection[] {
  return buildRelationSelectionPayload(
    names,
    buildVideoRelationActionMap(names, currentNames, existingNames, excludedNames, forceCreateNames, createMissing),
  );
}

function buildScraperVideoApplyRequest(
  result: UnifiedVideoMatch,
  video: Video,
  state: VideoSearchState | undefined,
  taggerConfig: TaggerConfig,
): ApplyVideoScrapeAttemptRequest {
  const fieldStrategies = buildVideoFieldStrategies(video, result, state, taggerConfig);
  const collectionModes = getVideoCollectionModes(result, state, taggerConfig);
  const replaceFields = Object.entries(fieldStrategies)
    .filter(
      ([field, strategy]) =>
        strategy === "overwrite" && !["urls", "tags", "performers", "studio", "image"].includes(field),
    )
    .map(([field]) => field);
  const raw = result.rawResult ?? {};
  // Cover is driven by the per-result image decision (defaulting to the global toggle).
  if (getVideoImageReplace(video, result, state, taggerConfig) && pickString(raw, "Image", "ImageUrl", "ImageURL"))
    replaceFields.push("image");

  const performerChoices = getPerformerChoices(result);
  const performerActions = buildVideoRelationActionMap(
    performerChoices.map((choice) => choice.key),
    getCurrentPerformerChoiceKeys(video, performerChoices),
    performerChoices.filter((choice) => choice.candidate.existsLocally).map((choice) => choice.key),
    state?.excludedPerformers,
    state?.forceIncludedPerformers,
    !taggerConfig.onlyExistingPerformers,
  );
  return {
    replaceFields,
    collectionModes,
    createMissingTags: !taggerConfig.onlyExistingTags,
    createMissingPerformers: !taggerConfig.onlyExistingPerformers,
    createMissingStudio: !taggerConfig.onlyExistingStudio,
    markOrganized: taggerConfig.markOrganized,
    // The tagger always fills in a created performer's details from the scrape; it used to read this off
    // an unrelated, invisible "create parent tags" flag that nothing else consulted.
    hydratePerformers: true,
    selectedCandidateIndex: result.selectedCandidateIndex,
    tagSelections:
      result.tagNames.length > 0
        ? buildVideoRelationSelections(
            result.tagNames,
            getVideoTagNames(video),
            result.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name),
            state?.excludedTags,
            state?.forceIncludedTags,
            !taggerConfig.onlyExistingTags,
          )
        : undefined,
    performerSelections:
      performerChoices.length > 0
        ? performerChoices.map((choice) => ({
            name: choice.candidate.name,
            action: performerActions[relationKey(choice.key)] ?? "exclude",
          }))
        : undefined,
    ...relationshipEditFields(state),
  };
}

// The review's hand edits, in the shape both apply requests take; absent when there are none.
function relationshipEditFields(state: VideoSearchState | undefined) {
  const ids = (list: number[] | undefined) => (list && list.length > 0 ? list : undefined);
  return {
    addedTagIds: ids(state?.tagEdits?.added),
    removedTagIds: ids(state?.tagEdits?.removed),
    addedPerformerIds: ids(state?.performerEdits?.added),
    removedPerformerIds: ids(state?.performerEdits?.removed),
  };
}

const CONCURRENCY_LIMIT = 5;

interface BatchLifecycle {
  onStart: () => void;
  /** Runs once every worker has drained, even if one threw, so the toolbar is always handed back. */
  onFinish: (cancelled: boolean) => void;
}

/**
 * Runs one batch at a time per `batch` ref, which holds the running batch's controller until its last
 * worker drains. Cancelling only aborts that controller: a batch started while the cancelled one still
 * drains would take the ref from it, leaving the first uncancellable and both working the same rows,
 * so a start is refused outright while the ref is held.
 */
async function runWithConcurrency<T>(
  batch: RefObject<AbortController | null>,
  items: T[],
  fn: (item: T, signal: AbortSignal) => Promise<void>,
  limit: number,
  lifecycle: BatchLifecycle,
): Promise<void> {
  if (batch.current) return;
  const controller = new AbortController();
  batch.current = controller;
  lifecycle.onStart();
  try {
    let index = 0;
    const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
      while (index < items.length) {
        if (controller.signal.aborted) return;
        const i = index++;
        await fn(items[i], controller.signal);
      }
    });
    // Settled rather than all, so one worker throwing cannot hand the toolbar back while the others still run.
    const outcomes = await Promise.allSettled(workers);
    const failure = outcomes.find((outcome): outcome is PromiseRejectedResult => outcome.status === "rejected");
    if (failure) throw failure.reason;
  } finally {
    batch.current = null;
    lifecycle.onFinish(controller.signal.aborted);
  }
}

export function VideoTagger({
  videos: videoList,
  onNavigate,
  selectedIds,
  selecting = false,
  onSelect,
  mode = "bulk",
  resetKey = "",
}: VideoTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];
  const videoPreviewObjectFit: EntityMediaFit = config?.ui?.videoObjectFit === "contain" ? "contain" : "cover";
  const { data: scraperList = [] } = useQuery({ queryKey: ["scrapers"], queryFn: system.listScrapers });
  const videoScrapers = scraperList.filter((scraper) => scraper.entityType.toLowerCase() === "video");
  const taggerSources: TaggerSource[] = [
    ...metadataServers.map((server) => ({
      kind: "metadata-server" as const,
      value: sourceValue("metadata-server", server.endpoint),
      label: server.name || server.endpoint,
      endpoint: server.endpoint,
    })),
    ...videoScrapers.map((scraper) => ({
      kind: "scraper" as const,
      value: sourceValue("scraper", scraper.id),
      label: `${scraper.name} (Scraper)`,
      scraper,
    })),
  ];

  const TAGGER_CONFIG_KEY = "cove-tagger-config";

  const DEFAULT_TAGGER_CONFIG: TaggerConfig = {
    selectedEndpoint: metadataServers[0] ? sourceValue("metadata-server", metadataServers[0].endpoint) : "",
    setCoverImage: true,
    setTags: true,
    setPerformers: true,
    setStudio: true,
    onlyExistingTags: false,
    onlyExistingPerformers: false,
    onlyExistingStudio: false,
    markOrganized: false,
    bulkMatchStrategy: "remote-id-and-fingerprint-text",
    queryMode: "auto",
    defaultScraperInputKind: "auto",
    denylist: [...DEFAULT_TAGGER_DENYLIST],
    performerGenders: [...PERFORMER_GENDER_OPTIONS],
    configVersion: TAGGER_CONFIG_VERSION,
  };

  const [taggerConfig, _setTaggerConfig] = useState<TaggerConfig>(() => {
    try {
      const saved = localStorage.getItem(TAGGER_CONFIG_KEY);
      if (saved) {
        const parsed = JSON.parse(saved) as Partial<TaggerConfig>;
        return {
          ...DEFAULT_TAGGER_CONFIG,
          ...parsed,
          selectedEndpoint: parsed.selectedEndpoint ?? DEFAULT_TAGGER_CONFIG.selectedEndpoint,
          bulkMatchStrategy: isVideoMetadataSearchStrategy(parsed.bulkMatchStrategy)
            ? parsed.bulkMatchStrategy
            : DEFAULT_TAGGER_CONFIG.bulkMatchStrategy,
          denylist: parsed.denylist ?? DEFAULT_TAGGER_CONFIG.denylist,
          // Upgraded in memory; the stamp reaches storage on the next settings change. A visit that
          // changes nothing replays the upgrade next time, which is harmless because it is idempotent —
          // a later migration has to stay idempotent too, or write the stamp back on load itself.
          performerGenders: upgradeSavedPerformerGenders(parsed),
          configVersion: TAGGER_CONFIG_VERSION,
        };
      }
    } catch {
      /* ignore */
    }
    return DEFAULT_TAGGER_CONFIG;
  });

  const setTaggerConfig = useCallback((updater: TaggerConfig | ((prev: TaggerConfig) => TaggerConfig)) => {
    _setTaggerConfig((prev) => {
      const next = typeof updater === "function" ? updater(prev) : updater;
      try {
        localStorage.setItem(TAGGER_CONFIG_KEY, JSON.stringify(next));
      } catch {
        /* ignore */
      }
      return next;
    });
  }, []);
  const [showConfig, setShowConfig] = useState(false);
  // Deliberately outside the persisted config: hiding unmatched is a way to work through one pass of
  // results, not a preference, and a stored "hide" would greet the next visit with an empty list before
  // anything has been searched.
  const [showUnmatched, setShowUnmatched] = useState(true);
  // Reset during render rather than in an effect, so a new page never flashes as an empty list. The
  // key, not the video ids, marks a new list: an apply that refetches the list and drops a video the
  // filter now excludes must not undo the user's choice mid-pass.
  const [showUnmatchedResetKey, setShowUnmatchedResetKey] = useState(resetKey);
  if (showUnmatchedResetKey !== resetKey) {
    setShowUnmatchedResetKey(resetKey);
    setShowUnmatched(true);
  }
  const [bulkStrategyDraft, setBulkStrategyDraft] = useState<VideoMetadataSearchStrategy>(
    taggerConfig.bulkMatchStrategy,
  );
  const [searchStates, setSearchStates] = useState<Record<number, VideoSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [scraperInputKinds, setScraperInputKinds] = useState<Record<number, InputKind>>({});
  const selectedSource = resolveSource(taggerConfig.selectedEndpoint, taggerSources);

  const updateSearchState = useCallback((videoId: number, update: Partial<VideoSearchState>) => {
    setSearchStates((prev) => ({
      ...prev,
      [videoId]: { ...prev[videoId], ...update },
    }));
  }, []);

  // Derive search query from video (standard prepareQueryString logic)
  const getSearchQuery = useCallback(
    (video: Video): string => {
      if (queryOverrides[video.id] !== undefined) return queryOverrides[video.id];
      const file = video.files.find((candidate) => candidate.id === video.primaryFileId);
      const mode = taggerConfig.queryMode;

      // metadata mode, or auto mode when video has date+studio — build compound query
      if (mode === "metadata" || (mode === "auto" && video.date && video.studioName)) {
        let str = [
          video.date || "",
          video.studioName || "",
          (video.performers || []).map((p: any) => p.name).join(" "),
          video.title ? video.title.replace(/[^a-zA-Z0-9 ]+/g, "") : "",
        ]
          .filter((s) => s !== "")
          .join(" ");
        str = cleanTaggerQueryString(str, taggerConfig.denylist);
        return str;
      }

      // filename/dir/path modes: derive from file path
      if (mode === "filename" && file?.basename) {
        return cleanTaggerQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.denylist);
      }
      if (mode === "dir" && file?.path) {
        const parts = file.path.replace(/\\/g, "/").split("/");
        return parts.length > 1 ? cleanTaggerQueryString(parts[parts.length - 2], taggerConfig.denylist) : "";
      }
      if (mode === "path" && file?.path) {
        return cleanTaggerQueryString(file.path, taggerConfig.denylist);
      }

      // auto mode: try title first, then filename — always apply denylist
      if (video.title) return cleanTaggerQueryString(video.title, taggerConfig.denylist);
      if (file?.basename) {
        return cleanTaggerQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.denylist);
      }
      return "";
    },
    [queryOverrides, taggerConfig.queryMode, taggerConfig.denylist],
  );

  const getScraperInputKind = useCallback(
    (video: Video, source: TaggerSource | undefined): InputKind => {
      if (source?.kind !== "scraper") {
        return "name";
      }

      const override = scraperInputKinds[video.id];
      if (override) {
        return override;
      }
      const configured = taggerConfig.defaultScraperInputKind;
      const preferred: InputKind =
        configured !== "auto" ? configured : video.urls?.some((url) => url.trim()) ? "url" : "name";
      return findDefaultKind(source.scraper, preferred);
    },
    [scraperInputKinds, taggerConfig.defaultScraperInputKind],
  );

  const getSourceQuery = useCallback(
    (video: Video, source: TaggerSource | undefined): string => {
      if (source?.kind === "scraper") {
        const inputKind = getScraperInputKind(video, source);
        if (queryOverrides[video.id] !== undefined) {
          return queryOverrides[video.id];
        }

        if (inputKind === "url") {
          return video.urls?.find((url) => url.trim()) ?? "";
        }

        if (inputKind === "fragment") {
          return buildFragmentDraft(video);
        }

        return getVideoNameSearchInput(video) || getSearchQuery(video);
      }
      return getSearchQuery(video);
    },
    [getScraperInputKind, getSearchQuery, queryOverrides],
  );

  const handleScraperInputKindChange = useCallback(
    (video: Video, source: TaggerSource | undefined, inputKind: InputKind) => {
      setScraperInputKinds((prev) => ({ ...prev, [video.id]: inputKind }));
      setQueryOverrides((prev) => {
        const nextQuery =
          inputKind === "url"
            ? (video.urls?.find((url) => url.trim()) ?? "")
            : inputKind === "fragment"
              ? buildFragmentDraft(video)
              : getVideoNameSearchInput(video) || getSearchQuery(video);
        return { ...prev, [video.id]: nextQuery };
      });
      if (source?.kind === "scraper" && !supportsScrapeKind(source.scraper, inputKind)) {
        updateSearchState(video.id, { error: `The selected scraper does not support ${inputKind} input.` });
      }
    },
    [getSearchQuery, updateSearchState],
  );

  const searchVideo = useCallback(
    async (video: Video, bulkStrategy?: VideoMetadataSearchStrategy, signal?: AbortSignal) => {
      const source = selectedSource;
      const query = getSourceQuery(video, source);
      updateSearchState(video.id, {
        loading: true,
        error: undefined,
        warning: undefined,
        results: undefined,
        saved: false,
        tagEdits: undefined,
        performerEdits: undefined,
      });
      try {
        let results: UnifiedVideoMatch[] = [];
        if (source?.kind === "scraper") {
          const inputKind = getScraperInputKind(video, source);
          if (!supportsScrapeKind(source.scraper, inputKind))
            throw new Error(`This scraper does not support ${inputKind} input.`);
          if (inputKind === "url" && !query.trim()) throw new Error("Enter a URL to scrape.");
          if (inputKind === "name" && !query.trim()) throw new Error("Enter a title or name to scrape.");
          let fragment: Record<string, unknown> | undefined;
          if (inputKind === "fragment") {
            const parsed = JSON.parse(query);
            if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
              throw new Error("Fragment input must be a JSON object.");
            }
            fragment = parsed as Record<string, unknown>;
          }
          const attempt = await scrapeAttempts.create({
            scraperId: source.scraper.id,
            entityType: "video",
            entityId: video.id,
            inputKind,
            url: inputKind === "url" ? query : undefined,
            name: inputKind === "name" ? query : undefined,
            fragment,
          });
          if (attempt.status.toLowerCase() === "failure")
            throw new Error(attempt.error || "Scrape returned no results.");
          results = parseAttemptResults(attempt).map((result, index) =>
            toScraperVideoMatch(attempt, result, index, source.scraper),
          );
        } else {
          const endpoint = source?.endpoint || undefined;
          if (!bulkStrategy && !query.trim()) throw new Error("Enter a title or name to search.");
          // The row's query box searches by text alone whatever the bulk strategy, and says so in the request
          // rather than leaving the server to infer it from a term arriving without a strategy.
          const strategy = bulkStrategy ?? "text";
          results = (await videos.searchMetadataServer(video.id, query || undefined, endpoint, strategy, signal)).map(
            (match) => ({ ...match, sourceKind: "metadata-server" as const }),
          );
        }

        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        // A cancelled batch abandons the searches it had sent; the row goes back to idle rather than failing.
        // Only the abort itself is swallowed: a search that failed for its own reason still says why.
        if (signal?.aborted && (err as { name?: unknown } | null)?.name === "AbortError") {
          updateSearchState(video.id, { loading: false });
          return;
        }
        updateSearchState(video.id, {
          loading: false,
          error: taggerFailureReason(err),
        });
      }
    },
    [getScraperInputKind, getSourceQuery, selectedSource, updateSearchState],
  );

  // Fingerprint-only search
  const searchVideoFingerprints = useCallback(
    async (video: Video) => {
      updateSearchState(video.id, {
        loading: true,
        error: undefined,
        warning: undefined,
        results: undefined,
        saved: false,
        tagEdits: undefined,
        performerEdits: undefined,
      });
      try {
        if (selectedSource?.kind !== "metadata-server")
          throw new TaggerPreconditionError("Fingerprint search is only available for metadata-server sources.");
        const results = (
          await videos.searchMetadataServer(video.id, undefined, selectedSource.endpoint || undefined, "fingerprint")
        ).map((match) => ({ ...match, sourceKind: "metadata-server" as const }));
        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(video.id, {
          loading: false,
          error: taggerFailureReason(err),
        });
      }
    },
    [selectedSource, updateSearchState],
  );

  // Refresh/rescrape directly from an existing remote id (no name search needed).
  const refreshVideoFromRemote = useCallback(
    async (video: Video, endpoint: string, remoteId: string) => {
      updateSearchState(video.id, {
        loading: true,
        error: undefined,
        warning: undefined,
        results: undefined,
        saved: false,
        tagEdits: undefined,
        performerEdits: undefined,
      });
      try {
        const results = (await videos.findMetadataServerByIds({ endpoint, ids: [remoteId] })).map((match) => ({
          ...match,
          sourceKind: "metadata-server" as const,
        }));
        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
          error: results.length === 0 ? "No metadata-server entry found for this remote id." : undefined,
        });
      } catch (err) {
        updateSearchState(video.id, { loading: false, error: taggerFailureReason(err) });
      }
    },
    [updateSearchState],
  );

  // Batch scrape all (concurrent)
  const [batchSearching, setBatchSearching] = useState(false);
  const searchBatchRef = useRef<AbortController | null>(null);
  const searchAll = useCallback(
    async (strategyOverride?: string) => {
      const toSearch = videoList.filter((s) => !searchStates[s.id]?.saved);
      const bulkStrategy =
        selectedSource?.kind === "metadata-server"
          ? isVideoMetadataSearchStrategy(strategyOverride)
            ? strategyOverride
            : taggerConfig.bulkMatchStrategy
          : undefined;
      await runWithConcurrency(
        searchBatchRef,
        toSearch,
        (video, signal) => searchVideo(video, bulkStrategy, signal),
        CONCURRENCY_LIMIT,
        { onStart: () => setBatchSearching(true), onFinish: () => setBatchSearching(false) },
      );
    },
    [selectedSource, taggerConfig.bulkMatchStrategy, videoList, searchStates, searchVideo],
  );

  // Stops rows being started; the toolbar stays busy until the searches already sent come back.
  const cancelBatchSearch = useCallback(() => {
    searchBatchRef.current?.abort();
  }, []);

  // Videos the user has taken off this pass, so a bad or unwanted match stops occupying the list and
  // stays out of Apply all. Deliberately component state and nothing more: a dismissal lasts for this
  // visit to the tagger and the video is back on the next load.
  const [dismissedIds, setDismissedIds] = useState<ReadonlySet<number>>(() => new Set());
  const dismissVideo = useCallback((videoId: number) => {
    setDismissedIds((current) => new Set(current).add(videoId));
  }, []);
  const restoreDismissed = useCallback(() => setDismissedIds(new Set()), []);

  // Bulk apply. Each row publishes its own apply here, so Apply all sends exactly the request the
  // row's own Apply button would, honouring every per-row exclusion and field choice already made.
  const applyHandlersRef = useRef(new Map<number, () => Promise<unknown>>());
  const registerApply = useCallback((videoId: number, apply: (() => Promise<unknown>) | null) => {
    if (apply) applyHandlersRef.current.set(videoId, apply);
    else applyHandlersRef.current.delete(videoId);
  }, []);
  const [applyingAll, setApplyingAll] = useState(false);
  const applyBatchRef = useRef<AbortController | null>(null);
  // A video counts as matched once a search returned results it has not been saved from yet.
  const applyAllTargets = videoList
    .filter((video) => {
      if (dismissedIds.has(video.id)) return false;
      const videoState = searchStates[video.id];
      return !videoState?.saved && !!videoState?.results && videoState.results.length > 0;
    })
    .map((video) => video.id);
  // Which rows the last Apply all covered, and how far it got. Only the run is recorded: what became
  // of each row is read back from that row's own state when the notice renders, so a row put right
  // afterwards drops out of the notice by itself and the two can never disagree about why it failed.
  const [applyAllRun, setApplyAllRun] = useState<ApplyAllRun | null>(null);
  // The dismiss button leaves with the summary, so focus moves to the list the summary described rather
  // than dropping to the page body, where a keyboard user would have to find their way back from the top.
  const videoListRef = useRef<HTMLDivElement>(null);
  const dismissApplyAllRun = useCallback(() => {
    setApplyAllRun(null);
    videoListRef.current?.focus();
  }, []);
  const applyAll = useCallback(async () => {
    const targetIds = applyAllTargets;
    const startedIds: number[] = [];
    const skippedIds: number[] = [];
    await runWithConcurrency(
      applyBatchRef,
      targetIds,
      async (videoId) => {
        // A row unmounted or saved since the click no longer has a handler; skip it rather than fail.
        const apply = applyHandlersRef.current.get(videoId);
        if (!apply) {
          skippedIds.push(videoId);
          return;
        }
        startedIds.push(videoId);
        // The row records its own outcome, and one row's failure must not abandon the batch.
        await apply().catch(() => undefined);
      },
      CONCURRENCY_LIMIT,
      {
        onStart: () => {
          setApplyingAll(true);
          setApplyAllRun(null);
        },
        onFinish: (cancelled) => {
          setApplyingAll(false);
          // Cancelling stops rows being started but never interrupts an import already sent, so the rows
          // that were never reached are recorded rather than dropped out of the arithmetic.
          setApplyAllRun({ targetIds, startedIds, skippedIds, cancelled });
        },
      },
    );
  }, [applyAllTargets]);
  const cancelApplyAll = useCallback(() => {
    applyBatchRef.current?.abort();
  }, []);
  const applyAllOutcome = summariseApplyAllRun(applyAllRun, searchStates);

  if (taggerSources.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No Metadata Sources Configured</p>
        <p className="text-muted text-sm mt-1">Add a metadata server or install a video scraper to use the tagger.</p>
      </div>
    );
  }

  // Detail mode was opened for this specific video, so always show it (the bulk "hide unmatched"
  // convenience filter would otherwise leave the dialog empty). Hiding unmatched keeps only videos
  // that actually have a match right now: one that has not been searched at all is unmatched too.
  const matchingList =
    mode === "detail" || showUnmatched
      ? videoList
      : videoList.filter((s) => {
          const state = searchStates[s.id];
          return !!state?.results && state.results.length > 0;
        });
  // Detail mode is about one video the user opened deliberately, so a stale dismissal must not empty it.
  const visibleVideos =
    mode === "detail" || dismissedIds.size === 0
      ? matchingList
      : matchingList.filter((video) => !dismissedIds.has(video.id));
  const visibleVideoIds = visibleVideos.map((video) => video.id);
  const dismissedVisibleCount = matchingList.length - visibleVideos.length;

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={taggerSources.map((source) => ({ value: source.value, label: source.label }))}
        selectedSource={selectedSource?.value ?? taggerConfig.selectedEndpoint}
        onSourceChange={(value) => {
          setTaggerConfig((c) => ({ ...c, selectedEndpoint: value }));
          setSearchStates({});
          setQueryOverrides({});
          setScraperInputKinds({});
          // The results the last batch acted on are gone, so its summary describes nothing that is
          // still on screen; keeping it would resurrect failures from the previous source.
          setApplyAllRun(null);
        }}
        showToggle={
          mode === "bulk"
            ? {
                value: showUnmatched,
                onChange: setShowUnmatched,
                enabledLabel: "Hide Unmatched",
                disabledLabel: "Show Unmatched",
              }
            : undefined
        }
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        runAllOptions={selectedSource?.kind === "metadata-server" ? VIDEO_METADATA_SEARCH_STRATEGIES : undefined}
        showRunAll={mode === "bulk"}
        countLabel={`${visibleVideos.length} video${visibleVideos.length !== 1 ? "s" : ""}`}
        dismissed={mode === "bulk" ? { count: dismissedVisibleCount, onRestore: restoreDismissed } : undefined}
        applyAll={
          mode === "bulk"
            ? {
                onApply: () => void applyAll(),
                onCancel: cancelApplyAll,
                busy: applyingAll,
                count: applyAllTargets.length,
              }
            : undefined
        }
        settingsOpen={showConfig}
        onToggleSettings={() => setShowConfig((current) => !current)}
      />

      {showConfig && (
        <TaggerSettingsPanel
          denylist={taggerConfig.denylist}
          onDenylistChange={(items) => setTaggerConfig((c) => ({ ...c, denylist: items }))}
        >
          {selectedSource?.kind === "metadata-server" && mode === "bulk" && (
            <div>
              <label className="block text-xs text-muted mb-1" htmlFor="default-bulk-match-strategy">
                Default bulk match strategy
              </label>
              <div className="flex flex-wrap items-center gap-2">
                <select
                  id="default-bulk-match-strategy"
                  value={bulkStrategyDraft}
                  onChange={(event) => setBulkStrategyDraft(event.target.value as VideoMetadataSearchStrategy)}
                  className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                >
                  {VIDEO_METADATA_SEARCH_STRATEGIES.map((strategy) => (
                    <option key={strategy.value} value={strategy.value}>
                      {strategy.label}
                    </option>
                  ))}
                </select>
                <button
                  type="button"
                  onClick={() => setTaggerConfig((current) => ({ ...current, bulkMatchStrategy: bulkStrategyDraft }))}
                  disabled={bulkStrategyDraft === taggerConfig.bulkMatchStrategy}
                  className="rounded border border-border bg-input px-2 py-1 text-xs text-secondary hover:text-foreground disabled:opacity-50"
                >
                  Save default
                </button>
              </div>
              <p className="text-[10px] text-muted mt-1">
                {VIDEO_METADATA_SEARCH_STRATEGIES.find((strategy) => strategy.value === bulkStrategyDraft)?.description}{" "}
                Use the menu beside Search all for a one-time override.
              </p>
            </div>
          )}

          {/* Performer genders */}
          <div>
            <p className="text-xs text-muted mb-1.5">Performer genders</p>
            <div className="space-y-1">
              {PERFORMER_GENDER_OPTIONS.map((g) => (
                <label key={g} className="flex items-center gap-2 text-xs text-foreground">
                  <input
                    type="checkbox"
                    checked={isGenderOptionChecked(taggerConfig.performerGenders, g)}
                    onChange={(e) =>
                      setTaggerConfig((c) => ({
                        ...c,
                        performerGenders: toggleGenderOption(c.performerGenders, g, e.target.checked),
                      }))
                    }
                    className="rounded border-border"
                  />
                  {g}
                </label>
              ))}
            </div>
            <p className="text-[10px] text-muted mt-1">
              Performers with these genders will be shown when tagging videos. Only a metadata server states a
              performer's gender, so scraper results are unaffected.
            </p>
          </div>

          {/* Set video cover image */}
          <div>
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input
                type="checkbox"
                checked={taggerConfig.setCoverImage}
                onChange={(e) => setTaggerConfig((c) => ({ ...c, setCoverImage: e.target.checked }))}
                className="rounded border-border"
              />
              Set video cover image
            </label>
            <p className="text-[10px] text-muted mt-0.5 ml-5">Replace the video cover if one is found.</p>
          </div>

          {/* Set performers */}
          <div>
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input
                type="checkbox"
                checked={taggerConfig.setPerformers}
                onChange={(e) => setTaggerConfig((c) => ({ ...c, setPerformers: e.target.checked }))}
                className="rounded border-border"
              />
              Set performers
            </label>
            {taggerConfig.setPerformers && (
              <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                <input
                  type="checkbox"
                  checked={!taggerConfig.onlyExistingPerformers}
                  onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingPerformers: !e.target.checked }))}
                  className="rounded border-border"
                />
                Create missing performers
              </label>
            )}
            <p className="text-[10px] text-muted mt-0.5 ml-5">
              Attach performers to video. Uncheck "Create missing" to only use performers that already exist.
            </p>
          </div>

          {/* Set studio */}
          <div>
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input
                type="checkbox"
                checked={taggerConfig.setStudio}
                onChange={(e) => setTaggerConfig((c) => ({ ...c, setStudio: e.target.checked }))}
                className="rounded border-border"
              />
              Set studio
            </label>
            {taggerConfig.setStudio && (
              <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                <input
                  type="checkbox"
                  checked={!taggerConfig.onlyExistingStudio}
                  onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingStudio: !e.target.checked }))}
                  className="rounded border-border"
                />
                Create missing studios
              </label>
            )}
            <p className="text-[10px] text-muted mt-0.5 ml-5">
              Set the video studio. Uncheck "Create missing" to only use studios that already exist.
            </p>
          </div>

          {/* Set tags + operation */}
          <div>
            <div className="flex items-center gap-3">
              <label className="flex items-center gap-2 text-xs text-foreground">
                <input
                  type="checkbox"
                  checked={taggerConfig.setTags}
                  onChange={(e) => setTaggerConfig((c) => ({ ...c, setTags: e.target.checked }))}
                  className="rounded border-border"
                />
                Set tags
              </label>
            </div>
            {taggerConfig.setTags && (
              <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                <input
                  type="checkbox"
                  checked={!taggerConfig.onlyExistingTags}
                  onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingTags: !e.target.checked }))}
                  className="rounded border-border"
                />
                Create missing tags
              </label>
            )}
            <p className="text-[10px] text-muted mt-0.5 ml-5">
              Attach tags to video. Uncheck "Create missing" to only set tags that already exist.
            </p>
          </div>

          {/* Query mode */}
          <div>
            <div className="flex items-center gap-2">
              <span className="text-xs text-muted">Query Mode:</span>
              <select
                value={taggerConfig.queryMode}
                onChange={(e) =>
                  setTaggerConfig((c) => ({ ...c, queryMode: e.target.value as TaggerConfig["queryMode"] }))
                }
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              >
                <option value="auto">Auto</option>
                <option value="filename">Filename</option>
                <option value="dir">Directory</option>
                <option value="path">Full Path</option>
                <option value="metadata">Metadata</option>
              </select>
            </div>
            <p className="text-[10px] text-muted mt-0.5">Uses metadata if present, or filename</p>
          </div>

          {/* Default scraper input (only relevant when the source is a scraper) */}
          {selectedSource?.kind === "scraper" && (
            <div>
              <div className="flex items-center gap-2">
                <span className="text-xs text-muted">Scraper Input:</span>
                <select
                  value={taggerConfig.defaultScraperInputKind}
                  onChange={(e) => {
                    setTaggerConfig((c) => ({
                      ...c,
                      defaultScraperInputKind: e.target.value as TaggerConfig["defaultScraperInputKind"],
                    }));
                    setScraperInputKinds({});
                    setQueryOverrides({});
                  }}
                  className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                >
                  <option value="auto">Auto</option>
                  <option value="url">URL</option>
                  <option value="name">Title</option>
                  <option value="fragment">Fragment</option>
                </select>
              </div>
              <p className="text-[10px] text-muted mt-0.5">
                Default scrape input for scraper sources. Auto uses the URL when present, otherwise the title. Falls
                back to a supported mode if the scraper lacks the chosen one, and can be overridden per video.
              </p>
            </div>
          )}

          {/* Mark organized */}
          <div>
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input
                type="checkbox"
                checked={taggerConfig.markOrganized}
                onChange={(e) => setTaggerConfig((c) => ({ ...c, markOrganized: e.target.checked }))}
                className="rounded border-border"
              />
              Mark as Organized on save
            </label>
            <p className="text-[10px] text-muted mt-0.5 ml-5">
              Immediately mark the video as Organized after the Save button is clicked.
            </p>
          </div>
        </TaggerSettingsPanel>
      )}

      {/* Outcome of the last Apply all. A failure is an alert, because it is added to the page rather
          than updated in place and a polite region is not reliably announced for that. A run the user
          cancelled themselves is only a status: they know they cancelled it, and interrupting a screen
          reader assertively to say so would be noise. */}
      {applyAllOutcome && (
        <div
          role={applyAllOutcome.failureCount > 0 ? "alert" : "status"}
          className={`flex items-start gap-2 border-b border-border px-4 py-2 text-xs ${
            applyAllOutcome.failureCount > 0 ? "bg-red-500/5 text-red-400" : "bg-surface text-muted"
          }`}
        >
          <AlertCircle className="mt-0.5 h-3 w-3 shrink-0" aria-hidden="true" />
          <p className="min-w-0 flex-1">
            {describeApplyAllOutcome(applyAllOutcome)}
            {applyAllOutcome.failureCount > 0 && " The videos that failed keep their changes and can be applied again."}
          </p>
          <button
            type="button"
            onClick={dismissApplyAllRun}
            aria-label="Dismiss apply summary"
            className="shrink-0 rounded p-0.5 opacity-70 hover:bg-foreground/10 hover:opacity-100"
          >
            <X className="h-3 w-3" />
          </button>
        </div>
      )}

      {/* Video list */}
      <div
        ref={videoListRef}
        tabIndex={-1}
        role="region"
        aria-label="Videos"
        className="divide-y divide-border focus:outline-none focus-visible:ring-1 focus-visible:ring-accent/40"
      >
        {visibleVideos.map((video) => (
          <TaggerVideoRow
            key={video.id}
            video={video}
            state={searchStates[video.id]}
            query={getSourceQuery(video, selectedSource)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [video.id]: q }))}
            scraperInputKind={getScraperInputKind(video, selectedSource)}
            onScraperInputKindChange={(inputKind) => handleScraperInputKindChange(video, selectedSource, inputKind)}
            onSearch={() => searchVideo(video)}
            onSearchFingerprints={() => searchVideoFingerprints(video)}
            onRefreshFromRemote={(endpoint, remoteId) => refreshVideoFromRemote(video, endpoint, remoteId)}
            onUpdateState={(update) => updateSearchState(video.id, update)}
            source={selectedSource}
            metadataServers={metadataServers}
            taggerConfig={taggerConfig}
            videoPreviewObjectFit={videoPreviewObjectFit}
            onNavigate={onNavigate}
            selected={selectedIds?.has(video.id) ?? false}
            selecting={selecting}
            onSelect={onSelect ? withOrderedToggle(onSelect, visibleVideoIds) : undefined}
            detailMode={mode === "detail"}
            onRegisterApply={registerApply}
            onDismiss={mode === "bulk" ? () => dismissVideo(video.id) : undefined}
          />
        ))}
      </div>
    </div>
  );
}

/* ── Video Tagger Row ── */

interface TaggerVideoRowProps {
  video: Video;
  state?: VideoSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  scraperInputKind: InputKind;
  onScraperInputKindChange: (inputKind: InputKind) => void;
  onSearch: () => void;
  onSearchFingerprints: () => void;
  onRefreshFromRemote: (endpoint: string, remoteId: string) => void | Promise<void>;
  onUpdateState: (update: Partial<VideoSearchState>) => void;
  source?: TaggerSource;
  metadataServers: MetadataServer[];
  taggerConfig: TaggerConfig;
  videoPreviewObjectFit: EntityMediaFit;
  onNavigate?: (videoId: number) => void;
  selected?: boolean;
  selecting?: boolean;
  onSelect?: (videoId: number, options?: MultiSelectToggleOptions) => void;
  detailMode?: boolean;
  /**
   * Publishes this row's apply action so the toolbar's Apply all can drive it. The row owns the
   * request it would send, so bulk apply reuses that instead of rebuilding it from the outside.
   * Called with null when the row has nothing to apply.
   */
  onRegisterApply?: (videoId: number, apply: (() => Promise<unknown>) | null) => void;
  /** Takes this row off the list for the rest of the visit. Absent when dismissing does not apply. */
  onDismiss?: () => void;
}

function TaggerVideoRow({
  video,
  state,
  query,
  onQueryChange,
  scraperInputKind,
  onScraperInputKindChange,
  onSearch,
  onSearchFingerprints,
  onRefreshFromRemote,
  onUpdateState,
  source,
  metadataServers,
  taggerConfig,
  videoPreviewObjectFit,
  onNavigate,
  selected = false,
  selecting = false,
  onSelect,
  detailMode = false,
  onRegisterApply,
  onDismiss,
}: TaggerVideoRowProps) {
  const file = video.files.find((candidate) => candidate.id === video.primaryFileId);
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);
  const handleRefreshFromRemote = async (endpoint: string, remoteId: string) => {
    setRefreshBusyEndpoint(endpoint);
    try {
      await onRefreshFromRemote(endpoint, remoteId);
    } finally {
      setRefreshBusyEndpoint(null);
    }
  };
  const queryClient = useQueryClient();
  // Resolve which scraper-returned tag/performer names already exist locally, using the same backend
  // matcher the apply path uses (exact name + blank disambiguation for performers). Metadata-server results already carry
  // correct existsLocally from their own search, so only scraper candidates are enriched below.
  const scraperResultNames = useMemo(() => {
    const tags = new Set<string>();
    const performers = new Set<string>();
    for (const r of state?.results ?? []) {
      if (r.sourceKind !== "scraper") continue;
      r.tagNames.forEach((name) => tags.add(name));
      r.performerNames.forEach((name) => performers.add(name));
    }
    return { tags: [...tags], performers: [...performers] };
  }, [state?.results]);
  const { data: resolvedRelations } = useQuery({
    queryKey: ["tagger-resolve-relations", scraperResultNames],
    queryFn: () =>
      scrapeAttempts.resolveRelations({ tags: scraperResultNames.tags, performers: scraperResultNames.performers }),
    enabled: scraperResultNames.tags.length > 0 || scraperResultNames.performers.length > 0,
    staleTime: 30_000,
  });
  const existingTagKeys = useMemo(
    () => new Set((resolvedRelations?.tags ?? []).map((m) => relationKey(m.input))),
    [resolvedRelations],
  );
  const existingPerformerKeys = useMemo(
    () => new Set((resolvedRelations?.performers ?? []).map((m) => relationKey(m.input))),
    [resolvedRelations],
  );
  const tagMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.tags), [resolvedRelations]);
  const performerMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.performers), [resolvedRelations]);
  const allowedGenderKeys = useMemo(
    () => buildAllowedGenderKeys(taggerConfig.performerGenders),
    [taggerConfig.performerGenders],
  );
  const enrichedResults = useMemo(() => {
    const results = state?.results;
    if (!results) return results;
    return results.map((r) =>
      r.sourceKind !== "scraper"
        ? // Only a metadata server states a performer's gender, so only its matches can be filtered by
          // one. Dropping the excluded performers here keeps the preview, its counts and the apply
          // request agreed on one list, and re-runs when the setting changes without a new search.
          filterMatchPerformersByGender(r, allowedGenderKeys)
        : {
            ...r,
            tagCandidates: r.tagCandidates.map((c) => ({
              ...c,
              existsLocally: existingTagKeys.has(relationKey(c.name)),
            })),
            performerCandidates: r.performerCandidates.map((c) => ({
              ...c,
              existsLocally: existingPerformerKeys.has(relationKey(c.name)),
            })),
          },
    );
  }, [state?.results, existingTagKeys, existingPerformerKeys, allowedGenderKeys]);
  const selectedResult = enrichedResults?.[state?.selectedIndex ?? 0];
  const coverComparison = useCoverComparison(video, selectedResult?.imageUrl);
  const videoLinkProps = createNestedRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, () =>
    onNavigate?.(video.id),
  );
  const isScraperSource = source?.kind === "scraper";
  const isFragmentInput = isScraperSource && scraperInputKind === "fragment";
  const videoUrls = (video.urls ?? []).filter((url) => url.trim());
  const selectedUrlOption = videoUrls.includes(query) ? query : "__custom";
  const searchPlaceholder = isScraperSource
    ? scraperInputKind === "url"
      ? "Video URL..."
      : scraperInputKind === "fragment"
        ? "Fragment JSON..."
        : "Title or name..."
    : "Search query...";
  const textSearchLabel = isScraperSource ? "Search" : "Search for this text";

  const importMut = useMutation<Video | ScrapeAttempt, Error>({
    mutationFn: () => {
      if (!selectedResult) throw new TaggerPreconditionError("No result selected");
      const collectionModes = getVideoCollectionModes(selectedResult, state, taggerConfig);
      const tagActions = buildVideoRelationActionMap(
        selectedResult.tagNames,
        getVideoTagNames(video),
        selectedResult.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name),
        state?.excludedTags,
        state?.forceIncludedTags,
        !taggerConfig.onlyExistingTags,
      );
      const performerChoices = getPerformerChoices(selectedResult);
      const performerActions = buildVideoRelationActionMap(
        performerChoices.map((choice) => choice.key),
        getCurrentPerformerChoiceKeys(video, performerChoices),
        performerChoices.filter((choice) => choice.candidate.existsLocally).map((choice) => choice.key),
        state?.excludedPerformers,
        state?.forceIncludedPerformers,
        !taggerConfig.onlyExistingPerformers,
      );
      const excludedTags =
        collectionModes.tags === "skip"
          ? selectedResult.tagNames
          : selectedResult.tagNames.filter((name) => tagActions[relationKey(name)] === "exclude");
      if (selectedResult?.sourceKind === "scraper") {
        if (!selectedResult.scrapeAttemptId) throw new TaggerPreconditionError("No scraper attempt selected");
        return scrapeAttempts.apply(
          selectedResult.scrapeAttemptId,
          buildScraperVideoApplyRequest(selectedResult, video, state, taggerConfig),
        );
      }

      // Remote IDs keep same-name performer identities independent. Send every non-default choice so
      // an exclusion for one disambiguation can never affect another performer with the same name.
      const performerOverrides = performerChoices.flatMap((choice) => {
        const action = performerActions[relationKey(choice.key)] ?? "exclude";
        if (action === "exclude")
          return [{ remoteId: choice.candidate.remoteId, name: choice.candidate.name, action: "skip" }];
        if (action === "create")
          return [{ remoteId: choice.candidate.remoteId, name: choice.candidate.name, action: "create" }];
        if (choice.candidate.localId != null)
          return [
            {
              remoteId: choice.candidate.remoteId,
              name: choice.candidate.name,
              action: "existing",
              localId: choice.candidate.localId,
            },
          ];
        return [];
      });
      const tagOverrides = selectedResult.tagCandidates.some((tag) => tagActions[relationKey(tag.name)] === "create")
        ? selectedResult.tagCandidates
            .filter((t) => tagActions[relationKey(t.name)] === "create")
            .map((t) => ({ remoteId: t.remoteId, name: t.name, action: "create" }))
        : undefined;
      const studioOverride =
        state?.forceIncludeStudio && selectedResult.studioCandidate
          ? {
              remoteId: selectedResult.studioCandidate.remoteId,
              name: selectedResult.studioCandidate.name,
              action: "create",
            }
          : undefined;

      const importReq: MetadataServerVideoImportRequest = {
        endpoint: selectedResult.endpoint,
        videoId: selectedResult?.id ?? "",
        setCoverImage: getVideoImageReplace(video, selectedResult, state, taggerConfig),
        // When the user explicitly chose Replace, overwrite even an explicitly set cover.
        overwriteExplicitCover: getVideoImageReplace(video, selectedResult, state, taggerConfig),
        setTags: taggerConfig.setTags && collectionModes.tags !== "skip",
        setPerformers: taggerConfig.setPerformers && collectionModes.performers !== "skip",
        setStudio: taggerConfig.setStudio && collectionModes.studio !== "skip",
        onlyExistingTags: taggerConfig.onlyExistingTags,
        onlyExistingPerformers: taggerConfig.onlyExistingPerformers,
        onlyExistingStudio: taggerConfig.onlyExistingStudio,
        markOrganized: taggerConfig.markOrganized,
        // The preview already dropped the excluded genders; send the same selection the filter above
        // used, so a performer it hides can never be written by the parts of the import the overrides
        // do not cover. Omitted, and only omitted, when nothing is filtered.
        performerGenders: allowedGenderKeys ? taggerConfig.performerGenders : undefined,
        excludedTagNames: excludedTags.length > 0 ? excludedTags : undefined,
        performerOverrides: performerOverrides.length > 0 ? performerOverrides : undefined,
        tagOverrides,
        studioOverride,
        fieldStrategies: buildVideoFieldStrategies(video, selectedResult, state, taggerConfig),
        ...relationshipEditFields(state),
      };
      return videos.importFromMetadataServer(video.id, importReq);
    },
    // The row and the Apply all summary both report this failure with the server's own wording, so
    // the app-wide notice would be a third, vaguer account of the same thing.
    meta: { suppressGlobalError: true },
    // A retry starts from a clean slate, so a stale reason cannot sit beside a fresh attempt.
    onMutate: () => onUpdateState({ error: undefined }),
    // React Query routes anything thrown in here to onError, which would report an import that has
    // already landed as a failure and invite the user to write it a second time. The whole body is
    // guarded, not just the refresh: a 204 makes `result` undefined, and reading a warning off it
    // would throw before the row was ever marked saved.
    onSuccess: async (result) => {
      try {
        const importWarnings =
          result && typeof result === "object" && "importWarnings" in result ? result.importWarnings : undefined;
        onUpdateState({
          saved: true,
          warning: importWarnings && importWarnings.length > 0 ? importWarnings.join(" ") : undefined,
        });
        await invalidateVideoMetadataQueries(queryClient, video.id);
      } catch {
        // The import itself succeeded, so the row stays saved and a stale list is the lesser problem.
        onUpdateState({ saved: true });
      }
    },
    // Apply all deliberately swallows a rejection so one row cannot abandon the batch, so the reason
    // has to be recorded here: without this the row keeps its pending changes and looks untouched.
    onError: (err) => {
      onUpdateState({ error: taggerFailureReason(err) });
    },
  });

  // mutateAsync keeps its identity across renders, so the registration only changes when whether this row
  // has something to apply does.
  const applyImport = importMut.mutateAsync;
  const canApply = Boolean(selectedResult) && !state?.saved;
  useEffect(() => {
    if (!onRegisterApply) return;
    onRegisterApply(video.id, canApply ? () => applyImport() : null);
    return () => onRegisterApply(video.id, null);
  }, [applyImport, onRegisterApply, video.id, canApply]);

  const submitEndpoint = source?.kind === "metadata-server" ? source.endpoint : undefined;
  const normalizedSubmitEndpoint = normalizeEndpoint(submitEndpoint);
  const hasRemoteIdForEndpoint =
    Boolean(normalizedSubmitEndpoint) &&
    video.remoteIds.some((remote) => normalizeEndpoint(remote.endpoint) === normalizedSubmitEndpoint);
  const hasSavedMetadataServerMatchForEndpoint =
    Boolean(normalizedSubmitEndpoint) &&
    Boolean(state?.saved) &&
    selectedResult?.sourceKind === "metadata-server" &&
    normalizeEndpoint(selectedResult.endpoint) === normalizedSubmitEndpoint;
  const canSubmitFingerprints =
    source?.kind === "metadata-server" && (hasRemoteIdForEndpoint || hasSavedMetadataServerMatchForEndpoint);
  const shouldHighlightFingerprintSubmit = canSubmitFingerprints;

  const submitDraftMut = useMetadataServerDraftSubmit((endpoint) =>
    videos.submitMetadataServerDraft(video.id, endpoint),
  );

  const submitFingerprintsMut = useMutation<void, Error>({
    meta: { suppressGlobalError: true },
    mutationFn: () => {
      if (!submitEndpoint) throw new Error("Select a metadata-server source first.");
      return videos.submitFingerprints(video.id, submitEndpoint);
    },
  });

  return (
    <div className={`px-3 py-2.5 ${selected ? "bg-accent/5" : ""}`}>
      {/* Wraps: thumbnail + title, the query beside them when there is room, and every result full width. */}
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        {onSelect && (
          <button
            type="button"
            onClick={(event) => onSelect(video.id, toggleOptionsFromEvent(event))}
            className={`flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
            aria-label={selected ? "Deselect video" : "Select video"}
            title={selected ? "Deselect" : "Select"}
          >
            <Check className="h-3 w-3" />
          </button>
        )}
        {/* Video preview */}
        <a
          {...videoLinkProps}
          className="video-card-preview-trigger group/video flex min-w-0 flex-[1_1_14rem] items-center gap-2.5"
          title={`Open video ${video.title || file?.basename || "Untitled"}`}
        >
          <div className="w-24 shrink-0">
            <VideoPreviewThumbnail
              video={video}
              fit={videoPreviewObjectFit}
              surface="list"
              coverWidth={640}
              className="rounded bg-card"
            >
              {file && file.duration > 0 && (
                <span className="video-specs-overlay absolute bottom-0.5 right-0.5 z-[5] rounded bg-black/70 px-0.5 text-[8px] text-white transition-opacity">
                  {formatDuration(file.duration)}
                </span>
              )}
            </VideoPreviewThumbnail>
          </div>
          <div className="flex min-w-0 flex-col">
            <span className="truncate text-xs font-medium leading-snug text-accent group-hover/video:underline">
              {video.title || file?.basename || "Untitled"}
            </span>
            <span className="truncate text-[11px] leading-snug text-muted">
              {[video.studioName, file && getResolutionLabel(file.width, file.height)].filter(Boolean).join(" · ")}
            </span>
          </div>
        </a>

        {/* Search + Results: laid out by the row's own flex so the query can sit beside the title */}
        <div className="contents">
          {detailMode && (
            <div className="w-full">
              <RemoteRefreshButtons
                remoteIds={video.remoteIds}
                servers={metadataServers}
                busyEndpoint={refreshBusyEndpoint}
                onRefresh={handleRefreshFromRemote}
              />
            </div>
          )}
          {isScraperSource && (
            <div className="flex w-full flex-wrap items-center gap-1.5">
              <select
                value={scraperInputKind}
                onChange={(event) => onScraperInputKindChange(event.target.value as InputKind)}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              >
                <option value="url" disabled={!supportsScrapeKind(source.scraper, "url")}>
                  URL
                </option>
                <option value="name" disabled={!supportsScrapeKind(source.scraper, "name")}>
                  Title
                </option>
                <option value="fragment" disabled={!supportsScrapeKind(source.scraper, "fragment")}>
                  Fragment
                </option>
              </select>
              {scraperInputKind === "url" && videoUrls.length > 0 ? (
                <select
                  value={selectedUrlOption}
                  onChange={(event) => {
                    if (event.target.value !== "__custom") {
                      onQueryChange(event.target.value);
                    }
                  }}
                  className="min-w-0 max-w-full flex-1 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                >
                  <option value="__custom">Custom URL</option>
                  {videoUrls.map((url) => (
                    <option key={url} value={url}>
                      {url}
                    </option>
                  ))}
                </select>
              ) : null}
            </div>
          )}
          {/* Search input — inline and compact */}
          <div className="flex min-w-0 flex-[1_1_100%] gap-1.5 md:flex-[1_1_22rem]">
            {isFragmentInput ? (
              <textarea
                value={query}
                onChange={(e) => onQueryChange(e.target.value)}
                rows={detailMode ? 8 : 3}
                placeholder={searchPlaceholder}
                className="flex-1 min-w-0 bg-input border border-border rounded pl-2 pr-2 py-1 font-mono text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
              />
            ) : (
              <input
                type="text"
                value={query}
                onChange={(e) => onQueryChange(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && onSearch()}
                placeholder={searchPlaceholder}
                className="flex-1 min-w-0 bg-input border border-border rounded pl-2 pr-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
              />
            )}
            <button
              type="button"
              onClick={onSearch}
              disabled={state?.loading}
              aria-label={textSearchLabel}
              title={textSearchLabel}
              // Stretch to the one-line input's height; beside the multi-line fragment box, stay compact at the top.
              className={`flex shrink-0 items-center rounded bg-accent px-2 py-1 text-white hover:bg-accent-hover disabled:opacity-60 ${
                isFragmentInput ? "h-fit" : ""
              }`}
            >
              {state?.loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
            </button>
            {source?.kind === "metadata-server" && (
              // The row's second search mode, so it sits beside the first rather than in the menu. It never
              // reads the query box, which is what keeps the two visibly independent.
              <button
                type="button"
                onClick={onSearchFingerprints}
                disabled={state?.loading}
                aria-label="Identify by file content"
                title="Identify by file content (fingerprints). Ignores the search text."
                className="flex shrink-0 items-center rounded border border-border bg-surface px-1.5 text-muted hover:border-accent/40 hover:text-accent disabled:opacity-60"
              >
                <Fingerprint className="h-3.5 w-3.5" />
              </button>
            )}
            {onDismiss && (
              // Sits beside Search so a row can be cleared whether or not it found a match.
              <button
                type="button"
                onClick={onDismiss}
                aria-label="Dismiss video"
                title="Dismiss video from this search session. It will be included in future search sessions."
                className={`flex shrink-0 items-center rounded border border-border bg-surface px-1.5 text-muted hover:border-red-500/40 hover:text-red-400 ${
                  isFragmentInput ? "h-fit py-1" : ""
                }`}
              >
                <X className="h-3.5 w-3.5" />
              </button>
            )}
            {source?.kind === "metadata-server" && (
              // The rare actions, the two submissions, live behind one menu so the row shows its query and its
              // two search modes and nothing more.
              <DismissibleMenu className="relative shrink-0">
                <summary
                  role="button"
                  aria-label="More actions"
                  title="More actions"
                  className={`flex h-full cursor-pointer list-none items-center rounded border px-1.5 text-muted hover:text-foreground [&::-webkit-details-marker]:hidden ${
                    shouldHighlightFingerprintSubmit
                      ? "border-accent/40 bg-accent/10 text-accent"
                      : "border-border bg-surface"
                  }`}
                >
                  <MoreHorizontal className="h-3.5 w-3.5" />
                </summary>
                <div className="absolute right-0 z-30 mt-1 w-64 overflow-hidden rounded border border-border bg-card shadow-xl">
                  <button
                    type="button"
                    onClick={(event) => {
                      event.currentTarget.closest("details")?.removeAttribute("open");
                      submitFingerprintsMut.mutate();
                    }}
                    disabled={submitFingerprintsMut.isPending || !canSubmitFingerprints}
                    title={
                      canSubmitFingerprints
                        ? "Submit your fingerprints for this video to the metadata server"
                        : "Link this video to a metadata-server entry before submitting fingerprints"
                    }
                    className={`flex w-full items-center gap-2 px-3 py-2 text-left text-xs hover:bg-surface disabled:opacity-60 ${
                      shouldHighlightFingerprintSubmit ? "text-accent" : "text-foreground"
                    }`}
                  >
                    {submitFingerprintsMut.isPending ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    ) : (
                      <Upload className="h-3.5 w-3.5 text-muted" />
                    )}
                    Submit fingerprints
                  </button>
                  <button
                    type="button"
                    onClick={(event) => {
                      event.currentTarget.closest("details")?.removeAttribute("open");
                      submitDraftMut.submitDraft(submitEndpoint);
                    }}
                    disabled={submitDraftMut.isPending}
                    title="Submit this video as a draft entry to the metadata server"
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs text-foreground hover:bg-surface disabled:opacity-60"
                  >
                    {submitDraftMut.isPending ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    ) : (
                      <CloudUpload className="h-3.5 w-3.5 text-muted" />
                    )}
                    Submit as draft
                  </button>
                </div>
              </DismissibleMenu>
            )}
          </div>

          {submitFingerprintsMut.isError && (
            <p className="w-full text-xs text-red-400">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {submitFingerprintsMut.error.message}
            </p>
          )}
          {submitFingerprintsMut.isSuccess && (
            <p className="w-full text-xs text-green-400">
              <Check className="w-3 h-3 inline mr-1" />
              Fingerprints submitted to the metadata server.
            </p>
          )}
          {submitDraftMut.isError && (
            <p className="w-full text-xs text-red-400">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {submitDraftMut.error.message}
            </p>
          )}
          {submitDraftMut.isSuccess && (
            <p className="w-full text-xs text-green-400">
              <Check className="w-3 h-3 inline mr-1" />
              {submitDraftMut.data.draftUrl ? (
                <>
                  Video draft submitted.{" "}
                  <a
                    href={submitDraftMut.data.draftUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 text-accent hover:underline"
                  >
                    Open draft
                    <ExternalLink className="h-3 w-3" />
                  </a>
                </>
              ) : (
                <>Video draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.</>
              )}
            </p>
          )}

          {/* Error */}
          {state?.error && (
            <p className="w-full text-xs text-red-400">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {state.error}
            </p>
          )}

          {/* No results */}
          {state?.results && state.results.length === 0 && (
            <p className="w-full text-xs text-muted">No matches found.</p>
          )}

          {/* Results */}
          {state?.results && state.results.length > 0 && (
            <TaggerResults
              video={video}
              results={enrichedResults ?? state.results}
              tagMatchInfo={tagMatchInfo}
              performerMatchInfo={performerMatchInfo}
              selectedIndex={state.selectedIndex ?? 0}
              onSelect={(i) =>
                onUpdateState(
                  i === (state.selectedIndex ?? 0)
                    ? { selectedIndex: i }
                    : {
                        selectedIndex: i,
                        fieldStrategies: undefined,
                        collectionModes: undefined,
                        excludedPerformers: undefined,
                        excludedTags: undefined,
                        skipStudio: undefined,
                        forceIncludedPerformers: undefined,
                        forceIncludedTags: undefined,
                        forceIncludeStudio: undefined,
                        tagEdits: undefined,
                        performerEdits: undefined,
                      },
                )
              }
              onSave={() => importMut.mutate()}
              saving={importMut.isPending}
              saved={state.saved}
              localDuration={file?.duration}
              excludedPerformers={state.excludedPerformers ?? new Set()}
              excludedTags={state.excludedTags ?? new Set()}
              skipStudio={state.skipStudio ?? false}
              forceIncludedPerformers={state.forceIncludedPerformers ?? new Set()}
              forceIncludedTags={state.forceIncludedTags ?? new Set()}
              forceIncludeStudio={state.forceIncludeStudio ?? false}
              fieldStrategies={selectedResult ? getVideoFieldStrategies(video, selectedResult, state) : {}}
              collectionModes={selectedResult ? getVideoCollectionModes(selectedResult, state, taggerConfig) : {}}
              onFieldStrategyChange={(field, strategy) => {
                if (!selectedResult) return;
                onUpdateState({
                  fieldStrategies: { ...getVideoFieldStrategies(video, selectedResult, state), [field]: strategy },
                });
              }}
              onCollectionModeChange={(field, mode) => {
                if (!selectedResult) return;
                onUpdateState({
                  collectionModes: { ...getVideoCollectionModes(selectedResult, state, taggerConfig), [field]: mode },
                });
              }}
              onTogglePerformer={(names) => {
                // Several chips can change in one review action, so every toggle lands in one state update.
                const forceIncluded = new Set(state.forceIncludedPerformers ?? []);
                const excluded = new Set(state.excludedPerformers ?? []);
                for (const name of Array.isArray(names) ? names : [names]) {
                  const perf =
                    selectedResult == null
                      ? undefined
                      : getPerformerChoices(selectedResult).find((choice) => choice.key === name)?.candidate;
                  const willSkipByDefault = taggerConfig.onlyExistingPerformers && perf && !perf.existsLocally;
                  const target = willSkipByDefault ? forceIncluded : excluded;
                  if (target.has(name)) target.delete(name);
                  else target.add(name);
                }
                onUpdateState({ forceIncludedPerformers: forceIncluded, excludedPerformers: excluded });
              }}
              onToggleTag={(names) => {
                const forceIncluded = new Set(state.forceIncludedTags ?? []);
                const excluded = new Set(state.excludedTags ?? []);
                for (const name of Array.isArray(names) ? names : [names]) {
                  const tag = selectedResult?.tagCandidates.find((t) => t.name === name);
                  const willSkipByDefault = taggerConfig.onlyExistingTags && tag && !tag.existsLocally;
                  const target = willSkipByDefault ? forceIncluded : excluded;
                  if (target.has(name)) target.delete(name);
                  else target.add(name);
                }
                onUpdateState({ forceIncludedTags: forceIncluded, excludedTags: excluded });
              }}
              onToggleStudio={() => {
                const willSkipByDefault =
                  taggerConfig.onlyExistingStudio &&
                  selectedResult?.studioCandidate &&
                  !selectedResult.studioCandidate.existsLocally;
                if (willSkipByDefault) {
                  onUpdateState({ forceIncludeStudio: !state.forceIncludeStudio });
                } else {
                  onUpdateState({ skipStudio: !state.skipStudio });
                }
              }}
              tagEdits={state.tagEdits}
              performerEdits={state.performerEdits}
              onRelationshipEditsChange={(key, edits) =>
                onUpdateState(key === "tags" ? { tagEdits: edits } : { performerEdits: edits })
              }
              taggerConfig={taggerConfig}
              coverComparison={coverComparison}
            />
          )}

          {/* Saved indicator */}
          {state?.saved && (
            <div className="flex w-full items-center gap-1 text-xs text-green-400">
              <Check className="w-3.5 h-3.5" />
              Saved successfully
            </div>
          )}
          {state?.warning && (
            <div className="flex w-full items-start gap-1 text-xs text-amber-300">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>Saved with warnings: {state.warning}</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* ── Tagger Results ── */

interface TaggerResultsProps {
  video: Video;
  results: UnifiedVideoMatch[];
  tagMatchInfo?: Record<string, string>;
  performerMatchInfo?: Record<string, string>;
  selectedIndex: number;
  onSelect: (index: number) => void;
  onSave: () => void;
  saving?: boolean;
  saved?: boolean;
  localDuration?: number;
  excludedPerformers: Set<string>;
  excludedTags: Set<string>;
  skipStudio: boolean;
  forceIncludedPerformers: Set<string>;
  forceIncludedTags: Set<string>;
  forceIncludeStudio: boolean;
  fieldStrategies: Record<string, VideoFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange: (field: string, strategy: VideoFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onTogglePerformer: (names: string | string[]) => void;
  onToggleTag: (names: string | string[]) => void;
  onToggleStudio: () => void;
  tagEdits?: TaggerRelationshipEdits;
  performerEdits?: TaggerRelationshipEdits;
  onRelationshipEditsChange: (key: TaggerRelationshipKey, edits: TaggerRelationshipEdits) => void;
  taggerConfig: TaggerConfig;
  /** Pixel verdict on the selected result's cover; absent until it has been asked for and answered. */
  coverComparison?: VideoCoverComparison;
}

function TaggerResults({
  video,
  results,
  tagMatchInfo,
  performerMatchInfo,
  selectedIndex,
  onSelect,
  onSave,
  saving,
  saved,
  localDuration,
  excludedPerformers,
  excludedTags,
  skipStudio,
  forceIncludedPerformers,
  forceIncludedTags,
  forceIncludeStudio,
  fieldStrategies,
  collectionModes,
  onFieldStrategyChange,
  onCollectionModeChange,
  onTogglePerformer,
  onToggleTag,
  onToggleStudio,
  tagEdits,
  performerEdits,
  onRelationshipEditsChange,
  taggerConfig,
  coverComparison,
}: TaggerResultsProps) {
  const current = results[selectedIndex] ? selectedIndex : 0;
  const row = (result: UnifiedVideoMatch, i: number) => (
    <TaggerResultRow
      key={`${result.endpoint}-${result.id}`}
      video={video}
      result={result}
      tagMatchInfo={tagMatchInfo}
      performerMatchInfo={performerMatchInfo}
      isSelected={i === current}
      showSelector={results.length > 1}
      onClick={() => onSelect(i)}
      onSave={i === current ? onSave : undefined}
      saving={i === current ? saving : false}
      saved={saved}
      localDuration={localDuration}
      excludedPerformers={excludedPerformers}
      excludedTags={excludedTags}
      skipStudio={skipStudio}
      forceIncludedPerformers={forceIncludedPerformers}
      forceIncludedTags={forceIncludedTags}
      forceIncludeStudio={forceIncludeStudio}
      fieldStrategies={fieldStrategies}
      collectionModes={collectionModes}
      onFieldStrategyChange={i === current ? onFieldStrategyChange : undefined}
      onCollectionModeChange={i === current ? onCollectionModeChange : undefined}
      onTogglePerformer={i === current ? onTogglePerformer : undefined}
      onToggleTag={i === current ? onToggleTag : undefined}
      onToggleStudio={i === current ? onToggleStudio : undefined}
      tagEdits={tagEdits}
      performerEdits={performerEdits}
      onRelationshipEditsChange={i === current ? onRelationshipEditsChange : undefined}
      taggerConfig={taggerConfig}
      coverComparison={i === current ? coverComparison : undefined}
    />
  );
  const others = results.map((result, i) => ({ result, i })).filter(({ i }) => i !== current);
  // The chosen match is the review; the alternatives stay one click away instead of stacking below it.
  return (
    <div className="flex w-full flex-col gap-1.5">
      {results[current] ? row(results[current], current) : null}
      {others.length > 0 && (
        <details className="group/others">
          <summary className="inline-flex cursor-pointer list-none items-center gap-1 px-1 text-xs text-accent hover:underline [&::-webkit-details-marker]:hidden">
            {others.length} other {others.length === 1 ? "match" : "matches"}
            <ChevronDown className="h-3.5 w-3.5 transition-transform group-open/others:rotate-180" />
          </summary>
          <div className="mt-1.5 flex flex-col gap-1.5">{others.map(({ result, i }) => row(result, i))}</div>
        </details>
      )}
    </div>
  );
}

function TaggerResultRow({
  video,
  result,
  tagMatchInfo,
  performerMatchInfo,
  isSelected,
  showSelector,
  onClick,
  onSave,
  saving,
  saved,
  localDuration,
  excludedPerformers,
  excludedTags,
  forceIncludedPerformers,
  forceIncludedTags,
  fieldStrategies,
  collectionModes,
  onFieldStrategyChange,
  onCollectionModeChange,
  onTogglePerformer,
  onToggleTag,
  tagEdits,
  performerEdits,
  onRelationshipEditsChange,
  taggerConfig,
  coverComparison,
}: {
  video: Video;
  result: MetadataServerVideoMatch;
  tagMatchInfo?: Record<string, string>;
  performerMatchInfo?: Record<string, string>;
  isSelected: boolean;
  showSelector: boolean;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
  localDuration?: number;
  excludedPerformers: Set<string>;
  excludedTags: Set<string>;
  skipStudio: boolean;
  forceIncludedPerformers: Set<string>;
  forceIncludedTags: Set<string>;
  forceIncludeStudio: boolean;
  fieldStrategies: Record<string, VideoFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange?: (field: string, strategy: VideoFieldStrategy) => void;
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
  onTogglePerformer?: (names: string | string[]) => void;
  onToggleTag?: (names: string | string[]) => void;
  onToggleStudio?: () => void;
  tagEdits?: TaggerRelationshipEdits;
  performerEdits?: TaggerRelationshipEdits;
  onRelationshipEditsChange?: (key: TaggerRelationshipKey, edits: TaggerRelationshipEdits) => void;
  taggerConfig: TaggerConfig;
  coverComparison?: VideoCoverComparison;
}) {
  const metadataServers = useOptionalAppConfig()?.config?.scraping?.metadataServers;
  // Accept-all is the common case, so the review opens as a list of facts; the full side-by-side
  // rows are one click away for per-item chips and hand edits.
  const [adjusting, setAdjusting] = useState(false);
  const durationDiff =
    localDuration != null && result.duration != null ? Math.abs(localDuration - result.duration) : undefined;
  const durationMatch = durationDiff != null && durationDiff < 5;
  const currentTagNames = getVideoTagNames(video);
  const performerChoices = getPerformerChoices(result);
  const performerChoiceKeys = performerChoices.map((choice) => choice.key);
  const currentPerformerChoiceKeys = getCurrentPerformerChoiceKeys(video, performerChoices);
  const existingTagNames = result.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name);
  const existingPerformerChoiceKeys = performerChoices
    .filter((choice) => choice.candidate.existsLocally)
    .map((choice) => choice.key);
  const tagActions = buildVideoRelationActionMap(
    result.tagNames,
    currentTagNames,
    existingTagNames,
    excludedTags,
    forceIncludedTags,
    !taggerConfig.onlyExistingTags,
  );
  const performerActions = buildVideoRelationActionMap(
    performerChoiceKeys,
    currentPerformerChoiceKeys,
    existingPerformerChoiceKeys,
    excludedPerformers,
    forceIncludedPerformers,
    !taggerConfig.onlyExistingPerformers,
  );
  const reviewInput: TaggerReviewInput = {
    video,
    result,
    sourceName:
      result.serverName ||
      (result.endpoint ? metadataServerLabel(result.endpoint, metadataServers ?? []) : "the scraper"),
    metadataServers,
    fieldStrategies,
    imageReplace: (fieldStrategies.image ?? defaultVideoImageStrategy(video, taggerConfig)) === "overwrite",
    coverComparison,
    collectionModes,
    showStudio: taggerConfig.setStudio,
    showTags: taggerConfig.setTags,
    showPerformers: taggerConfig.setPerformers,
    currentTagNames,
    existingTagNames,
    tagActions,
    tagMatchInfo,
    performerChoices,
    currentPerformerChoiceKeys,
    performerActions,
    performerMatchInfo,
    tagEdits,
    performerEdits,
  };
  const review = isSelected ? buildTaggerReview(reviewInput) : null;
  const summary = review ? summarizeDiff(review.fields, review.source, review.target, review.selection) : null;
  const handleSelectionChange = (next: DiffSelection) => {
    if (!review) return;
    applyTaggerSelectionChange(reviewInput, review.selection, next, {
      onFieldStrategyChange,
      onCollectionModeChange,
      onToggleTag,
      onTogglePerformer,
      onRelationshipEditsChange,
    });
  };
  const matchedAlgorithms = [...new Set(result.fingerprintAlgorithms.map((algorithm) => algorithm.toUpperCase()))];
  const fingerprintNote =
    result.fingerprints.length === 0
      ? null
      : result.matchCount > 0
        ? `${result.matchCount} fingerprint ${result.matchCount === 1 ? "match" : "matches"}${
            matchedAlgorithms.length ? ` (${matchedAlgorithms.join(", ")})` : ""
          }`
        : "No fingerprint matches";
  const identity = [result.studioName, result.date].filter(Boolean).join(" · ");
  const facts = [
    ...(isSelected
      ? []
      : [
          identity || null,
          result.code ? (
            <span key="code" className="font-mono text-muted">
              {result.code}
            </span>
          ) : null,
        ]),
    result.duration != null ? (
      <span key="duration">
        {formatDuration(result.duration)}
        {durationDiff != null && (
          <span
            className={durationMatch ? " text-green-400" : durationDiff < 30 ? " text-yellow-400" : " text-red-400"}
          >
            {" "}
            {durationDiff < 1 ? "exact" : `${Math.round(durationDiff)}s off`}
          </span>
        )}
      </span>
    ) : null,
    fingerprintNote ? (
      <span
        key="fingerprints"
        title={`Remote fingerprints: ${[...new Set(result.fingerprints.map((fp) => fp.algorithm.toUpperCase()))].join(", ")}`}
        className={`inline-flex items-center gap-1 ${result.matchCount > 0 ? "text-green-400" : "text-muted"}`}
      >
        <Fingerprint className="h-3 w-3" />
        {fingerprintNote}
      </span>
    ) : null,
    !isSelected && performerChoices.length > 0 ? performerChoices.map((choice) => choice.label).join(", ") : null,
  ].filter(Boolean);

  return (
    <div
      onClick={onClick}
      className={`rounded-lg border transition-colors ${
        isSelected ? "border-accent/70 bg-card" : "cursor-pointer border-border bg-surface hover:border-accent/50"
      }`}
    >
      {/* Header: what matched, in one line of facts */}
      <div
        role={showSelector && !isSelected ? "button" : undefined}
        tabIndex={showSelector && !isSelected ? 0 : undefined}
        aria-label={showSelector && !isSelected ? `Use ${result.title || "this match"}` : undefined}
        onKeyDown={(event) => {
          if (showSelector && !isSelected && (event.key === "Enter" || event.key === " ")) {
            event.preventDefault();
            onClick();
          }
        }}
        className="flex flex-wrap items-center gap-2.5 px-3 py-2 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent"
      >
        {showSelector && (
          <div
            aria-hidden="true"
            className={`flex h-4 w-4 shrink-0 items-center justify-center rounded-full border-2 ${isSelected ? "border-accent" : "border-border"}`}
          >
            {isSelected && <div className="h-2 w-2 rounded-full bg-accent" />}
          </div>
        )}
        {!isSelected && result.imageUrl && (
          <img src={result.imageUrl} alt="" className="h-9 w-16 shrink-0 rounded object-cover" loading="lazy" />
        )}
        {isSelected && review ? (
          <CoverPanel
            review={review}
            onChange={handleSelectionChange}
            disabled={saving}
            coverComparison={coverComparison}
          />
        ) : null}
        <div className="min-w-0 flex-1 self-start">
          <p
            className={`text-foreground ${isSelected ? "text-base font-semibold leading-snug" : "truncate text-[13px] font-semibold"}`}
          >
            {result.title || "Untitled"}
          </p>
          {isSelected && identity ? <p className="text-sm text-secondary">{identity}</p> : null}
          <div className="mt-0.5 flex flex-wrap items-center gap-x-2.5 gap-y-0.5 text-[11px] text-secondary">
            {facts.map((fact, index) => (
              <span key={index} className="min-w-0 truncate">
                {fact}
              </span>
            ))}
          </div>
        </div>
      </div>

      {/* Expanded review — only for the selected result */}
      {review && !saved && (
        <div className="border-t border-border" onClick={(event) => event.stopPropagation()}>
          {adjusting ? (
            <div className="px-3 py-3">
              <MetadataDiff
                fields={review.fields}
                source={review.source}
                target={review.target}
                value={review.selection}
                onChange={handleSelectionChange}
                disabled={saving}
              />
            </div>
          ) : (
            <div className="py-1">
              <MetadataDiffSummary
                // The cover has its own panel, so it is not a row here — except when it is unchanged,
                // where it belongs among the unchanged labels the footer is already counting it in.
                fields={review.fields.filter(
                  (field) => field.key !== "image" || scalarStatus(field, review.source, review.target) === "identical",
                )}

                source={review.source}
                target={review.target}
                value={review.selection}
                onChange={handleSelectionChange}
                disabled={saving}
              />
            </div>
          )}
          {/* Summary reads left to right; the actions that act on it are grouped at the right edge. */}
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-border bg-surface/60 px-3 py-2">
            {summary ? (
              <span className="hidden min-w-0 flex-1 truncate text-[11px] text-muted sm:inline">
                {summary.changes.map((change) => change.text).join(" · ")}
              </span>
            ) : null}
            <div className="ml-auto flex items-center gap-3">
              <button
                type="button"
                aria-expanded={adjusting}
                onClick={() => setAdjusting((current) => !current)}
                className="text-xs text-accent hover:underline"
              >
                {adjusting ? "Done adjusting" : "Adjust…"}
              </button>
              {onSave && (
                <button
                  onClick={onSave}
                  disabled={saving}
                  className="flex items-center gap-1.5 rounded px-4 py-1.5 text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60"
                >
                  {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
                  {summary?.changeCount
                    ? `Apply ${summary.changeCount} ${summary.changeCount === 1 ? "change" : "changes"}`
                    : "Apply"}
                </button>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/** The video's cover decision, read from the review and shown through the shared cover panel. */
function CoverPanel({
  review,
  onChange,
  disabled,
  coverComparison,
}: {
  review: ReturnType<typeof buildTaggerReview>;
  onChange: (next: DiffSelection) => void;
  disabled?: boolean;
  coverComparison?: VideoCoverComparison;
}) {
  const field = review.fields.find((entry) => entry.key === "image");
  if (!field) return null;
  return (
    <ReviewCoverPanel
      status={scalarStatus(field, review.source, review.target)}
      note={coverComparisonNote(coverComparison)}
      chosen={review.selection.image === "source" ? "source" : "target"}
      currentUrl={review.target.values.image ? String(review.target.values.image) : null}
      candidates={[String(review.source.values.image ?? "")]}
      incomingLabel={review.source.sentenceLabel ?? review.source.label}
      onChoose={(side) => onChange({ ...review.selection, image: side })}
      disabled={disabled}
    />
  );
}
