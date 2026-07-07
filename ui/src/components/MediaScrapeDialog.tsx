import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink, Loader2, Search, Sparkles, X } from "lucide-react";
import { scrapeAttempts, system } from "../api/client";
import type { ScrapeAttempt, ScraperSummary } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import {
  buildMatchInfo,
  buildRelationActionMap,
  buildRelationSelectionPayload,
  relationKey,
  ScrapeRelationChoices,
  type ScrapeRelationActionMap,
} from "./ScrapeRelationChoices";
import type { CollectionMode, InputKind, ScrapeApplyPreferences } from "./videoScrapeUtils";
import {
  DEFAULT_COLLECTION_MODES,
  findDefaultKind,
  findPreferredScraperId,
  getNamedList,
  getString,
  getStringList,
  listsEqual,
  loadScrapeApplyPreferences,
  matchesUrlPattern,
  normalizeVideoDate,
  parseJsonObject,
  parseJsonObjectArray,
  saveScrapeApplyPreferences,
  sortScrapersForVideo,
  supportsScrapeKind,
} from "./videoScrapeUtils";

type MediaEntityType = "audio" | "text" | "image";

interface MediaScrapeEntity {
  id: number;
  title?: string;
  code?: string;
  details?: string;
  creator?: string;
  date?: string;
  studioName?: string;
  urls: string[];
  tags: { name: string }[];
  performers: { name: string }[];
  files: { basename: string; path: string }[];
  organized: boolean;
}

interface MediaReviewData {
  title?: string;
  code?: string;
  details?: string;
  creator?: string;
  date?: string;
  studio?: string;
  urls: string[];
  tags: string[];
  performers: string[];
  raw: Record<string, unknown> | null;
}

interface MediaApplyPlan {
  currentData: MediaReviewData;
  scrapedData: MediaReviewData | null;
  replaceFields: string[];
  collectionModes: Record<string, CollectionMode>;
}

interface Props {
  open: boolean;
  onClose: () => void;
  entityType: MediaEntityType;
  entity: MediaScrapeEntity;
}

const ENTITY_LABELS: Record<MediaEntityType, string> = {
  audio: "Audio",
  text: "Text",
  image: "Image",
};

const ENTITY_LIST_KEYS: Record<MediaEntityType, string> = {
  audio: "audios",
  text: "texts",
  image: "images",
};

const URL_PLACEHOLDERS: Record<MediaEntityType, string> = {
  audio: "https://example.com/audio/...",
  text: "https://example.com/story/...",
  image: "https://example.com/image/...",
};

function formatAttemptTime(value: string) {
  try {
    return new Date(value).toLocaleString();
  } catch {
    return value;
  }
}

function statusTone(status: string) {
  switch (status.toLowerCase()) {
    case "success":
    case "applied":
      return "border-emerald-800/60 bg-emerald-950/30 text-emerald-300";
    case "appliedpartial":
      return "border-amber-800/60 bg-amber-950/30 text-amber-300";
    default:
      return "border-red-800/60 bg-red-950/30 text-red-300";
  }
}

function upsertReplaceField(current: string[], field: string, enabled: boolean) {
  if (enabled) {
    return current.includes(field) ? current : [...current, field];
  }

  return current.filter((value) => value !== field);
}

function normalizeTagName(value: string) {
  const trimmed = value.trim();
  if (trimmed.startsWith("[") && trimmed.endsWith("]") && trimmed.length >= 2) {
    return trimmed.slice(1, -1).trim();
  }

  return trimmed;
}

function normalizeTagList(values: string[]) {
  return values
    .map((value) => normalizeTagName(value))
    .filter(Boolean)
    .filter((value, index, items) => items.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index);
}

function getCreatorValue(entityType: MediaEntityType, raw: Record<string, unknown> | null) {
  if (entityType === "image") {
    return getString(raw, "Photographer");
  }
  return undefined;
}

function getPerformerValues(entityType: MediaEntityType, raw: Record<string, unknown> | null) {
  const explicit = getNamedList(raw, "Performers", "Performer", "PerformerNames");
  const legacyCreatorValues =
    entityType === "audio"
      ? getNamedList(raw, "Artist", "artist", "Creator", "creator", "Author", "author")
      : entityType === "text"
        ? getNamedList(raw, "Author", "author", "Creator", "creator", "Artist", "artist")
        : [];

  return [...explicit, ...legacyCreatorValues].filter((value, index, values) => values.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index);
}

function normalizeAttemptData(entityType: MediaEntityType, attempt?: ScrapeAttempt | null, rawOverride?: Record<string, unknown> | null): MediaReviewData | null {
  const raw = rawOverride ?? parseJsonObject(attempt?.resultJson);
  if (!raw) {
    return null;
  }

  return {
    title: getString(raw, "Title", "Name"),
    code: getString(raw, "Code"),
    details: getString(raw, "Details", "Description", "Synopsis"),
    creator: getCreatorValue(entityType, raw),
    date: normalizeVideoDate(getString(raw, "Date", "ReleaseDate")),
    studio: getNamedList(raw, "Studio", "StudioName")[0] ?? getString(raw, "Studio", "StudioName"),
    urls: getStringList(raw, "URLs", "Url", "URL"),
    tags: normalizeTagList(getNamedList(raw, "Tags", "Tag", "TagNames")),
    performers: getPerformerValues(entityType, raw),
    raw,
  };
}

function getAttemptCandidates(entityType: MediaEntityType, attempt?: ScrapeAttempt | null) {
  const candidatePayloads = parseJsonObjectArray(attempt?.candidateResultsJson);
  if (candidatePayloads.length > 0) {
    return candidatePayloads
      .map((payload) => normalizeAttemptData(entityType, undefined, payload))
      .filter((candidate): candidate is MediaReviewData => candidate !== null);
  }

  const single = normalizeAttemptData(entityType, attempt);
  return single ? [single] : [];
}

function normalizeSnapshot(entity: MediaScrapeEntity, entityType: MediaEntityType, attempt?: ScrapeAttempt | null): MediaReviewData {
  const snapshot = parseJsonObject(attempt?.entitySnapshotJson);
  const snapshotTags = normalizeTagList(getNamedList(snapshot, "tags"));
  const snapshotPerformers = getNamedList(snapshot, "performers");
  const snapshotUrls = getStringList(snapshot, "urls");

  return {
    title: getString(snapshot, "title") ?? entity.title,
    code: getString(snapshot, "code") ?? entity.code,
    details: getString(snapshot, "details") ?? entity.details,
    creator: entityType === "image" ? getString(snapshot, "photographer") ?? entity.creator : undefined,
    date: normalizeVideoDate(getString(snapshot, "date") ?? entity.date),
    studio: getString(snapshot, "studio") ?? entity.studioName,
    urls: snapshotUrls.length > 0 ? snapshotUrls : entity.urls,
    tags: snapshotTags.length > 0 ? snapshotTags : normalizeTagList(entity.tags.map((tag) => tag.name)),
    performers: snapshotPerformers.length > 0 ? snapshotPerformers : entity.performers.map((performer) => performer.name),
    raw: snapshot,
  };
}

function buildDefaultApplyPlan(entity: MediaScrapeEntity, entityType: MediaEntityType, attempt?: ScrapeAttempt | null, selectedPayload?: Record<string, unknown> | null): MediaApplyPlan {
  const currentData = normalizeSnapshot(entity, entityType, attempt);
  const scrapedData = normalizeAttemptData(entityType, attempt, selectedPayload);

  if (!scrapedData) {
    return {
      currentData,
      scrapedData: null,
      replaceFields: [],
      collectionModes: { ...DEFAULT_COLLECTION_MODES },
    };
  }

  const replaceFields: string[] = [];
  if (scrapedData.title && scrapedData.title !== currentData.title) replaceFields.push("title");
  if (scrapedData.code && scrapedData.code !== currentData.code) replaceFields.push("code");
  if (scrapedData.details && scrapedData.details !== currentData.details) replaceFields.push("details");

  if (entityType === "image" && scrapedData.creator && scrapedData.creator !== currentData.creator) replaceFields.push("photographer");
  if (scrapedData.date && scrapedData.date !== currentData.date) replaceFields.push("date");

  return {
    currentData,
    scrapedData,
    replaceFields,
    collectionModes: {
      studio: scrapedData.studio && scrapedData.studio !== currentData.studio ? "replace" : "skip",
      urls: scrapedData.urls.length > 0 && !listsEqual(scrapedData.urls, currentData.urls) ? "merge" : "skip",
      tags: scrapedData.tags.length > 0 && !listsEqual(scrapedData.tags, currentData.tags) ? "merge" : "skip",
      performers: scrapedData.performers.length > 0 && !listsEqual(scrapedData.performers, currentData.performers) ? "merge" : "skip",
    },
  };
}

function getEntityLabel(entityType: MediaEntityType, entity: MediaScrapeEntity) {
  return entity.title || entity.files[0]?.basename || `${ENTITY_LABELS[entityType]} ${entity.id}`;
}

function getNameSearchInput(entity: MediaScrapeEntity) {
  const raw = entity.title?.trim() || entity.files[0]?.basename?.trim() || "";
  if (!raw) {
    return "";
  }

  const sanitized = raw
    .replace(/[\\/_:|]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();

  return sanitized || raw;
}

function buildFragmentDraft(entityType: MediaEntityType, entity: MediaScrapeEntity) {
  return buildFragmentDraftForUrl(entityType, entity, entity.urls[0] ?? "");
}

function buildFragmentDraftForUrl(entityType: MediaEntityType, entity: MediaScrapeEntity, sourceUrl: string) {
  return JSON.stringify(
    {
      title: entity.title ?? "",
      name: entity.title ?? entity.files[0]?.basename ?? "",
      filename: entity.files[0]?.basename ?? "",
      path: entity.files[0]?.path ?? "",
      code: entity.code ?? "",
      details: entity.details ?? "",
      ...(entityType === "image" ? { photographer: entity.creator ?? "" } : {}),
      date: entity.date ?? "",
      url: sourceUrl,
      urls: entity.urls,
      studio: entity.studioName ?? "",
    },
    null,
    2,
  );
}

function normalizeSourceUrls(urls: string[]) {
  return urls
    .map((value) => value.trim())
    .filter(Boolean)
    .filter((value, index, items) => items.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index);
}

function getSourceUrlSortScore(scrapers: ScraperSummary[], sourceUrl: string) {
  if (!sourceUrl.trim()) {
    return 0;
  }

  return scrapers.reduce((score, scraper) => score + (matchesUrlPattern(scraper, sourceUrl) ? 1 : 0), 0);
}

function chooseInitialSourceUrl(sourceUrls: string[], scrapers: ScraperSummary[], scraperPreferences: { site: string; scraperId: string }[]) {
  if (sourceUrls.length <= 1) {
    return sourceUrls[0] ?? "";
  }

  return [...sourceUrls].sort((left, right) => {
    const leftScore = getSourceUrlSortScore(sortScrapersForVideo(scrapers, left, scraperPreferences), left);
    const rightScore = getSourceUrlSortScore(sortScrapersForVideo(scrapers, right, scraperPreferences), right);
    if (leftScore !== rightScore) {
      return rightScore - leftScore;
    }

    return sourceUrls.indexOf(left) - sourceUrls.indexOf(right);
  })[0] ?? "";
}

export function MediaScrapeDialog({ open, onClose, entityType, entity }: Props) {
  const queryClient = useQueryClient();
  const { config } = useAppConfig();
  const [preferences, setPreferences] = useState<ScrapeApplyPreferences>(() => loadScrapeApplyPreferences());
  const [selectedScraperId, setSelectedScraperId] = useState("");
  const [selectedSourceUrl, setSelectedSourceUrl] = useState("");
  const [inputKind, setInputKind] = useState<InputKind>("url");
  const [url, setUrl] = useState("");
  const [name, setName] = useState("");
  const [fragmentJson, setFragmentJson] = useState("");
  const [selectedAttempt, setSelectedAttempt] = useState<ScrapeAttempt | null>(null);
  const [selectedCandidateIndex, setSelectedCandidateIndex] = useState(0);
  const [replaceFields, setReplaceFields] = useState<string[]>([]);
  const [collectionModes, setCollectionModes] = useState<Record<string, CollectionMode>>({ ...DEFAULT_COLLECTION_MODES });
  const [tagActions, setTagActions] = useState<ScrapeRelationActionMap>({});
  const [performerActions, setPerformerActions] = useState<ScrapeRelationActionMap>({});
  const [error, setError] = useState<string | null>(null);

  const { data: scrapers = [] } = useQuery({
    queryKey: ["system-scrapers"],
    queryFn: system.listScrapers,
    enabled: open,
  });

  const { data: recentAttempts = [] } = useQuery({
    queryKey: ["scrape-attempts", entityType, entity.id],
    queryFn: () => scrapeAttempts.list({ entityType, entityId: entity.id, limit: 12 }),
    enabled: open,
  });

  const scraperPreferences = config?.scraping.scraperPreferences ?? [];
  const sourceUrls = useMemo(() => normalizeSourceUrls(entity.urls), [entity.urls]);
  const availableEntityScrapers = useMemo(
    () => scrapers.filter((scraper) => scraper.entityType.toLowerCase() === entityType),
    [entityType, scrapers],
  );
  const initialSourceUrl = useMemo(
    () => chooseInitialSourceUrl(sourceUrls, availableEntityScrapers, scraperPreferences),
    [availableEntityScrapers, scraperPreferences, sourceUrls],
  );
  const activeSourceUrl = selectedSourceUrl || initialSourceUrl;
  const entityScrapers = useMemo(
    () => sortScrapersForVideo(availableEntityScrapers, activeSourceUrl, scraperPreferences),
    [activeSourceUrl, availableEntityScrapers, scraperPreferences],
  );
  const selectedScraper = useMemo(
    () => entityScrapers.find((scraper) => scraper.id === selectedScraperId),
    [entityScrapers, selectedScraperId],
  );
  const candidateResults = useMemo(() => getAttemptCandidates(entityType, selectedAttempt), [entityType, selectedAttempt]);
  const selectedCandidate = candidateResults[selectedCandidateIndex] ?? candidateResults[0] ?? null;
  const applyPlan = useMemo(
    () => buildDefaultApplyPlan(entity, entityType, selectedAttempt, selectedCandidate?.raw ?? null),
    [entity, entityType, selectedAttempt, selectedCandidate],
  );
  const currentData = applyPlan.currentData;
  const scrapedData = applyPlan.scrapedData;
  // Ask the backend which scraped names already resolve to an existing tag/performer, using the same
  // matcher the apply path runs (performers match on name OR alias). Replaces a former client-side
  // fetch of every tag/performer, which missed alias matches and silently capped at 10k entities.
  const scrapedRelationNames = useMemo(
    () => ({ tags: scrapedData?.tags ?? [], performers: scrapedData?.performers ?? [] }),
    [scrapedData?.tags, scrapedData?.performers],
  );
  const { data: resolvedRelations } = useQuery({
    queryKey: ["scrape-dialog-resolve-relations", scrapedRelationNames],
    queryFn: () => scrapeAttempts.resolveRelations(scrapedRelationNames),
    enabled: open && (scrapedRelationNames.tags.length > 0 || scrapedRelationNames.performers.length > 0),
    staleTime: 30_000,
  });
  const existingTagNames = useMemo(() => (resolvedRelations?.tags ?? []).map((match) => match.input), [resolvedRelations]);
  const existingPerformerNames = useMemo(() => (resolvedRelations?.performers ?? []).map((match) => match.input), [resolvedRelations]);
  const tagMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.tags), [resolvedRelations]);
  const performerMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.performers), [resolvedRelations]);
  const suggestedReplaceKey = useMemo(() => applyPlan.replaceFields.join("|"), [applyPlan.replaceFields]);
  const suggestedCollectionModesKey = useMemo(() => JSON.stringify(applyPlan.collectionModes), [applyPlan.collectionModes]);
  const relationDefaultsKey = useMemo(
    () => JSON.stringify({
      tags: scrapedData?.tags ?? [],
      performers: scrapedData?.performers ?? [],
      currentTags: currentData.tags,
      currentPerformers: currentData.performers,
      existingTags: existingTagNames,
      existingPerformers: existingPerformerNames,
      createMissingTags: preferences.createMissingTags,
      createMissingPerformers: preferences.createMissingPerformers,
    }),
    [currentData.performers, currentData.tags, existingPerformerNames, existingTagNames, preferences.createMissingPerformers, preferences.createMissingTags, scrapedData?.performers, scrapedData?.tags],
  );

  useEffect(() => {
    if (!open) {
      return;
    }

    setPreferences(loadScrapeApplyPreferences());
  }, [open]);

  useEffect(() => {
    saveScrapeApplyPreferences(preferences);
  }, [preferences]);

  useEffect(() => {
    if (!open) {
      return;
    }

    setSelectedSourceUrl(initialSourceUrl);
    setUrl(initialSourceUrl);
    setName(getNameSearchInput(entity));
    setFragmentJson(buildFragmentDraftForUrl(entityType, entity, initialSourceUrl));
    setSelectedAttempt(null);
    setSelectedCandidateIndex(0);
    setReplaceFields([]);
    setCollectionModes({ ...DEFAULT_COLLECTION_MODES });
    setTagActions({});
    setPerformerActions({});
    setError(null);
  }, [entity, entityType, initialSourceUrl, open]);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (!selectedAttempt && recentAttempts.length > 0) {
      setSelectedAttempt(recentAttempts[0] ?? null);
    }
  }, [open, recentAttempts, selectedAttempt]);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (!selectedScraperId || !entityScrapers.some((scraper) => scraper.id === selectedScraperId)) {
      setSelectedScraperId(findPreferredScraperId(entityScrapers, activeSourceUrl, scraperPreferences));
    }
  }, [activeSourceUrl, entityScrapers, open, scraperPreferences, selectedScraperId]);

  useEffect(() => {
    if (!selectedAttempt) {
      setSelectedCandidateIndex(0);
      return;
    }

    const resultPayload = parseJsonObject(selectedAttempt.resultJson);
    if (!resultPayload || candidateResults.length <= 1) {
      setSelectedCandidateIndex(0);
      return;
    }

    const resultSignature = JSON.stringify(resultPayload);
    const matchingIndex = candidateResults.findIndex((candidate) => JSON.stringify(candidate.raw) === resultSignature);
    setSelectedCandidateIndex(matchingIndex >= 0 ? matchingIndex : 0);
  }, [candidateResults, selectedAttempt]);

  useEffect(() => {
    if (!selectedAttempt) {
      setError(null);
      return;
    }

    setError(selectedAttempt.status.toLowerCase() === "failure" ? selectedAttempt.error || "Scrape returned no results." : null);
  }, [selectedAttempt]);

  useEffect(() => {
    if (!selectedScraper) {
      return;
    }

    setInputKind((current) => findDefaultKind(selectedScraper, current));
  }, [selectedScraper]);

  useEffect(() => {
    if (!scrapedData) {
      setReplaceFields([]);
      setCollectionModes({ ...DEFAULT_COLLECTION_MODES });
      return;
    }

    setReplaceFields([...applyPlan.replaceFields]);
    setCollectionModes({ ...applyPlan.collectionModes });
  }, [entity.id, selectedAttempt?.id, scrapedData, suggestedCollectionModesKey, suggestedReplaceKey]);

  useEffect(() => {
    if (!scrapedData) {
      setTagActions({});
      setPerformerActions({});
      return;
    }

    setTagActions(buildRelationActionMap(scrapedData.tags, currentData.tags, existingTagNames, preferences.createMissingTags));
    setPerformerActions(buildRelationActionMap(scrapedData.performers, currentData.performers, existingPerformerNames, preferences.createMissingPerformers));
  }, [entity.id, relationDefaultsKey, scrapedData, selectedAttempt?.id]);

  const runMutation = useMutation({
    mutationFn: async () => {
      if (!selectedScraper) {
        throw new Error("Select a scraper first.");
      }

      if (inputKind === "url" && !url.trim()) {
        throw new Error("Enter a URL to scrape.");
      }

      if (inputKind === "name" && !name.trim()) {
        throw new Error("Enter a name to scrape.");
      }

      let fragment: Record<string, unknown> | undefined;
      if (inputKind === "fragment") {
        const parsed = JSON.parse(fragmentJson);
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
          throw new Error("Fragment input must be a JSON object.");
        }
        fragment = parsed as Record<string, unknown>;
      }

      return scrapeAttempts.create({
        scraperId: selectedScraper.id,
        entityType,
        entityId: entity.id,
        inputKind,
        url: inputKind === "url" ? url.trim() : undefined,
        name: inputKind === "name" ? name.trim() : undefined,
        fragment,
      });
    },
    onSuccess: (attempt) => {
      setSelectedAttempt(attempt);
      setSelectedCandidateIndex(0);
      queryClient.invalidateQueries({ queryKey: ["scrape-attempts", entityType, entity.id] });
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to run scrape.");
    },
  });

  const applyMutation = useMutation({
    mutationFn: async () => {
      if (!selectedAttempt) {
        throw new Error("Run a scrape first.");
      }

      return scrapeAttempts.apply(selectedAttempt.id, {
        replaceFields,
        collectionModes,
        createMissingTags: preferences.createMissingTags,
        createMissingPerformers: preferences.createMissingPerformers,
        createMissingStudio: preferences.createMissingStudio,
        markOrganized: preferences.markOrganized,
        selectedCandidateIndex: candidateResults.length > 1 ? selectedCandidateIndex : undefined,
        tagSelections: scrapedData?.tags.length ? buildRelationSelectionPayload(scrapedData.tags, tagActions) : undefined,
        performerSelections: scrapedData?.performers.length ? buildRelationSelectionPayload(scrapedData.performers, performerActions) : undefined,
      });
    },
    onSuccess: async (attempt) => {
      setSelectedAttempt(attempt);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: [entityType, entity.id] }),
        queryClient.invalidateQueries({ queryKey: [ENTITY_LIST_KEYS[entityType]] }),
        queryClient.invalidateQueries({ queryKey: ["scrape-attempts", entityType, entity.id] }),
      ]);
      onClose();
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to apply scraped fields.");
    },
  });

  if (!open) {
    return null;
  }

  const canRun = Boolean(selectedScraper) && supportsScrapeKind(selectedScraper, inputKind);
  const canApply = Boolean(selectedAttempt && scrapedData && selectedAttempt.status.toLowerCase() !== "failure");
  const entityLabel = getEntityLabel(entityType, entity);
  const collectionChangeCount = Object.values(collectionModes).filter((mode) => mode !== "skip").length;
  const rawPayload = selectedCandidate?.raw ? JSON.stringify(selectedCandidate.raw, null, 2) : selectedAttempt?.resultJson || "No result JSON";
  const scalarRows = [
    { key: "title", label: "Title", current: currentData.title, scraped: scrapedData?.title },
    { key: "code", label: "Code", current: currentData.code, scraped: scrapedData?.code },
    { key: "details", label: "Details", current: currentData.details, scraped: scrapedData?.details, multiline: true },
    ...(entityType === "image" ? [{ key: "photographer", label: "Photographer", current: currentData.creator, scraped: scrapedData?.creator }] : []),
    { key: "date", label: "Date", current: currentData.date, scraped: scrapedData?.date },
  ].filter((row) => Boolean(row.scraped));
  const collectionRows = [
    { key: "urls", label: "URLs", current: currentData.urls, scraped: scrapedData?.urls ?? [] },
    { key: "tags", label: "Tags", current: currentData.tags, scraped: scrapedData?.tags ?? [] },
    { key: "performers", label: "Performers", current: currentData.performers, scraped: scrapedData?.performers ?? [] },
  ].filter((row) => row.scraped.length > 0);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="flex max-h-[92vh] w-full max-w-7xl flex-col overflow-hidden rounded-[28px] border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <ExternalLink className="h-5 w-5 text-accent" />
              Scrape Review
            </h2>
            <p className="mt-0.5 text-xs text-secondary">
              Run a scraper for {entityLabel} and review the incoming metadata before it touches the {entityType}.
            </p>
          </div>
          <button onClick={onClose} className="text-muted hover:text-foreground" aria-label="Close scrape review dialog">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid min-h-0 flex-1 gap-0 lg:grid-cols-[340px_minmax(0,1fr)]">
          <div className="overflow-y-auto border-b border-border bg-card/40 p-4 lg:border-b-0 lg:border-r">
            <div className="space-y-4">
              <div className="rounded-2xl border border-border bg-card p-4">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="text-xs uppercase tracking-[0.18em] text-muted">{ENTITY_LABELS[entityType]}</div>
                    <div className="mt-1 text-sm font-semibold text-foreground">{entityLabel}</div>
                    <div className="mt-2 break-all text-xs text-secondary">{activeSourceUrl ? activeSourceUrl : "No source URL stored yet."}</div>
                    {sourceUrls.length > 1 ? (
                      <div className="mt-3 space-y-1.5">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">Source URL</div>
                        <select
                          value={activeSourceUrl}
                          onChange={(event) => {
                            const nextSourceUrl = event.target.value;
                            setSelectedSourceUrl(nextSourceUrl);
                            setSelectedScraperId(findPreferredScraperId(availableEntityScrapers, nextSourceUrl, scraperPreferences));
                            setUrl(nextSourceUrl);
                            setFragmentJson(buildFragmentDraftForUrl(entityType, entity, nextSourceUrl));
                            setSelectedAttempt(null);
                            setSelectedCandidateIndex(0);
                            setError(null);
                          }}
                          className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-xs text-foreground outline-none"
                        >
                          {sourceUrls.map((sourceUrl) => (
                            <option key={sourceUrl} value={sourceUrl}>
                              {sourceUrl}
                            </option>
                          ))}
                        </select>
                        <div className="text-[11px] text-muted">Choose which stored URL should drive scraper selection and the default scrape input.</div>
                      </div>
                    ) : null}
                  </div>
                </div>
              </div>

              <div className="space-y-2">
                <label className="block text-sm font-medium text-foreground">Scraper</label>
                <select
                  value={selectedScraperId}
                  onChange={(event) => setSelectedScraperId(event.target.value)}
                  className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground outline-none"
                >
                  {entityScrapers.length === 0 ? <option value="">No {entityType} scrapers found</option> : null}
                  {entityScrapers.map((scraper) => (
                    <option key={scraper.id} value={scraper.id}>
                      {scraper.name}
                    </option>
                  ))}
                </select>
                {selectedScraper ? (
                  <div className="rounded-xl border border-border bg-card/70 px-3 py-2 text-xs text-muted">
                    <div>Supports: {selectedScraper.supportedScrapes.join(", ")}</div>
                    <div className="mt-1 break-all">Source: {selectedScraper.sourcePath}</div>
                  </div>
                ) : null}
              </div>

              <div className="space-y-2 rounded-2xl border border-border bg-card p-4">
                <label className="block text-sm font-medium text-foreground">Input</label>
                <div className="grid grid-cols-3 gap-2">
                  {(["url", "name", "fragment"] as InputKind[]).map((value) => {
                    const supported = supportsScrapeKind(selectedScraper, value);
                    return (
                      <button
                        key={value}
                        onClick={() => setInputKind(value)}
                        disabled={!supported}
                        className={`rounded-xl border px-3 py-2 text-sm capitalize transition-colors ${
                          inputKind === value
                            ? "border-accent bg-accent/10 text-accent"
                            : "border-border bg-surface text-secondary hover:text-foreground"
                        } disabled:cursor-not-allowed disabled:opacity-40`}
                      >
                        {value}
                      </button>
                    );
                  })}
                </div>

                {inputKind === "url" ? (
                  <input
                    value={url}
                    onChange={(event) => setUrl(event.target.value)}
                    placeholder={URL_PLACEHOLDERS[entityType]}
                    className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground outline-none"
                  />
                ) : null}

                {inputKind === "name" ? (
                  <div className="space-y-2">
                    <input
                      value={name}
                      onChange={(event) => setName(event.target.value)}
                      placeholder="Search text"
                      className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground outline-none"
                    />
                    <div className="text-xs text-secondary">
                      Edit the search text before running if the stored title contains extra site prefixes or punctuation.
                    </div>
                  </div>
                ) : null}

                {inputKind === "fragment" ? (
                  <textarea
                    value={fragmentJson}
                    onChange={(event) => setFragmentJson(event.target.value)}
                    rows={10}
                    className="w-full rounded-xl border border-border bg-surface px-3 py-2 font-mono text-xs text-foreground outline-none"
                  />
                ) : null}
              </div>

              <button
                onClick={() => {
                  setError(null);
                  runMutation.mutate();
                }}
                disabled={!canRun || runMutation.isPending || applyMutation.isPending}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
              >
                {runMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                Scrape And Review
              </button>

              <div className="space-y-2 border-t border-border pt-4">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-medium text-foreground">Recent Attempts</h3>
                  <span className="text-xs text-muted">{recentAttempts.length}</span>
                </div>
                <div className="space-y-2">
                  {recentAttempts.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-border bg-card/50 px-3 py-4 text-sm text-muted">
                      No scrape attempts for this {entityType} yet.
                    </div>
                  ) : (
                    recentAttempts.map((attempt) => {
                      const attemptCandidates = getAttemptCandidates(entityType, attempt);
                      return (
                        <button
                          key={attempt.id}
                          onClick={() => setSelectedAttempt(attempt)}
                          className={`w-full rounded-xl border px-3 py-3 text-left transition-colors ${
                            selectedAttempt?.id === attempt.id
                              ? "border-accent bg-accent/10"
                              : "border-border bg-card hover:border-accent/40"
                          }`}
                        >
                          <div className="flex items-center justify-between gap-3">
                            <div className="min-w-0">
                              <div className="truncate text-sm font-medium text-foreground">{attempt.scraperId}</div>
                              <div className="text-xs text-muted">
                                {formatAttemptTime(attempt.createdAt)}
                                {attemptCandidates.length > 1 ? ` • ${attemptCandidates.length} matches` : ""}
                              </div>
                            </div>
                            <span className={`rounded-full border px-2 py-0.5 text-[11px] ${statusTone(attempt.status)}`}>
                              {attempt.status}
                            </span>
                          </div>
                        </button>
                      );
                    })
                  )}
                </div>
              </div>
            </div>
          </div>

          <div className="min-h-0 overflow-y-auto bg-[radial-gradient(circle_at_top,_rgba(34,197,94,0.08),_transparent_32%),linear-gradient(180deg,_rgba(255,255,255,0.02),_transparent_40%)] p-5">
            {error ? (
              <div className="mb-4 rounded-xl border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">
                {error}
              </div>
            ) : null}

            {!selectedAttempt ? (
              <div className="flex h-full items-center justify-center rounded-2xl border border-dashed border-border bg-card/40 px-6 py-16 text-center text-sm text-muted">
                Run a scrape or select a previous attempt to review the scraped fields.
              </div>
            ) : selectedAttempt.status.toLowerCase() === "failure" || !scrapedData ? (
              <div className="space-y-4">
                <div className="flex items-center gap-3">
                  <span className={`rounded-full border px-2.5 py-1 text-xs ${statusTone(selectedAttempt.status)}`}>
                    {selectedAttempt.status}
                  </span>
                  <span className="text-xs text-muted">{formatAttemptTime(selectedAttempt.createdAt)}</span>
                </div>
                <div className="rounded-2xl border border-border bg-card p-4 text-sm text-secondary">
                  {selectedAttempt.error || "This scrape attempt did not return any reviewable fields."}
                </div>
              </div>
            ) : (
              <div className="space-y-5">
                <div className="flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-border bg-card/70 px-4 py-3">
                  <div className="flex flex-wrap items-center gap-3">
                    <span className={`rounded-full border px-2.5 py-1 text-xs ${statusTone(selectedAttempt.status)}`}>
                      {selectedAttempt.status}
                    </span>
                    <span className="text-xs text-muted">{formatAttemptTime(selectedAttempt.createdAt)}</span>
                  </div>
                  <div className="flex items-center gap-2 text-xs text-muted">
                    <Sparkles className="h-4 w-4 text-accent" />
                    {replaceFields.length} replace field{replaceFields.length === 1 ? "" : "s"} selected, {collectionChangeCount} collection action{collectionChangeCount === 1 ? "" : "s"}
                  </div>
                </div>

                {candidateResults.length > 1 ? (
                  <section className="rounded-2xl border border-border bg-card/75 p-4">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="text-sm font-semibold text-foreground">Search Matches</div>
                        <div className="text-xs text-secondary">Choose the candidate that best matches the {entityType} before applying any fields.</div>
                      </div>
                      <div className="rounded-full border border-accent/30 bg-accent/10 px-3 py-1 text-xs font-medium text-accent">
                        {candidateResults.length} options
                      </div>
                    </div>
                    <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                      {candidateResults.map((candidate, index) => {
                        const title = candidate.title || candidate.code || candidate.urls[0] || `Result ${index + 1}`;
                        const candidatePerformers = candidate.performers.join(", ");
                        const subtitle = [entityType === "image" ? candidate.creator : candidatePerformers, candidate.studio, candidate.date].filter(Boolean).join(" • ");
                        const missingSubtitle = entityType === "image" ? "No photographer, studio, or date available" : "No performers, studio, or date available";
                        return (
                          <button
                            key={`${title}-${index}`}
                            onClick={() => setSelectedCandidateIndex(index)}
                            className={`rounded-2xl border px-4 py-3 text-left transition-colors ${
                              selectedCandidateIndex === index
                                ? "border-accent bg-accent/10"
                                : "border-border bg-surface hover:border-accent/40"
                            }`}
                          >
                            <div className="text-sm font-semibold text-foreground">{title}</div>
                            <div className="mt-1 min-h-[1.25rem] text-xs text-secondary">{subtitle || missingSubtitle}</div>
                            <div className="mt-2 line-clamp-2 break-all text-xs text-muted">{candidate.urls[0] || candidate.details || "No URL available"}</div>
                            <div className="mt-3 text-[11px] uppercase tracking-[0.18em] text-muted">
                              {candidate.performers.length} performer{candidate.performers.length === 1 ? "" : "s"} • {candidate.tags.length} tag{candidate.tags.length === 1 ? "" : "s"}
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </section>
                ) : null}

                {scalarRows.map((row) => (
                  <section key={row.key} className="rounded-2xl border border-border bg-card/75 p-4">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="text-sm font-semibold text-foreground">{row.label}</div>
                        <div className="text-xs text-secondary">Apply the scraped value only if it is the one you want to keep.</div>
                      </div>
                      <button
                        onClick={() => setReplaceFields((current) => upsertReplaceField(current, row.key, !replaceFields.includes(row.key)))}
                        className={`rounded-full border px-3 py-1 text-xs font-medium ${
                          replaceFields.includes(row.key)
                            ? "border-accent/40 bg-accent/10 text-accent"
                            : "border-border bg-surface text-secondary"
                        }`}
                      >
                        {replaceFields.includes(row.key) ? "Replace" : "Skip"}
                      </button>
                    </div>
                    <div className="mt-4 grid gap-3 lg:grid-cols-2">
                      <div className="rounded-2xl border border-border bg-surface p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">Current</div>
                        <div className={`mt-2 text-sm text-secondary ${row.multiline ? "whitespace-pre-wrap" : ""}`}>
                          {row.current || <span className="text-muted">Empty</span>}
                        </div>
                      </div>
                      <div className="rounded-2xl border border-accent/30 bg-accent/5 p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-accent">Scraped</div>
                        <div className={`mt-2 text-sm text-foreground ${row.multiline ? "whitespace-pre-wrap" : ""}`}>{row.scraped}</div>
                      </div>
                    </div>
                  </section>
                ))}

                {scrapedData.studio ? (
                  <section className="rounded-2xl border border-border bg-card/75 p-4">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="text-sm font-semibold text-foreground">Studio</div>
                        <div className="text-xs text-secondary">Choose whether the scraped studio should replace the current one.</div>
                      </div>
                      <select
                        value={collectionModes.studio}
                        onChange={(event) => setCollectionModes((current) => ({ ...current, studio: event.target.value as CollectionMode }))}
                        className="rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground outline-none"
                      >
                        <option value="skip">Skip</option>
                        <option value="replace">Replace</option>
                      </select>
                    </div>
                    <div className="mt-4 grid gap-3 lg:grid-cols-2">
                      <div className="rounded-2xl border border-border bg-surface p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">Current</div>
                        <div className="mt-2 text-sm text-secondary">{currentData.studio || <span className="text-muted">Empty</span>}</div>
                      </div>
                      <div className="rounded-2xl border border-accent/30 bg-accent/5 p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-accent">Scraped</div>
                        <div className="mt-2 text-sm text-foreground">{scrapedData.studio}</div>
                      </div>
                    </div>
                  </section>
                ) : null}

                {collectionRows.map((row) => (
                  <section key={row.key} className="rounded-2xl border border-border bg-card/75 p-4">
                    {(() => {
                      const isTags = row.key === "tags";
                      const isPerformers = row.key === "performers";
                      const relationActions = isTags ? tagActions : performerActions;
                      const setRelationActions = isTags ? setTagActions : setPerformerActions;
                      const existingNames = isTags ? existingTagNames : existingPerformerNames;
                      const matchInfo = isTags ? tagMatchInfo : performerMatchInfo;
                      const showRelationChoices = isTags || isPerformers;

                      return (
                        <>
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="text-sm font-semibold text-foreground">{row.label}</div>
                        {!showRelationChoices ? <div className="text-xs text-secondary">Merge the incoming list, replace it entirely, or leave the current values untouched.</div> : null}
                      </div>
                      <select
                        value={collectionModes[row.key]}
                        onChange={(event) => setCollectionModes((current) => ({ ...current, [row.key]: event.target.value as CollectionMode }))}
                        className="rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground outline-none"
                      >
                        <option value="skip">Skip</option>
                        <option value="merge">Merge</option>
                        <option value="replace">Replace</option>
                      </select>
                    </div>
                    <div className="mt-4 grid gap-3 lg:grid-cols-2">
                      <div className="rounded-2xl border border-border bg-surface p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">Current</div>
                        <div className="mt-2 text-sm text-secondary">
                          {row.current.length > 0 ? row.current.join(", ") : <span className="text-muted">Empty</span>}
                        </div>
                      </div>
                      <div className="rounded-2xl border border-accent/30 bg-accent/5 p-3">
                        <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-accent">Scraped</div>
                        {showRelationChoices ? (
                          <ScrapeRelationChoices
                            names={row.scraped}
                            currentNames={row.current}
                            existingNames={existingNames}
                            matchInfo={matchInfo}
                            actions={relationActions}
                            disabled={collectionModes[row.key] === "skip"}
                            onActionChange={(name, action) => setRelationActions((current) => ({ ...current, [relationKey(name)]: action }))}
                          />
                        ) : (
                          <div className="mt-2 text-sm text-foreground">{row.scraped.join(", ")}</div>
                        )}
                      </div>
                    </div>
                        </>
                      );
                    })()}
                  </section>
                ))}

                <div className="grid gap-4 rounded-2xl border border-border bg-card p-4 lg:grid-cols-[minmax(0,1fr)_220px]">
                  <div>
                    <div className="text-sm font-semibold text-foreground">Apply Defaults</div>
                    <div className="mt-1 text-xs text-secondary">These options persist for future scrape review runs.</div>
                    <div className="mt-4 grid gap-3 md:grid-cols-2">
                      <label className="flex items-center gap-2 text-sm text-secondary">
                        <input
                          type="checkbox"
                          checked={preferences.createMissingStudio}
                          onChange={(event) => setPreferences((current) => ({ ...current, createMissingStudio: event.target.checked }))}
                          className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Create missing studio
                      </label>
                      <label className="flex items-center gap-2 text-sm text-secondary">
                        <input
                          type="checkbox"
                          checked={preferences.createMissingTags}
                          onChange={(event) => setPreferences((current) => ({ ...current, createMissingTags: event.target.checked }))}
                          className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Default new tags to create
                      </label>
                      <label className="flex items-center gap-2 text-sm text-secondary">
                        <input
                          type="checkbox"
                          checked={preferences.createMissingPerformers}
                          onChange={(event) => setPreferences((current) => ({ ...current, createMissingPerformers: event.target.checked }))}
                          className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Default new performers to create
                      </label>
                      <label className="flex items-center gap-2 text-sm text-secondary">
                        <input
                          type="checkbox"
                          checked={preferences.markOrganized}
                          onChange={(event) => setPreferences((current) => ({ ...current, markOrganized: event.target.checked }))}
                          className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Mark {entityType} organized after apply
                      </label>
                    </div>
                  </div>
                  <div className="rounded-2xl border border-border bg-surface p-4">
                    <div className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">Ready To Apply</div>
                    <div className="mt-2 text-2xl font-semibold text-foreground">{replaceFields.length + collectionChangeCount}</div>
                    <div className="mt-1 text-xs text-secondary">
                      {replaceFields.length} direct field change{replaceFields.length === 1 ? "" : "s"} and {collectionChangeCount} collection rule{collectionChangeCount === 1 ? "" : "s"}.
                    </div>
                  </div>
                </div>

                <details className="rounded-2xl border border-border bg-card p-4">
                  <summary className="cursor-pointer text-sm font-medium text-foreground">Raw scrape payload</summary>
                  <pre className="mt-3 overflow-x-auto rounded-xl bg-black/30 p-3 text-xs text-secondary">{rawPayload}</pre>
                </details>

                <div className="flex items-center justify-end gap-2 border-t border-border pt-2">
                  <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">
                    Close
                  </button>
                  <button
                    onClick={() => applyMutation.mutate()}
                    disabled={!canApply || applyMutation.isPending || runMutation.isPending}
                    className="inline-flex items-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
                  >
                    {applyMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <ExternalLink className="h-4 w-4" />}
                    Apply Selected Fields
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
