import type {
  MeResponse,
  GlobalSearchResponse,
  Video, VideoCreate, VideoUpdate, VideoListEntry,
  Performer, PerformerCreate, PerformerUpdate,
  Tag, TagDetail, TagCreate, TagUpdate, TagSegmentWall,
  TagApplication, TagApplicationCreate, TagGroup, TagGroupCreate, TagGroupUpdate,
  TagGraphNode, TagGraphResponse,
  Studio, StudioCreate, StudioUpdate,
  Gallery, GalleryCreate, GalleryUpdate, GalleryChapter, GalleryChapterCreate, GalleryChapterUpdate,
  Image, ImageCreate, ImageUpdate,
  Audio, AudioCreate, AudioUpdate,
  TextDocument, TextCreate, TextUpdate, TextContent,
  Group, GroupCreate, GroupUpdate,
  GroupReorder,
  GroupItem, GroupItemCreate, GroupItemsFromSpans, GroupItemsRemoveHosts, GroupItemsReorder, GroupItemUpdate,
  GroupPlaybackManifest,
  BookmarkDto, BookmarkToggle, BookmarkState, BookmarkBatchRequest,
  DynamicGroupSource,
  AiDataPurgeRequest,
  AiDataPurgeResult,
  AiDataSelector,
  AiDataSummary,
  AudioSimilarVideo,
  VisualSimilarImage,
  VisualSimilarVideo,
  AffinityHostType,
  Segment, SegmentCreate, SegmentRecord, SegmentUpdate,
  ResolvedSpanDetail, ResolvedSpanList, VideoResolvedSpans, SegmentDisplayProfile,
  SegmentDisplayProfileCreate, SegmentDisplayProfileUpdate,
  SegmentDisplayRule, SegmentDisplayRuleCreate, SegmentDisplayRuleUpdate,
  SegmentSpanQueryRequest, SegmentSpanSearchRequest, SegmentSpanSearchResponse, SegmentSpanCountResponse,
  Detection, DetectionCreate, DetectionUpdate,
  Face, FaceAppearance, FaceAppearancesResponse, FaceCreate, FaceUpdate, FaceLink, FaceBatchLinkTopSuggestionRequest, FaceBatchDeleteRequest, FaceBatchOperationResult, FaceCreatePerformer, FaceCapabilities, FaceHostFace, FaceHostTrack, FaceMerge, FaceIgnore, FaceDeleteImpact, FaceNotPresentResult, FaceSimilar, FaceSplitResult, FaceSuggestion,
  EntityEngagement, EntityFavorite, EntityEngagementBatchRequest, EntityRatings,
  EngagementInteraction, EngagementInteractionWrite,
  VideoHistory,
  PaginatedResponse, Stats, SystemStatus, CoveConfig, FfmpegCapabilities, JobInfo,
  DatabaseMigrationResult,
  ScraperSummary,
  DownloaderDescriptor,
  DownloaderBatchStartRequest,
  DownloaderBatchStartResponse,
  DownloaderMatch,
  DownloaderMatchRequest,
  DownloaderStartRequest,
  MetadataServer,
  MetadataServerFindByIdsRequest,
  MetadataServerPerformerBatchTagRequest,
  MetadataServerPerformerImportRequest,
  MetadataServerPerformerMatch,
  MetadataServerVideoImportRequest,
  MetadataServerVideoMatch,
  MetadataServerTagBatchTagRequest,
  MetadataServerTagImportRequest,
  MetadataServerTagMatch,
  MetadataServerStudioBatchTagRequest,
  MetadataServerStudioMatch,
  MetadataServerStudioImportRequest,
  MetadataServerValidationResult,
  FindFilter,
  FileBackedCreate,
  DeleteEntityOptions,
  BulkDeletionJobStart,
  DuplicateSearchRequest,
  DuplicateSearchStart,
  DuplicateSearchInfo,
  DuplicateSearchGroupPage,
  CustomFieldDefinition, CustomFieldDefinitionCreate, CustomFieldDefinitionUpdate, CustomFieldCriterion,
  SavedFilter,
  SavedFilterCreate,
  SavedFilterUpdate,
  PerformerScrapeRequest,
  FilteredQueryRequest,
  VideoFilteredQueryRequest,
  PerformerFilteredQueryRequest,
  VideoFilterCriteria,
  VideoAggregate,
  ImageAggregate,
  AudioAggregate,
  TextAggregate,
  GalleryAggregate,
  PerformerFilterCriteria,
  TagFilterCriteria,
  StudioFilterCriteria,
  GalleryFilterCriteria,
  ImageFilterCriteria,
  AudioFilterCriteria,
  TextFilterCriteria,
  GroupFilterCriteria,
  ScrapeAttempt,
  CreateScrapeAttemptRequest,
  ApplyVideoScrapeAttemptRequest,
  ResolveScrapeRelationsRequest,
  ResolveScrapeRelationsResult,
  BulkVideoUpdate,
  BulkPerformerUpdate,
  BulkTagUpdate,
  BulkStudioUpdate,
  BulkGalleryUpdate,
  BulkImageUpdate,
  BulkAudioUpdate,
  BulkTextUpdate,
  BulkGroupUpdate,
  Plugin,
  PluginTask,
  RunPluginTaskRequest,
  PluginSettings,
  ExtensionManifest,
  ExtensionInfo,
  DependencyProblem,
  RegistrySearchResult,
  RegistryExtensionDetail,
  RegistryUpdateInfo,
  RegistryInstallResult,
  RegistryUninstallResult,
  DependencyInfo,
  DownloaderPreflightRequest,
  DownloaderPreflightResponse,
  UserUiPreferences,
  PlaybackIntervalsRequest,
  Dashboard,
  DashboardSummary,
  DashboardWidget,
} from "./types";

const API_BASE = "/api";
const LONG_API_REQUEST_TIMEOUT_MS = 2 * 60_000;
const UPLOAD_REQUEST_TIMEOUT_MS = 5 * 60_000;

// ===== Auth-aware fetch =====
// Lazy import to avoid circular deps; the auth module has no client.ts deps.
import { authStore } from "../auth/authStore";
import { serverAwareFetch, type ServerAwareFetchOptions } from "../state/serverAvailability";
import { serializeSortClauses } from "../utils/sortClauses";

let refreshInFlight: Promise<boolean> | null = null;
const REFRESH_LOCK_NAME = "cove-auth-refresh";
const REFRESH_SYNC_WAIT_MS = 5_000;

function refreshTokenChanged(staleRefreshToken: string): boolean {
  const current = authStore.getRefreshToken();
  return current !== null && current !== staleRefreshToken;
}

async function waitForRefreshTokenChange(staleRefreshToken: string): Promise<boolean> {
  if (refreshTokenChanged(staleRefreshToken)) return true;
  if (typeof window === "undefined") return false;

  return new Promise(resolve => {
    let settled = false;
    const finish = (changed: boolean) => {
      if (settled) return;
      settled = true;
      window.removeEventListener("storage", onStorage);
      window.clearInterval(intervalId);
      window.clearTimeout(timeoutId);
      resolve(changed);
    };
    const check = () => {
      if (refreshTokenChanged(staleRefreshToken)) finish(true);
    };
    const onStorage = () => check();
    const intervalId = window.setInterval(check, 25);
    const timeoutId = window.setTimeout(
      () => finish(refreshTokenChanged(staleRefreshToken)),
      REFRESH_SYNC_WAIT_MS,
    );
    window.addEventListener("storage", onStorage);
    check();
  });
}

async function refreshIfCurrent(rejectedAccessToken: string): Promise<boolean> {
  const currentAccessToken = authStore.getAccessToken();
  if (currentAccessToken && currentAccessToken !== rejectedAccessToken) {
    return true;
  }

  const refresh = authStore.getRefreshToken();
  if (!refresh) return false;
  try {
    const res = await serverAwareFetch(`${API_BASE}/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: refresh }),
    });
    if (res.status === 409) {
      const body = await res.json().catch(() => null) as { code?: string } | null;
      if (body?.code === "REFRESH_TOKEN_ROTATED") {
        if (await waitForRefreshTokenChange(refresh)) return true;
        authStore.clear();
        return false;
      }
    }
    if (!res.ok) {
      authStore.clear();
      return false;
    }
    const body = await res.json() as { token?: string; refreshToken?: string };
    if (!body.token) return false;
    authStore.setTokens(body.token, body.refreshToken ?? refresh);
    return true;
  } catch {
    return false;
  }
}

async function coordinatedRefresh(rejectedAccessToken: string): Promise<boolean> {
  if (typeof navigator !== "undefined" && navigator.locks) {
    // Web Locks serialize refresh-token rotation across same-origin pages. The
    // callback re-checks storage because another page may have refreshed first.
    try {
      return await navigator.locks.request(
        REFRESH_LOCK_NAME,
        () => refreshIfCurrent(rejectedAccessToken),
      );
    } catch {
      // Treat a browser lock failure like an unsupported lock implementation.
    }
  }
  return refreshIfCurrent(rejectedAccessToken);
}

async function tryRefresh(rejectedAccessToken: string): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = coordinatedRefresh(rejectedAccessToken);
  try { return await refreshInFlight; }
  finally { refreshInFlight = null; }
}

export async function authedFetch(input: string, init?: ServerAwareFetchOptions): Promise<Response> {
  const token = authStore.getAccessToken();
  const shareToken = authStore.getShareToken();
  const sharePassword = authStore.getSharePassword();
  const headers = new Headers(init?.headers ?? {});
  const authMode = shareToken ? "share" : token ? "bearer" : "none";
  if (shareToken) {
    headers.set("X-Share-Token", shareToken);
    if (sharePassword) {
      headers.set("X-Share-Password", sharePassword);
    }
  } else if (token && !headers.has("Authorization")) {
    headers.set("Authorization", `Bearer ${token}`);
  }
  let res = await serverAwareFetch(input, { ...init, headers });
  if (res.status === 401 && authMode === "bearer" && token && authStore.getRefreshToken()) {
    const ok = await tryRefresh(token);
    if (ok) {
      const retryToken = authStore.getAccessToken();
      const retryHeaders = new Headers(init?.headers ?? {});
      if (retryToken) retryHeaders.set("Authorization", `Bearer ${retryToken}`);
      res = await serverAwareFetch(input, { ...init, headers: retryHeaders });
    } else {
      // refresh failed: emit a global event so UI can react
      window.dispatchEvent(new CustomEvent("cove-auth-required"));
    }
  } else if (res.status === 401 && authMode === "none") {
    window.dispatchEvent(new CustomEvent("cove-auth-required"));
  }
  return res;
}

const CRITERION_MODIFIER_MAP: Record<string, string> = {
  EQUALS: "equals",
  NOT_EQUALS: "notEquals",
  GREATER_THAN: "greaterThan",
  LESS_THAN: "lessThan",
  INCLUDES: "includes",
  EXCLUDES: "excludes",
  INCLUDES_ALL: "includesAll",
  EXCLUDES_ALL: "excludesAll",
  IS_NULL: "isNull",
  NOT_NULL: "notNull",
  BETWEEN: "between",
  NOT_BETWEEN: "notBetween",
  MATCHES_REGEX: "matchesRegex",
  NOT_MATCHES_REGEX: "notMatchesRegex",
  UNDER_PATH: "underPath",
  NOT_UNDER_PATH: "notUnderPath",
};

async function request<T>(path: string, options?: ServerAwareFetchOptions): Promise<T> {
  const headers = new Headers(options?.headers);
  if (!(options?.body instanceof FormData) && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  const res = await authedFetch(`${API_BASE}${path}`, {
    ...options,
    headers,
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API Error ${res.status}: ${text}`);
  }
  if (res.status === 204 || res.status === 205) return undefined as T;
  return readResponseBody<T>(res);
}

function normalizeApiPath(path: string): string {
  const normalized = path.trim();
  if (normalized.startsWith(`${API_BASE}/`)) {
    return normalized.slice(API_BASE.length);
  }

  return normalized.startsWith("/") ? normalized : `/${normalized}`;
}

async function requestOptional<T>(path: string, options?: ServerAwareFetchOptions): Promise<T | null> {
  const res = await authedFetch(`${API_BASE}${path}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...options?.headers,
    },
  });
  if (res.status === 404) {
    return null;
  }
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API Error ${res.status}: ${text}`);
  }
  if (res.status === 204 || res.status === 205) return undefined as T;
  return readResponseBody<T>(res);
}

async function readResponseBody<T>(res: Response): Promise<T> {
  const text = await res.text();
  if (text.trim() === "") {
    return undefined as T;
  }

  return JSON.parse(text) as T;
}

export const globalSearch = {
  find: (q: string, perType = 8, signal?: AbortSignal) =>
    request<GlobalSearchResponse>(`/search/global${buildQuery(undefined, { q, perType })}`, { signal }),
};

function buildQuery(filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>): string {
  const params = new URLSearchParams();
  if (filter?.q) params.set("q", filter.q);
  if (filter?.page != null) params.set("page", String(filter.page));
  if (filter && filter.perPage != null) params.set("perPage", String(filter.perPage));
  if (filter?.sorts && filter.sorts.length > 1) {
    params.set("sorts", serializeSortClauses(filter.sorts));
  } else {
    if (filter?.sort) params.set("sort", filter.sort);
    if (filter?.direction) params.set("direction", filter.direction);
  }
  if (filter?.seed != null) params.set("seed", String(filter.seed));
  if (extra) {
    for (const [k, v] of Object.entries(extra)) {
      if (v !== undefined) params.set(k, String(v));
    }
  }
  const qs = params.toString();
  return qs ? `?${qs}` : "";
}

function buildAiDataQuery(selector?: AiDataSelector): string {
  if (!selector) {
    return "";
  }

  const params = new URLSearchParams();
  if (selector.sourceKey) params.set("sourceKey", selector.sourceKey);
  if (selector.sourceRunId) params.set("sourceRunId", selector.sourceRunId);
  if (selector.model) params.set("model", selector.model);
  if (selector.modality) params.set("modality", selector.modality);
  if (selector.hostType) params.set("hostType", selector.hostType);
  if (selector.hostId != null) params.set("hostId", String(selector.hostId));
  if (selector.kinds && selector.kinds.length > 0) params.set("kinds", selector.kinds.join(","));
  const query = params.toString();
  return query ? `?${query}` : "";
}

function normalizeCriterionPayload<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map((item) => normalizeCriterionPayload(item)) as T;
  }

  if (value && typeof value === "object") {
    const normalizedEntries = Object.entries(value as Record<string, unknown>).map(([key, entryValue]) => {
      if (key === "modifier" && typeof entryValue === "string") {
        return [key, CRITERION_MODIFIER_MAP[entryValue] ?? entryValue];
      }

      return [key, normalizeCriterionPayload(entryValue)];
    });

    return Object.fromEntries(normalizedEntries) as T;
  }

  return value;
}

function buildMediaUrl(
  path: string,
  version?: string,
  max?: number,
  extra?: Record<string, string | number | undefined>,
): string {
  const params = new URLSearchParams();
  if (typeof max === "number" && max > 0) params.set("max", String(max));
  if (version) params.set("v", version);
  if (extra) {
    for (const [key, value] of Object.entries(extra)) {
      if (value !== undefined && value !== "") {
        params.set(key, String(value));
      }
    }
  }

  const shareToken = authStore.getShareToken();
  const sharePassword = authStore.getSharePassword();
  // Bearer users authenticate media requests via the same-origin, httpOnly `cove_access_token`
  // cookie (set on login and refreshed on every /auth/refresh). We deliberately do NOT embed the
  // access token in media URLs:
  //   1. The token lives ~15 min. Because the auth middleware prefers a query `access_token` over the
  //      cookie, a baked, now-expired token 401s even though a valid cookie is present.
  //   2. Refreshing the token would change the URL, remounting the <video> and reloading/rewinding it
  //      mid-playback (the reported "video jumps back on token refresh" bug).
  // Omitting it keeps the media URL stable and lets the request fall through to the always-current
  // cookie, so playback (and images) survive token refreshes seamlessly. Share links have no cookie,
  // so they still carry their token in the URL.
  if (shareToken) {
    params.set("share_token", shareToken);
    if (sharePassword) {
      params.set("share_password", sharePassword);
    }
  }

  const query = params.toString();
  return `${API_BASE}${path}${query ? `?${query}` : ""}`;
}

// ===== Videos =====
export const videos = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Video>>(`/videos${buildQuery(filter, extra)}`),
  findWithCompilations: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<VideoListEntry>>(`/videos/with-compilations${buildQuery(filter, extra)}`),
  findFiltered: (req: VideoFilteredQueryRequest) =>
    request<PaginatedResponse<Video>>("/videos/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  aggregate: (req: VideoFilteredQueryRequest) =>
    request<VideoAggregate>("/videos/aggregate", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Video>(`/videos/${id}`),
  create: (data: VideoCreate) => request<Video>("/videos", { method: "POST", body: JSON.stringify(data) }),
  createFromFile: (data: FileBackedCreate) => request<Video>("/videos/from-file", { method: "POST", body: JSON.stringify(data) }),
  createSubVideo: (parentVideoId: number, data: VideoCreate) =>
    request<Video>("/videos", { method: "POST", body: JSON.stringify({ ...data, parentVideoId }) }),
  update: (id: number, data: VideoUpdate) => request<Video>(`/videos/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  bulkUpdate: (data: BulkVideoUpdate) => request<void>("/videos/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number, options?: boolean | DeleteEntityOptions) => {
    const deleteFile = typeof options === "boolean" ? options : options?.deleteFile;
    const deleteGenerated = typeof options === "boolean" ? undefined : options?.deleteGenerated;
    return request<void>(`/videos/${id}${buildQuery(undefined, { deleteFile, deleteGenerated })}`, { method: "DELETE" });
  },
  bulkDelete: (ids: number[], options?: boolean | DeleteEntityOptions) => {
    const deleteFiles = typeof options === "boolean" ? options : options?.deleteFile ?? false;
    const deleteGenerated = typeof options === "boolean" ? false : options?.deleteGenerated ?? false;
    return request<BulkDeletionJobStart>("/videos/destroy", { method: "POST", body: JSON.stringify({ ids, deleteFiles, deleteGenerated }) });
  },
  merge: (targetId: number, sourceIds: number[]) =>
    request<Video>("/videos/merge", { method: "POST", body: JSON.stringify({ targetId, sourceIds }) }),
  recordPlay: (id: number) => request<void>(`/videos/${id}/play`, { method: "POST" }),
  incrementLike: (id: number) => request<number>(`/videos/${id}/like`, { method: "POST" }),
  addHistoricalLike: (id: number, at: string) =>
    request<number>(`/videos/${id}/like/historical`, { method: "POST", body: JSON.stringify({ at }) }),
  deleteLikeFromHistory: (id: number, at: string) => request<void>(`/videos/${id}/like/history?at=${encodeURIComponent(at)}`, { method: "DELETE" }),
  decrementLike: (id: number) => request<void>(`/videos/${id}/like`, { method: "DELETE" }),
  resetLike: (id: number) => request<void>(`/videos/${id}/like/reset`, { method: "POST" }),
  deletePlay: (id: number) => request<void>(`/videos/${id}/play`, { method: "DELETE" }),
  resetPlay: (id: number) => request<void>(`/videos/${id}/play/reset`, { method: "POST" }),
  resetActivity: (id: number) => request<void>(`/videos/${id}/activity/reset`, { method: "POST" }),
  getHistory: (id: number) => request<VideoHistory>(`/videos/${id}/history`),
  searchMetadataServer: (id: number, term?: string, endpoint?: string, strategy?: string) =>
    request<MetadataServerVideoMatch[]>(`/videos/${id}/metadata-server/search${buildQuery(undefined, { term, endpoint, strategy })}`),
  findMetadataServerByIds: (data: MetadataServerFindByIdsRequest) =>
    request<MetadataServerVideoMatch[]>("/videos/metadata-server/find-by-ids", { method: "POST", body: JSON.stringify(data) }),
  importFromMetadataServer: (id: number, data: MetadataServerVideoImportRequest) =>
    request<Video>(`/videos/${id}/metadata-server/import`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  submitMetadataServerDraft: (id: number, endpoint: string) =>
    request<{ draftId: string | null }>(`/videos/${id}/metadata-server/submit-draft`, { method: "POST", body: JSON.stringify({ endpoint }) }),
  submitFingerprints: (id: number, endpoint: string) =>
    request<void>(`/videos/${id}/metadata-server/submit-fingerprints`, { method: "POST", body: JSON.stringify({ endpoint }) }),
  generateScreenshot: (id: number, atSeconds?: number) =>
    request<{ success: boolean }>(`/videos/${id}/generate-screenshot`, { method: "POST", body: JSON.stringify({ atSeconds }) }),
  setCoverFromFrame: (id: number, atSeconds?: number) =>
    request<{ success: boolean }>(`/videos/${id}/cover/from-frame`, { method: "POST", body: JSON.stringify({ atSeconds }) }),
  rescan: (id: number) =>
    request<{ jobId: string }>(`/videos/${id}/rescan`, { method: "POST" }),
  assignFile: (id: number, fileId: number) =>
    request<void>(`/videos/${id}/assign-file`, { method: "POST", body: JSON.stringify({ fileId }) }),
  streamUrl: (id: number) => buildMediaUrl(`/stream/video/${id}`),
  screenshotUrl: (id: number, version?: string, seconds?: number) => buildMediaUrl(`/stream/video/${id}/screenshot`, version, undefined, { seconds }),
  segmentPreviewUrl: (id: number, seconds: number, version?: string) => buildMediaUrl(`/stream/video/${id}/segment-preview`, version, undefined, { seconds }),
  previewUrl: (id: number) => buildMediaUrl(`/stream/video/${id}/preview`),
  previewStatusUrl: (id: number) => buildMediaUrl(`/stream/video/${id}/preview/status`),
  captionUrl: (videoId: number, captionId: number) => buildMediaUrl(`/stream/video/${videoId}/caption/${captionId}`),
  transcodeUrl: (id: number, resolution?: string, start?: number) => buildMediaUrl(`/stream/video/${id}/transcode`, undefined, undefined, { resolution, start }),
  hlsMasterUrl: (id: number) => buildMediaUrl(`/stream/video/${id}/hls/master.m3u8`),
  getResolutions: (id: number) => request<string[]>(`/stream/video/${id}/resolutions`),
  segments: {
    list: (videoId: number) => request<Segment[]>(`/videos/${videoId}/segments`),
    create: (videoId: number, data: SegmentCreate) =>
      request<Segment>(`/videos/${videoId}/segments`, { method: "POST", body: JSON.stringify(data) }),
    update: (videoId: number, id: number, data: SegmentUpdate) =>
      request<Segment>(`/videos/${videoId}/segments/${id}`, { method: "PUT", body: JSON.stringify(data) }),
    delete: (videoId: number, id: number) =>
      request<void>(`/videos/${videoId}/segments/${id}`, { method: "DELETE" }),
    spans: (videoId: number, profile?: number) =>
      request<VideoResolvedSpans>(`/videos/${videoId}/segments/spans${buildQuery(undefined, { profile })}`),
    querySpans: (videoId: number, data: SegmentSpanQueryRequest) =>
      request<ResolvedSpanList>(`/videos/${videoId}/segments/spans/query`, { method: "POST", body: JSON.stringify(data) }),
    spanDetail: (videoId: number, spanKey: string, profile?: number) =>
      request<ResolvedSpanDetail>(`/videos/${videoId}/spans/${encodeURIComponent(spanKey)}${buildQuery(undefined, { profile })}`),
  },

  detections: {
    list: (videoId: number) => request<Detection[]>(`/videos/${videoId}/detections`),
    create: (videoId: number, data: DetectionCreate) =>
      request<Detection>(`/videos/${videoId}/detections`, { method: "POST", body: JSON.stringify(data) }),
    update: (videoId: number, id: number, data: DetectionUpdate) =>
      request<Detection>(`/videos/${videoId}/detections/${id}`, { method: "PUT", body: JSON.stringify(data) }),
    delete: (videoId: number, id: number) =>
      request<void>(`/videos/${videoId}/detections/${id}`, { method: "DELETE" }),
  },
  startDuplicateSearch: (options: DuplicateSearchRequest) =>
    request<DuplicateSearchStart>("/videos/duplicate-searches", { method: "POST", body: JSON.stringify(options) }),
  getDuplicateSearch: (searchId: string) =>
    request<DuplicateSearchInfo>(`/videos/duplicate-searches/${encodeURIComponent(searchId)}`),
  getDuplicateSearchGroups: (searchId: string, page: number, perPage: number) =>
    request<DuplicateSearchGroupPage>(`/videos/duplicate-searches/${encodeURIComponent(searchId)}/groups${buildQuery(undefined, { page, perPage })}`),
  updateDuplicateSearchDecision: (searchId: string, groupId: number, keepVideoIds: number[]) =>
    request<void>(`/videos/duplicate-searches/${encodeURIComponent(searchId)}/groups/${groupId}`, {
      method: "PATCH",
      body: JSON.stringify({ keepVideoIds }),
    }),
  deleteUnkeptDuplicates: (searchId: string, options?: DeleteEntityOptions) =>
    request<BulkDeletionJobStart>(`/videos/duplicate-searches/${encodeURIComponent(searchId)}/delete-unkept`, {
      method: "POST",
      body: JSON.stringify({
        deleteFiles: options?.deleteFile ?? false,
        deleteGenerated: options?.deleteGenerated ?? false,
      }),
    }),
};

export const fileOps = {
  reveal: (fileId: number) => request<void>(`/files/${fileId}/reveal`, { method: "POST" }),
  revealFolder: (folderId: number) => request<void>(`/files/folders/${folderId}/reveal`, { method: "POST" }),
};

export const playback = {
  recordIntervals: (data: PlaybackIntervalsRequest) =>
    request<void>("/playback/intervals", { method: "POST", body: JSON.stringify(data) }),
};

export const segmentLibrary = {
  list: (opts?: {
    q?: string;
    videoId?: number;
    videoIds?: string;
    videoTitle?: string;
    videoTagIds?: string;
    videoTagDepth?: number;
    tagId?: number;
    tagIds?: string;
    tagDepth?: number;
    kind?: string;
    sourceKey?: string;
    sourceCategory?: "user" | "extensions";
    refIds?: string;
    performerIds?: string;
    tagged?: boolean;
    minConfidence?: number;
    minDurationSec?: number;
    confidence?: number;
    confidence2?: number;
    confidenceModifier?: string;
    durationSec?: number;
    durationSec2?: number;
    durationModifier?: string;
    sort?: string;
    direction?: "asc" | "desc";
    seed?: number;
    page?: number;
    perPage?: number;
    ids?: string;
    excludeVideoIds?: string;
    title?: string;
    titleModifier?: string;
    hostType?: string;
    sourceRunId?: string;
    sourceRunIdModifier?: string;
    colorHint?: string;
    colorHintModifier?: string;
    hasImage?: boolean;
    hasPayload?: boolean;
    startSec?: number;
    startSec2?: number;
    startSecModifier?: string;
    endSec?: number;
    endSec2?: number;
    endSecModifier?: string;
    createdAt?: string;
    createdAt2?: string;
    createdAtModifier?: string;
    updatedAt?: string;
    updatedAt2?: string;
    updatedAtModifier?: string;
    includeAggregate?: boolean;
  }) =>
    request<PaginatedResponse<SegmentRecord>>(`/segments${buildQuery(undefined, opts)}`),
  get: (id: number) => requestOptional<SegmentRecord>(`/segments/${id}`),
  removeTag: (data: { tagId: number; ids: number[] }) =>
    request<{ count: number }>("/segments/bulk/remove-tag", { method: "POST", body: JSON.stringify(data) }),
  distinctSourceKeys: () => request<{ value: string; count: number }[]>("/segments/source-keys/distinct"),
  distinctKinds: () => request<{ value: string; count: number }[]>("/segments/kinds/distinct"),
};

// ===== Faces =====
type FaceListOptions = { q?: string; performerId?: number; performerIds?: string; linked?: boolean; ignored?: boolean; merged?: boolean; mergedIntoFaceId?: number; label?: string; labelModifier?: string; primarySourceKey?: string; primarySourceKeyModifier?: string; hasCover?: boolean; detectionCount?: number; detectionCount2?: number; detectionCountModifier?: string; appearanceCount?: number; appearanceCount2?: number; appearanceCountModifier?: string; frameSampleCount?: number; frameSampleCount2?: number; frameSampleCountModifier?: string; videoCount?: number; videoCount2?: number; videoCountModifier?: string; imageCount?: number; imageCount2?: number; imageCountModifier?: string; minSuggestionConfidence?: number; suggestionConfidence?: number; suggestionConfidence2?: number; suggestionConfidenceModifier?: string; topSuggestionPerformerIds?: string; sort?: string; direction?: "asc" | "desc"; seed?: number; customFieldCriteria?: CustomFieldCriterion[]; page?: number; perPage?: number };

export const faces: {
  list: (opts?: FaceListOptions) => Promise<PaginatedResponse<Face>>;
  get: (id: number) => Promise<Face>;
  appearances: (id: number, opts?: { q?: string; sort?: string; direction?: "asc" | "desc"; seed?: number; page?: number; perPage?: number }) => Promise<PaginatedResponse<FaceAppearance>>;
  videoFaces: (videoId: number) => Promise<FaceHostFace[]>;
  imageFaces: (imageId: number) => Promise<FaceHostFace[]>;
  performerFaces: (performerId: number) => Promise<Face[]>;
  reviewUnlinked: (take?: number) => Promise<Face[]>;
  reviewAiRun: (opts: { startedAt: string; completedAt: string; take?: number }) => Promise<Face[]>;
  detections: (id: number) => Promise<Detection[]>;
  detectionCropUrl: (detectionId: number, max?: number, context?: number) => string;
  deleteImpact: (id: number) => Promise<FaceDeleteImpact>;
  create: (data: FaceCreate) => Promise<Face>;
  update: (id: number, data: FaceUpdate) => Promise<Face>;
  delete: (id: number) => Promise<void>;
  batchLinkTopSuggestion: (data: FaceBatchLinkTopSuggestionRequest) => Promise<FaceBatchOperationResult>;
  batchDelete: (data: FaceBatchDeleteRequest) => Promise<BulkDeletionJobStart>;
  createPerformer: (id: number, data: FaceCreatePerformer) => Promise<Face>;
  link: (id: number, data: FaceLink) => Promise<Face>;
  mergeInto: (id: number, data: FaceMerge) => Promise<Face>;
  setIgnored: (id: number, data: FaceIgnore) => Promise<Face>;
  similar: (id: number, opts?: { kindFamily?: string; k?: number; q?: string; sort?: string; direction?: "asc" | "desc"; seed?: number; page?: number; perPage?: number }) => Promise<PaginatedResponse<FaceSimilar>>;
  suggestions: (id: number, maxResults?: number) => Promise<FaceSuggestion[]>;
  recordSuggestionDecision: (id: number, data: { performerId: number; decision: "accept" | "reject" | "merge"; setPerformerImage?: boolean; secondaryPerformerIds?: number[]; referenceEndpoint?: string; referenceExternalId?: string; referenceUpdateMetadata?: boolean }) => Promise<Face>;
  markNotPresent: (id: number, data: { hostType: "video" | "image"; hostId: number }) => Promise<FaceNotPresentResult>;
  hostTracks: (id: number, opts: { hostType: "video" | "image"; hostId: number }) => Promise<FaceHostTrack[]>;
  split: (id: number, data: { hostType: "video" | "image"; hostId: number; groupKeys: string[] }) => Promise<FaceSplitResult>;
  capabilities: () => Promise<FaceCapabilities>;
} = {
  list: (opts?: FaceListOptions) =>
    request<PaginatedResponse<Face>>(`/faces${buildQuery({ page: opts?.page, perPage: opts?.perPage, q: opts?.q }, {
      performerId: opts?.performerId,
      performerIds: opts?.performerIds,
      linked: opts?.linked,
      ignored: opts?.ignored,
      merged: opts?.merged,
      mergedIntoFaceId: opts?.mergedIntoFaceId,
      label: opts?.label,
      labelModifier: opts?.labelModifier,
      primarySourceKey: opts?.primarySourceKey,
      primarySourceKeyModifier: opts?.primarySourceKeyModifier,
      hasCover: opts?.hasCover,
      detectionCount: opts?.detectionCount,
      detectionCount2: opts?.detectionCount2,
      detectionCountModifier: opts?.detectionCountModifier,
      appearanceCount: opts?.appearanceCount,
      appearanceCount2: opts?.appearanceCount2,
      appearanceCountModifier: opts?.appearanceCountModifier,
      frameSampleCount: opts?.frameSampleCount,
      frameSampleCount2: opts?.frameSampleCount2,
      frameSampleCountModifier: opts?.frameSampleCountModifier,
      videoCount: opts?.videoCount,
      videoCount2: opts?.videoCount2,
      videoCountModifier: opts?.videoCountModifier,
      imageCount: opts?.imageCount,
      imageCount2: opts?.imageCount2,
      imageCountModifier: opts?.imageCountModifier,
      minSuggestionConfidence: opts?.minSuggestionConfidence,
      suggestionConfidence: opts?.suggestionConfidence,
      suggestionConfidence2: opts?.suggestionConfidence2,
      suggestionConfidenceModifier: opts?.suggestionConfidenceModifier,
      topSuggestionPerformerIds: opts?.topSuggestionPerformerIds,
      sort: opts?.sort,
      direction: opts?.direction,
      seed: opts?.seed,
      customFieldCriteria: opts?.customFieldCriteria && opts.customFieldCriteria.length > 0 ? JSON.stringify(opts.customFieldCriteria) : undefined,
    })}`),
  get: (id: number) => request<Face>(`/faces/${id}`),
  appearances: (id: number, opts?: { q?: string; sort?: string; direction?: "asc" | "desc"; seed?: number; page?: number; perPage?: number }) =>
    request<PaginatedResponse<FaceAppearance>>(`/faces/${id}/appearances${buildQuery({ page: opts?.page, perPage: opts?.perPage, q: opts?.q, seed: opts?.seed }, { sort: opts?.sort, direction: opts?.direction })}`),
  videoFaces: (videoId: number) => request<FaceHostFace[]>(`/videos/${videoId}/faces`),
  imageFaces: (imageId: number) => request<FaceHostFace[]>(`/images/${imageId}/faces`),
  performerFaces: (performerId: number) => request<Face[]>(`/performers/${performerId}/faces`),
  reviewUnlinked: (take?: number) => request<Face[]>(`/faces/review/unlinked${buildQuery(undefined, { take })}`),
  reviewAiRun: (opts: { startedAt: string; completedAt: string; take?: number }) =>
    request<Face[]>(`/faces/review/ai-run${buildQuery(undefined, { startedAt: opts.startedAt, completedAt: opts.completedAt, take: opts.take })}`),
  detections: (id: number) => request<Detection[]>(`/faces/${id}/detections`),
  // `context` scales how much frame surrounds the detection box (server default 1.8, a portrait
  // crop). Pass a value near 1 when the point is to tell adjacent detections apart.
  detectionCropUrl: (detectionId: number, max?: number, context?: number) =>
    buildMediaUrl(`/stream/detection/${detectionId}/crop`, undefined, max, { context }),
  deleteImpact: (id: number) => request<FaceDeleteImpact>(`/faces/${id}/delete-impact`),
  create: (data: FaceCreate) => request<Face>("/faces", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: FaceUpdate) => request<Face>(`/faces/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/faces/${id}`, { method: "DELETE" }),
  batchLinkTopSuggestion: (data: FaceBatchLinkTopSuggestionRequest) =>
    request<FaceBatchOperationResult>("/faces/batch/link-top-suggestion", { method: "POST", body: JSON.stringify(data) }),
  batchDelete: (data: FaceBatchDeleteRequest) =>
    request<BulkDeletionJobStart>("/faces/batch/delete", { method: "POST", body: JSON.stringify(data) }),
  createPerformer: (id: number, data: FaceCreatePerformer) => request<Face>(`/faces/${id}/create-performer`, { method: "POST", body: JSON.stringify(data) }),
  link: (id: number, data: FaceLink) => request<Face>(`/faces/${id}/link`, { method: "POST", body: JSON.stringify(data) }),
  mergeInto: (id: number, data: FaceMerge) => request<Face>(`/faces/${id}/merge-into`, { method: "POST", body: JSON.stringify(data) }),
  setIgnored: (id: number, data: FaceIgnore) => request<Face>(`/faces/${id}/ignore`, { method: "POST", body: JSON.stringify(data) }),
  similar: (id: number, opts?: { kindFamily?: string; k?: number; q?: string; sort?: string; direction?: "asc" | "desc"; seed?: number; page?: number; perPage?: number }) =>
    request<PaginatedResponse<FaceSimilar>>(`/faces/${id}/similar${buildQuery({ page: opts?.page, perPage: opts?.perPage, q: opts?.q, seed: opts?.seed }, { kindFamily: opts?.kindFamily, k: opts?.k, sort: opts?.sort, direction: opts?.direction })}`),
  suggestions: (id: number, maxResults?: number) =>
    request<FaceSuggestion[]>(`/faces/${id}/suggestions${buildQuery(undefined, { maxResults })}`),
  recordSuggestionDecision: (id: number, data: { performerId: number; decision: "accept" | "reject" | "merge"; setPerformerImage?: boolean; secondaryPerformerIds?: number[]; referenceEndpoint?: string; referenceExternalId?: string; referenceUpdateMetadata?: boolean }) =>
    request<Face>(`/faces/${id}/suggestions/decision`, { method: "POST", body: JSON.stringify(data) }),
  // Handled by the AI.Faces extension (ext endpoint), which owns the face-embedding split logic.
  // Occurrence editing is served by Cove itself and fulfilled by whichever extension registers an
  // IFaceOccurrenceEditor, so these are plain host routes — no extension id appears here. When nothing
  // provides the capability they answer 501, and faces.capabilities() lets the UI hide the actions.
  markNotPresent: (id: number, data: { hostType: "video" | "image"; hostId: number }) =>
    request<FaceNotPresentResult>(`/faces/${id}/not-present`, { method: "POST", body: JSON.stringify(data) }),
  // The face's separate tracked appearances on one host, and the finer-grained counterpart to
  // markNotPresent: it moves only the named appearances, so two performers tangled inside one video can
  // be pulled apart without rejecting the face from the whole video.
  hostTracks: (id: number, opts: { hostType: "video" | "image"; hostId: number }) =>
    request<FaceHostTrack[]>(`/faces/${id}/host-tracks${buildQuery(undefined, { hostType: opts.hostType, hostId: opts.hostId })}`),
  split: (id: number, data: { hostType: "video" | "image"; hostId: number; groupKeys: string[] }) =>
    request<FaceSplitResult>(`/faces/${id}/split`, { method: "POST", body: JSON.stringify(data) }),
  capabilities: () => request<FaceCapabilities>("/faces/capabilities"),
};

export const entityEngagement = {
  get: (hostType: AffinityHostType, hostId: number) => requestOptional<EntityEngagement>(`/engagement/${hostType}/${hostId}`),
  getRatings: (hostType: AffinityHostType, hostId: number) => request<EntityRatings>(`/engagement/${hostType}/${hostId}/ratings`),
  batch: (data: EntityEngagementBatchRequest) =>
    request<EntityEngagement[]>("/engagement/batch", { method: "POST", body: JSON.stringify(data) }),
  setFavorite: (hostType: AffinityHostType, hostId: number, data: EntityFavorite) =>
    request<EntityEngagement>(`/engagement/${hostType}/${hostId}/favorite`, { method: "PUT", body: JSON.stringify(data) }),
  setRating: (hostType: AffinityHostType, hostId: number, data: { value: number | null; aspect?: string }) =>
    request<EntityEngagement>(`/engagement/${hostType}/${hostId}/rating`, { method: "PUT", body: JSON.stringify(data) }),
  recordInteraction: (data: EngagementInteractionWrite) =>
    request<void>("/engagement/interactions", { method: "POST", body: JSON.stringify(data) }),
  getInteractions: (options?: { hostType?: string; hostId?: number; limit?: number }) =>
    request<EngagementInteraction[]>(`/engagement/interactions${buildQuery(undefined, options)}`),
  resetAllActivity: () => request<{ reset: number }>("/engagement/activity/reset-all", { method: "POST" }),
  wipeAll: () => request<{ wiped: number }>("/engagement/wipe-all", { method: "POST" }),
};

// ===== Performers =====
export const performers = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Performer>>(`/performers${buildQuery(filter, extra)}`),
  findFiltered: (req: PerformerFilteredQueryRequest) =>
    request<PaginatedResponse<Performer>>("/performers/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Performer>(`/performers/${id}`),
  groups: (id: number, filter?: FindFilter) =>
    request<PaginatedResponse<Group>>(`/performers/${id}/groups${buildQuery(filter)}`),
  appearsWith: (id: number, filter?: FindFilter) =>
    request<PaginatedResponse<Performer>>(`/performers/${id}/appears-with${buildQuery(filter)}`),
  create: (data: PerformerCreate) => request<Performer>("/performers", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: PerformerUpdate) => request<Performer>(`/performers/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  scrape: (id: number, data: PerformerScrapeRequest) => request<Performer>(`/performers/${id}/scrape`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  scrapeUrl: (id: number, data?: { url?: string; createMissingTags?: boolean }) =>
    request<Performer>(`/performers/${id}/scrape-url`, { method: "POST", body: JSON.stringify(data ?? {}), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  previewScrape: (id: number, data: PerformerScrapeRequest) => request<import("./types").PerformerScrapePreview>(`/performers/${id}/scrape-preview`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  applyScraped: (id: number, data: { scraped: import("./types").ScrapedPerformer; createMissingTags?: boolean; replaceFields?: string[]; collectionModes?: Record<string, string> }) => request<Performer>(`/performers/${id}/apply-scraped`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  bulkUpdate: (data: BulkPerformerUpdate) => request<void>("/performers/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/performers/${id}`, { method: "DELETE" }),
  bulkDelete: (ids: number[]) => request<BulkDeletionJobStart>("/performers/bulk", { method: "DELETE", body: JSON.stringify({ ids }) }),
  merge: (targetId: number, sourceIds: number[]) =>
    request<Performer>("/performers/merge", { method: "POST", body: JSON.stringify({ targetId, sourceIds }) }),
  searchMetadataServer: (id: number, term?: string, endpoint?: string) =>
    request<MetadataServerPerformerMatch[]>(`/performers/${id}/metadata-server/search${buildQuery(undefined, { term, endpoint })}`),
  findMetadataServerByIds: (data: MetadataServerFindByIdsRequest) =>
    request<MetadataServerPerformerMatch[]>("/performers/metadata-server/find-by-ids", { method: "POST", body: JSON.stringify(data) }),
  importFromMetadataServer: (id: number, data: MetadataServerPerformerImportRequest) =>
    request<Performer>(`/performers/${id}/metadata-server/import`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  submitMetadataServerDraft: (id: number, endpoint: string) =>
    request<{ draftId: string | null }>(`/performers/${id}/metadata-server/submit-draft`, { method: "POST", body: JSON.stringify({ endpoint }) }),
  batchTagMetadataServer: (data: MetadataServerPerformerBatchTagRequest) =>
    request<{ jobId: string; itemCount: number }>("/performers/metadata-server/batch-tag", { method: "POST", body: JSON.stringify(data) }),
};

// ===== Tags =====
export const tags = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Tag>>(`/tags${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<TagFilterCriteria>) =>
    request<PaginatedResponse<Tag>>("/tags/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  graph: (req: FilteredQueryRequest<TagFilterCriteria>) =>
    request<TagGraphResponse>("/tags/graph", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number, depth?: number) => request<TagDetail>(`/tags/${id}${buildQuery(undefined, { depth })}`),
  segments: (id: number, count = 100) => request<TagSegmentWall[]>(`/tags/${id}/segments${buildQuery(undefined, { count })}`),
  create: (data: TagCreate) => request<TagDetail>("/tags", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: TagUpdate) => request<TagDetail>(`/tags/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  bulkUpdate: (data: BulkTagUpdate) => request<void>("/tags/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/tags/${id}`, { method: "DELETE" }),
  bulkDelete: (ids: number[]) => request<BulkDeletionJobStart>("/tags/bulk", { method: "DELETE", body: JSON.stringify({ ids }) }),
  merge: (targetId: number, sourceIds: number[]) =>
    request<TagDetail>("/tags/merge", { method: "POST", body: JSON.stringify({ targetId, sourceIds }) }),
  searchMetadataServer: (id: number, term?: string, endpoint?: string) =>
    request<MetadataServerTagMatch[]>(`/tags/${id}/metadata-server/search${buildQuery(undefined, { term, endpoint })}`),
  findMetadataServerByIds: (data: MetadataServerFindByIdsRequest) =>
    request<MetadataServerTagMatch[]>("/tags/metadata-server/find-by-ids", { method: "POST", body: JSON.stringify(data) }),
  importFromMetadataServer: (id: number, data: MetadataServerTagImportRequest) =>
    request<TagDetail>(`/tags/${id}/metadata-server/import`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  submitMetadataServerDraft: (id: number, endpoint: string) =>
    request<{ draftId: string | null }>(`/tags/${id}/metadata-server/submit-draft`, { method: "POST", body: JSON.stringify({ endpoint }) }),
  batchTagMetadataServer: (data: MetadataServerTagBatchTagRequest) =>
    request<{ jobId: string; itemCount: number }>("/tags/metadata-server/batch-tag", { method: "POST", body: JSON.stringify(data) }),
};

export const tagGroups = {
  list: () => request<TagGroup[]>("/taggroups"),
  get: (id: number) => request<TagGroup>(`/taggroups/${id}`),
  create: (data: TagGroupCreate) => request<TagGroup>("/taggroups", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: TagGroupUpdate) => request<TagGroup>(`/taggroups/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/taggroups/${id}`, { method: "DELETE" }),
};

export const tagApplications = {
  list: (params?: { hostType?: string; hostId?: number; contextType?: string; contextId?: number }) =>
    request<TagApplication[]>(`/tagapplications${buildQuery(undefined, params)}`),
  create: (data: TagApplicationCreate) => request<TagApplication>("/tagapplications", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/tagapplications/${id}`, { method: "DELETE" }),
  // "Report incorrect detection": drop the AI's host-level applications for one (host, tag) so a
  // wrongly-derived tag falls off this host. Does not touch the tag's global threshold or segments.
  reportIncorrect: (hostType: string, hostId: number, tagId: number) =>
    request<void>(`/tagapplications/host/${hostType}/${hostId}/tag/${tagId}`, { method: "DELETE" }),
};

export const aiData = {
  summary: (selector?: AiDataSelector) => request<AiDataSummary>(`/ai-data/summary${buildAiDataQuery(selector)}`),
  purge: (request_: AiDataPurgeRequest) => request<AiDataPurgeResult>("/ai-data/purge", { method: "POST", body: JSON.stringify(request_) }),
};

function normalizeExtensionApiBasePath(apiBasePath: string): string {
  const normalizedPath = normalizeApiPath(apiBasePath);
  return normalizedPath.endsWith("/") ? normalizedPath.slice(0, -1) : normalizedPath;
}

export function createVisualSimilarityClient(apiBasePath: string) {
  const normalizedBasePath = normalizeExtensionApiBasePath(apiBasePath);

  return {
    searchVideos: (req: VideoFilteredQueryRequest) =>
      request<PaginatedResponse<Video>>(`${normalizedBasePath}/videos/search`, { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
    searchImages: (req: FilteredQueryRequest<ImageFilterCriteria>) =>
      request<PaginatedResponse<Image>>(`${normalizedBasePath}/images/search`, { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
    similarVideosForVideo: (videoId: number, params?: { perPage?: number }) =>
      request<{ items: VisualSimilarVideo[] }>(`${normalizedBasePath}/videos/${videoId}/similar-videos${buildQuery(params)}`),
    similarImagesForVideo: (videoId: number, params?: { perPage?: number }) =>
      request<{ items: VisualSimilarImage[] }>(`${normalizedBasePath}/videos/${videoId}/similar-images${buildQuery(params)}`),
    similarVideosForImage: (imageId: number, params?: { perPage?: number }) =>
      request<{ items: VisualSimilarVideo[] }>(`${normalizedBasePath}/images/${imageId}/similar-videos${buildQuery(params)}`),
    similarImagesForImage: (imageId: number, params?: { perPage?: number }) =>
      request<{ items: VisualSimilarImage[] }>(`${normalizedBasePath}/images/${imageId}/similar-images${buildQuery(params)}`),
    similarVideosForVideoSegment: (videoId: number, data: { intervals: Array<{ startSec: number; endSec?: number }>; perPage?: number }) =>
      request<{ items: VisualSimilarVideo[] }>(`${normalizedBasePath}/videos/${videoId}/similar-videos/segment`, { method: "POST", body: JSON.stringify(data) }),
    videoHasEmbeddings: (videoId: number) =>
      request<{ hasEmbeddings: boolean }>(`${normalizedBasePath}/videos/${videoId}/has-embeddings`),
    imageHasEmbeddings: (imageId: number) =>
      request<{ hasEmbeddings: boolean }>(`${normalizedBasePath}/images/${imageId}/has-embeddings`),
  };
}

export function createAudioSimilarityClient(apiBasePath: string) {
  const normalizedBasePath = normalizeExtensionApiBasePath(apiBasePath);

  return {
    similarVideosForVideo: (videoId: number, params?: { perPage?: number }) =>
      request<{ items: AudioSimilarVideo[] }>(`${normalizedBasePath}/videos/${videoId}/similar-videos${buildQuery(params)}`),
    videoHasEmbeddings: (videoId: number) =>
      request<{ hasEmbeddings: boolean }>(`${normalizedBasePath}/videos/${videoId}/has-embeddings`),
  };
}

// ===== Studios =====
export const studios = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Studio>>(`/studios${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<StudioFilterCriteria>) =>
    request<PaginatedResponse<Studio>>("/studios/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number, depth?: number) => request<Studio>(`/studios/${id}${buildQuery(undefined, { depth })}`),
  create: (data: StudioCreate) => request<Studio>("/studios", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: StudioUpdate) => request<Studio>(`/studios/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  bulkUpdate: (data: BulkStudioUpdate) => request<void>("/studios/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/studios/${id}`, { method: "DELETE" }),
  bulkDelete: (ids: number[]) => request<BulkDeletionJobStart>("/studios/bulk", { method: "DELETE", body: JSON.stringify({ ids }) }),
  merge: (targetId: number, sourceIds: number[]) =>
    request<Studio>("/studios/merge", { method: "POST", body: JSON.stringify({ targetId, sourceIds }) }),
  searchMetadataServer: (id: number, term?: string, endpoint?: string) => {
    const params = new URLSearchParams();
    if (term) params.set("term", term);
    if (endpoint) params.set("endpoint", endpoint);
    const qs = params.toString();
    return request<MetadataServerStudioMatch[]>(`/studios/${id}/metadata-server/search${qs ? `?${qs}` : ""}`);
  },
  findMetadataServerByIds: (data: MetadataServerFindByIdsRequest) =>
    request<MetadataServerStudioMatch[]>("/studios/metadata-server/find-by-ids", { method: "POST", body: JSON.stringify(data) }),
  importFromMetadataServer: (id: number, data: MetadataServerStudioImportRequest) =>
    request<Studio>(`/studios/${id}/metadata-server/import`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  submitMetadataServerDraft: (id: number, endpoint: string) =>
    request<{ draftId: string | null }>(`/studios/${id}/metadata-server/submit-draft`, { method: "POST", body: JSON.stringify({ endpoint }) }),
  batchTagMetadataServer: (data: MetadataServerStudioBatchTagRequest) =>
    request<{ jobId: string; itemCount: number }>("/studios/metadata-server/batch-tag", { method: "POST", body: JSON.stringify(data) }),
};

// ===== Galleries =====
export const galleries = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Gallery>>(`/galleries${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<GalleryFilterCriteria>) =>
    request<PaginatedResponse<Gallery>>("/galleries/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  aggregate: (req: FilteredQueryRequest<GalleryFilterCriteria>) =>
    request<GalleryAggregate>("/galleries/aggregate", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Gallery>(`/galleries/${id}`),
  getLikeCount: (id: number) => request<number>(`/galleries/${id}/like-count`),
  create: (data: GalleryCreate) => request<Gallery>("/galleries", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: GalleryUpdate) => request<Gallery>(`/galleries/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  bulkUpdate: (data: BulkGalleryUpdate) => request<void>("/galleries/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/galleries/${id}`, { method: "DELETE" }),
  rescan: (id: number) => request<{ jobId: string }>(`/galleries/${id}/rescan`, { method: "POST" }),
  bulkDelete: (ids: number[]) => request<BulkDeletionJobStart>("/galleries/bulk", { method: "DELETE", body: JSON.stringify({ ids }) }),
  chapters: (id: number) => request<GalleryChapter[]>(`/galleries/${id}/chapters`),
  createChapter: (id: number, data: GalleryChapterCreate) =>
    request<GalleryChapter>(`/galleries/${id}/chapters`, { method: "POST", body: JSON.stringify(data) }),
  updateChapter: (galleryId: number, chapterId: number, data: GalleryChapterUpdate) =>
    request<GalleryChapter>(`/galleries/${galleryId}/chapters/${chapterId}`, { method: "PUT", body: JSON.stringify(data) }),
  deleteChapter: (galleryId: number, chapterId: number) =>
    request<void>(`/galleries/${galleryId}/chapters/${chapterId}`, { method: "DELETE" }),
  addImages: (id: number, imageIds: number[]) =>
    request<{ added: number }>(`/galleries/${id}/images`, { method: "POST", body: JSON.stringify({ imageIds }) }),
  removeImages: (id: number, imageIds: number[]) =>
    request<{ removed: number }>(`/galleries/${id}/images`, { method: "DELETE", body: JSON.stringify({ imageIds }) }),
  uploadCoverImage: (id: number, file: File) => {
    const formData = new FormData();
    formData.append("file", file);
    return request<void>(`/galleries/${id}/image`, { method: "POST", body: formData, timeoutMs: UPLOAD_REQUEST_TIMEOUT_MS });
  },
  coverUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/galleries/${id}/cover`, version, max),
  getCoverImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/galleries/${id}/image`, version, max),
  deleteCoverImage: (id: number) => request<void>(`/galleries/${id}/image`, { method: "DELETE" }),
  setCover: (id: number, imageId: number) =>
    request<void>(`/galleries/${id}/cover`, { method: "PUT", body: JSON.stringify({ imageId }) }),
  resetCover: (id: number) => request<void>(`/galleries/${id}/cover`, { method: "DELETE" }),
};

// ===== Images =====
export const images = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Image>>(`/images${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<ImageFilterCriteria>) =>
    request<PaginatedResponse<Image>>("/images/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  aggregate: (req: FilteredQueryRequest<ImageFilterCriteria>) =>
    request<ImageAggregate>("/images/aggregate", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Image>(`/images/${id}`),
  create: (data: ImageCreate) => request<Image>("/images", { method: "POST", body: JSON.stringify(data) }),
  createFromFile: (data: FileBackedCreate) => request<Image>("/images/from-file", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: ImageUpdate) => request<Image>(`/images/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  rescan: (id: number) => request<{ jobId: string }>(`/images/${id}/rescan`, { method: "POST" }),
  bulkUpdate: (data: BulkImageUpdate) => request<void>("/images/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number, options?: boolean | DeleteEntityOptions) => {
    const deleteFile = typeof options === "boolean" ? options : options?.deleteFile;
    const deleteGenerated = typeof options === "boolean" ? undefined : options?.deleteGenerated;
    return request<void>(`/images/${id}${buildQuery(undefined, { deleteFile, deleteGenerated })}`, { method: "DELETE" });
  },
  bulkDelete: (ids: number[], options?: boolean | DeleteEntityOptions) => {
    const deleteFiles = typeof options === "boolean" ? options : options?.deleteFile ?? false;
    const deleteGenerated = typeof options === "boolean" ? false : options?.deleteGenerated ?? false;
    return request<BulkDeletionJobStart>("/images/bulk", { method: "DELETE", body: JSON.stringify({ ids, deleteFiles, deleteGenerated }) });
  },
  incrementLike: (id: number) => request<number>(`/images/${id}/like`, { method: "POST" }),
  addHistoricalLike: (id: number, at: string) => request<number>(`/images/${id}/like/historical`, { method: "POST", body: JSON.stringify({ at }) }),
  deleteLikeFromHistory: (id: number, at: string) => request<void>(`/images/${id}/like/history?at=${encodeURIComponent(at)}`, { method: "DELETE" }),
  decrementLike: (id: number) => request<number>(`/images/${id}/like`, { method: "DELETE" }),
  resetLike: (id: number) => request<number>(`/images/${id}/like/reset`, { method: "POST" }),
  getHistory: (id: number) => request<VideoHistory>(`/images/${id}/history`),
  detections: {
    list: (imageId: number) => request<Detection[]>(`/images/${imageId}/detections`),
    create: (imageId: number, data: DetectionCreate) =>
      request<Detection>(`/images/${imageId}/detections`, { method: "POST", body: JSON.stringify(data) }),
    update: (imageId: number, id: number, data: DetectionUpdate) =>
      request<Detection>(`/images/${imageId}/detections/${id}`, { method: "PUT", body: JSON.stringify(data) }),
    delete: (imageId: number, id: number) =>
      request<void>(`/images/${imageId}/detections/${id}`, { method: "DELETE" }),
  },
  imageUrl: (id: number) => buildMediaUrl(`/stream/image/${id}`),
  thumbnailUrl: (id: number, max?: number) => buildMediaUrl(`/stream/image/${id}/thumbnail`, undefined, max),
};

// ===== Audios =====
export const audios = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Audio>>(`/audios${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<AudioFilterCriteria>) =>
    request<PaginatedResponse<Audio>>("/audios/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  aggregate: (req: FilteredQueryRequest<AudioFilterCriteria>) =>
    request<AudioAggregate>("/audios/aggregate", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Audio>(`/audios/${id}`),
  create: (data: AudioCreate) => request<Audio>("/audios", { method: "POST", body: JSON.stringify(data) }),
  createFromFile: (data: FileBackedCreate) => request<Audio>("/audios/from-file", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: AudioUpdate) => request<Audio>(`/audios/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  rescan: (id: number) => request<{ jobId: string }>(`/audios/${id}/rescan`, { method: "POST" }),
  bulkUpdate: (data: BulkAudioUpdate) => request<void>("/audios/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number, options?: DeleteEntityOptions) =>
    request<void>(`/audios/${id}${buildQuery(undefined, { deleteFile: options?.deleteFile, deleteGenerated: options?.deleteGenerated })}`, { method: "DELETE" }),
  bulkDelete: (ids: number[], options?: DeleteEntityOptions) =>
    request<BulkDeletionJobStart>("/audios/bulk", { method: "DELETE", body: JSON.stringify({ ids, deleteFiles: options?.deleteFile ?? false, deleteGenerated: options?.deleteGenerated ?? false }) }),
  getHistory: (id: number) => request<VideoHistory>(`/audios/${id}/history`),
  incrementLike: (id: number) => request<number>(`/audios/${id}/like`, { method: "POST" }),
  addHistoricalLike: (id: number, at: string) => request<number>(`/audios/${id}/like/historical`, { method: "POST", body: JSON.stringify({ at }) }),
  deleteLikeFromHistory: (id: number, at: string) => request<void>(`/audios/${id}/like/history?at=${encodeURIComponent(at)}`, { method: "DELETE" }),
  decrementLike: (id: number) => request<number>(`/audios/${id}/like`, { method: "DELETE" }),
  resetLike: (id: number) => request<number>(`/audios/${id}/like/reset`, { method: "POST" }),
  resetActivity: (id: number) => request<void>(`/audios/${id}/activity/reset`, { method: "POST" }),
  streamUrl: (id: number) => buildMediaUrl(`/audios/${id}/stream`),
};

// ===== Texts =====
export const texts = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<TextDocument>>(`/texts${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<TextFilterCriteria>) =>
    request<PaginatedResponse<TextDocument>>("/texts/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  aggregate: (req: FilteredQueryRequest<TextFilterCriteria>) =>
    request<TextAggregate>("/texts/aggregate", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<TextDocument>(`/texts/${id}`),
  content: (id: number) => request<TextContent>(`/texts/${id}/content`),
  create: (data: TextCreate) => request<TextDocument>("/texts", { method: "POST", body: JSON.stringify(data) }),
  createFromFile: (data: FileBackedCreate) => request<TextDocument>("/texts/from-file", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: TextUpdate) => request<TextDocument>(`/texts/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  rescan: (id: number) => request<{ jobId: string }>(`/texts/${id}/rescan`, { method: "POST" }),
  bulkUpdate: (data: BulkTextUpdate) => request<void>("/texts/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number, options?: DeleteEntityOptions) =>
    request<void>(`/texts/${id}${buildQuery(undefined, { deleteFile: options?.deleteFile, deleteGenerated: options?.deleteGenerated })}`, { method: "DELETE" }),
  bulkDelete: (ids: number[], options?: DeleteEntityOptions) =>
    request<BulkDeletionJobStart>("/texts/bulk", { method: "DELETE", body: JSON.stringify({ ids, deleteFiles: options?.deleteFile ?? false, deleteGenerated: options?.deleteGenerated ?? false }) }),
  getHistory: (id: number) => request<VideoHistory>(`/texts/${id}/history`),
  incrementLike: (id: number) => request<number>(`/texts/${id}/like`, { method: "POST" }),
  addHistoricalLike: (id: number, at: string) => request<number>(`/texts/${id}/like/historical`, { method: "POST", body: JSON.stringify({ at }) }),
  deleteLikeFromHistory: (id: number, at: string) => request<void>(`/texts/${id}/like/history?at=${encodeURIComponent(at)}`, { method: "DELETE" }),
  decrementLike: (id: number) => request<number>(`/texts/${id}/like`, { method: "DELETE" }),
  resetLike: (id: number) => request<number>(`/texts/${id}/like/reset`, { method: "POST" }),
  fileUrl: (id: number) => buildMediaUrl(`/texts/${id}/file`),
};

// ===== Groups =====
export const groups = {
  find: (filter?: FindFilter, extra?: Record<string, string | number | boolean | undefined>) =>
    request<PaginatedResponse<Group>>(`/groups${buildQuery(filter, extra)}`),
  findFiltered: (req: FilteredQueryRequest<GroupFilterCriteria>) =>
    request<PaginatedResponse<Group>>("/groups/find", { method: "POST", body: JSON.stringify(normalizeCriterionPayload(req)) }),
  get: (id: number) => request<Group>(`/groups/${id}`),
  create: (data: GroupCreate) => request<Group>("/groups", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: GroupUpdate) => request<Group>(`/groups/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  bulkUpdate: (data: BulkGroupUpdate) => request<void>("/groups/bulk", { method: "POST", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/groups/${id}`, { method: "DELETE" }),
  bulkDelete: (ids: number[]) => request<BulkDeletionJobStart>("/groups/bulk", { method: "DELETE", body: JSON.stringify({ ids }) }),
  reorder: (data: GroupReorder) => request<void>("/groups/reorder", { method: "PUT", body: JSON.stringify(data) }),
  dynamicSources: () => request<DynamicGroupSource[]>("/groups/dynamic-sources"),
  subGroups: (id: number) => request<Group[]>(`/groups/${id}/subgroups`),
  containingGroups: (id: number) => request<Group[]>(`/groups/${id}/containinggroups`),
  addSubGroup: (id: number, subGroupId: number, orderIndex?: number) =>
    request<void>(`/groups/${id}/subgroups`, { method: "POST", body: JSON.stringify({ subGroupId, orderIndex }) }),
  removeSubGroup: (id: number, subGroupId: number) =>
    request<void>(`/groups/${id}/subgroups/${subGroupId}`, { method: "DELETE" }),
  reorderSubGroups: (id: number, subGroupIds: number[]) =>
    request<void>(`/groups/${id}/subgroups/reorder`, { method: "PUT", body: JSON.stringify({ subGroupIds }) }),
  items: {
    list: (groupId: number) => request<GroupItem[]>(`/groups/${groupId}/items`),
    page: (groupId: number, filter?: FindFilter) => request<PaginatedResponse<GroupItem>>(`/groups/${groupId}/items/page${buildQuery(filter)}`),
    create: (groupId: number, data: GroupItemCreate) =>
      request<GroupItem>(`/groups/${groupId}/items`, { method: "POST", body: JSON.stringify(data) }),
    update: (groupId: number, itemId: number, data: GroupItemUpdate) =>
      request<GroupItem>(`/groups/${groupId}/items/${itemId}`, { method: "PUT", body: JSON.stringify(data) }),
    delete: (groupId: number, itemId: number) =>
      request<void>(`/groups/${groupId}/items/${itemId}`, { method: "DELETE" }),
    removeHosts: (groupId: number, data: GroupItemsRemoveHosts) =>
      request<void>(`/groups/${groupId}/items/remove-hosts`, { method: "POST", body: JSON.stringify(data) }),
    reorder: (groupId: number, data: GroupItemsReorder) =>
      request<void>(`/groups/${groupId}/items/reorder`, { method: "PUT", body: JSON.stringify(data) }),
    fromSpans: (groupId: number, data: GroupItemsFromSpans) =>
      request<GroupItem[]>(`/groups/${groupId}/items/from-spans`, { method: "POST", body: JSON.stringify(data) }),
    playbackManifest: (groupId: number) =>
      request<GroupPlaybackManifest>(`/groups/${groupId}/playback-manifest`),
  },
};

export const segmentDisplayProfiles = {
  list: () => request<SegmentDisplayProfile[]>("/segment-display-profiles"),
  get: (id: number) => request<SegmentDisplayProfile>(`/segment-display-profiles/${id}`),
  create: (data: SegmentDisplayProfileCreate) =>
    request<SegmentDisplayProfile>("/segment-display-profiles", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: SegmentDisplayProfileUpdate) =>
    request<SegmentDisplayProfile>(`/segment-display-profiles/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/segment-display-profiles/${id}`, { method: "DELETE" }),
  setDefault: (id: number) => request<SegmentDisplayProfile>(`/segment-display-profiles/${id}/default`, { method: "PUT" }),
  preview: (data: import("./types").SegmentDisplayProfilePreviewRequest) =>
    request<import("./types").ResolvedSpanList>("/segment-display-profiles/preview", { method: "POST", body: JSON.stringify(data) }),
  rules: {
    list: (profileId: number) => request<SegmentDisplayRule[]>(`/segment-display-profiles/${profileId}/rules`),
    create: (profileId: number, data: SegmentDisplayRuleCreate) =>
      request<SegmentDisplayRule>(`/segment-display-profiles/${profileId}/rules`, { method: "POST", body: JSON.stringify(data) }),
    update: (profileId: number, ruleId: number, data: SegmentDisplayRuleUpdate) =>
      request<SegmentDisplayRule>(`/segment-display-profiles/${profileId}/rules/${ruleId}`, { method: "PUT", body: JSON.stringify(data) }),
    delete: (profileId: number, ruleId: number) =>
      request<void>(`/segment-display-profiles/${profileId}/rules/${ruleId}`, { method: "DELETE" }),
  },
};

export const segmentSpans = {
  search: (data: SegmentSpanSearchRequest) =>
    request<SegmentSpanSearchResponse>("/segments/spans/search", { method: "POST", body: JSON.stringify(data) }),
  // Exact span total for a filter set, computed/cached server-side. Independent of page/sort/direction.
  count: (data: SegmentSpanSearchRequest) =>
    request<SegmentSpanCountResponse>("/segments/spans/count", { method: "POST", body: JSON.stringify(data) }),
};

// ===== Entity Images =====
async function uploadImage(path: string, file: File): Promise<{ blobId: string }> {
  const formData = new FormData();
  formData.append("file", file);
  const res = await authedFetch(`${API_BASE}${path}`, { method: "POST", body: formData, timeoutMs: UPLOAD_REQUEST_TIMEOUT_MS });
  if (!res.ok) throw new Error(`Upload failed: ${res.status}`);
  return res.json();
}

async function deleteImage(path: string): Promise<void> {
  const res = await authedFetch(`${API_BASE}${path}`, { method: "DELETE" });
  if (!res.ok && res.status !== 404) throw new Error(`Delete failed: ${res.status}`);
}

export const entityImages = {
  videoCoverUrl: (id: number, version?: string, max = 1600) => buildMediaUrl(`/videos/${id}/image`, version, max),
  uploadVideoCoverImage: (id: number, file: File) => uploadImage(`/videos/${id}/image`, file),
  deleteVideoCoverImage: (id: number) => deleteImage(`/videos/${id}/image`),

  segmentCoverUrl: (id: number, version?: string, max = 1600) => buildMediaUrl(`/segments/${id}/image`, version, max),
  uploadSegmentCoverImage: (id: number, file: File) => uploadImage(`/segments/${id}/image`, file),
  deleteSegmentCoverImage: (id: number) => deleteImage(`/segments/${id}/image`),
  setSegmentCoverFromFrame: (id: number, atSeconds?: number) =>
    request<{ success: boolean }>(`/segments/${id}/image/from-frame`, { method: "POST", body: JSON.stringify({ atSeconds }) }),

  performerImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/performers/${id}/image`, version, max),
  uploadPerformerImage: (id: number, file: File) => uploadImage(`/performers/${id}/image`, file),
  deletePerformerImage: (id: number) => deleteImage(`/performers/${id}/image`),
  setPerformerImageFromSource: (id: number, source: { imageId?: number; videoId?: number }) =>
    request<void>(`/performers/${id}/image/source`, { method: "PUT", body: JSON.stringify(source) }),

  audioImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/audios/${id}/image`, version, max),
  uploadAudioImage: (id: number, file: File) => uploadImage(`/audios/${id}/image`, file),
  deleteAudioImage: (id: number) => deleteImage(`/audios/${id}/image`),

  textImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/texts/${id}/image`, version, max),
  uploadTextImage: (id: number, file: File) => uploadImage(`/texts/${id}/image`, file),
  deleteTextImage: (id: number) => deleteImage(`/texts/${id}/image`),

  studioImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/studios/${id}/image`, version, max),
  uploadStudioImage: (id: number, file: File) => uploadImage(`/studios/${id}/image`, file),
  deleteStudioImage: (id: number) => deleteImage(`/studios/${id}/image`),
  setStudioImageFromSource: (id: number, source: { imageId?: number; videoId?: number }) =>
    request<void>(`/studios/${id}/image/source`, { method: "PUT", body: JSON.stringify(source) }),

  tagImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/tags/${id}/image`, version, max),
  uploadTagImage: (id: number, file: File) => uploadImage(`/tags/${id}/image`, file),
  deleteTagImage: (id: number) => deleteImage(`/tags/${id}/image`),
  setTagImageFromSource: (id: number, source: { imageId?: number; videoId?: number }) =>
    request<void>(`/tags/${id}/image/source`, { method: "PUT", body: JSON.stringify(source) }),

  groupFrontImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/groups/${id}/image/front`, version, max),
  uploadGroupFrontImage: (id: number, file: File) => uploadImage(`/groups/${id}/image/front`, file),
  deleteGroupFrontImage: (id: number) => deleteImage(`/groups/${id}/image/front`),
  setGroupFrontImageFromSource: (id: number, source: { imageId?: number; videoId?: number }) =>
    request<void>(`/groups/${id}/image/front/source`, { method: "PUT", body: JSON.stringify(source) }),

  groupBackImageUrl: (id: number, version?: string, max = 640) => buildMediaUrl(`/groups/${id}/image/back`, version, max),
  uploadGroupBackImage: (id: number, file: File) => uploadImage(`/groups/${id}/image/back`, file),
  deleteGroupBackImage: (id: number) => deleteImage(`/groups/${id}/image/back`),

  setGalleryImageFromSource: (id: number, source: { imageId?: number; videoId?: number }) =>
    request<void>(`/galleries/${id}/image/source`, { method: "PUT", body: JSON.stringify(source) }),
};

// ===== System =====
export const system = {
  status: () => request<SystemStatus>("/system/status"),
  shutdown: () => request<{ message: string }>("/system/shutdown", { method: "POST" }),
  stats: () => request<Stats>("/system/stats"),
  getConfig: () => request<CoveConfig>("/system/config"),
  saveConfig: (config: CoveConfig) =>
    request<CoveConfig>("/system/config", { method: "PUT", body: JSON.stringify(config) }),
  getFfmpegCapabilities: (refresh = false) =>
    request<FfmpegCapabilities>(`/system/ffmpeg-capabilities${refresh ? "?refresh=true" : ""}`),
  getLogLevel: () => request<RuntimeLogLevelStatus>("/system/log-level"),
  setLogLevel: (level: string) =>
    request<RuntimeLogLevelStatus>("/system/log-level", { method: "PATCH", body: JSON.stringify({ level }) }),
  uploadFavicon: async (file: File) => {
    const form = new FormData();
    form.append("file", file);
    const res = await authedFetch(`${API_BASE}/system/ui/favicon`, { method: "POST", body: form, timeoutMs: UPLOAD_REQUEST_TIMEOUT_MS });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`API Error ${res.status}: ${text}`);
    }
    return res.json() as Promise<{ path: string; fileName: string }>;
  },
  uploadLogo: async (file: File) => {
    const form = new FormData();
    form.append("file", file);
    const res = await authedFetch(`${API_BASE}/system/ui/logo`, { method: "POST", body: form, timeoutMs: UPLOAD_REQUEST_TIMEOUT_MS });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`API Error ${res.status}: ${text}`);
    }
    return res.json() as Promise<{ path: string; fileName: string }>;
  },
  listScrapers: () => request<ScraperSummary[]>("/system/scrapers"),
  reloadScrapers: () => request<ScraperSummary[]>("/system/scrapers/reload", { method: "POST" }),
  scrapeUrl: (scraperId: string, entityType: string, url: string) =>
    request<Record<string, unknown>>("/system/scrapers/scrape-url", { method: "POST", body: JSON.stringify({ scraperId, entityType, url }), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  scrapeName: (scraperId: string, entityType: string, name: string) =>
    request<Record<string, unknown>[]>("/system/scrapers/scrape-name", { method: "POST", body: JSON.stringify({ scraperId, entityType, name }), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  scrapeFragment: (scraperId: string, entityType: string, fragment: Record<string, unknown>) =>
    request<Record<string, unknown>>("/system/scrapers/scrape-fragment", { method: "POST", body: JSON.stringify({ scraperId, entityType, fragment }), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  listDownloaders: () => request<DownloaderDescriptor[]>("/system/downloaders"),
  matchDownloaders: (data: DownloaderMatchRequest) => request<DownloaderMatch[]>("/system/downloaders/match", { method: "POST", body: JSON.stringify(data) }),
  startDownload: (data: DownloaderStartRequest) => request<{ jobId: string }>("/system/downloaders/download", { method: "POST", body: JSON.stringify(data) }),
  startBatchDownload: (data: DownloaderBatchStartRequest) => request<DownloaderBatchStartResponse>("/system/downloaders/download-batch", { method: "POST", body: JSON.stringify(data) }),
  preflightDownload: (data: DownloaderPreflightRequest) => request<DownloaderPreflightResponse>("/system/downloaders/preflight", { method: "POST", body: JSON.stringify(data) }),
  validateMetadataServer: (metadataServer: MetadataServer) =>
    request<MetadataServerValidationResult>("/system/metadata-servers/validate", { method: "POST", body: JSON.stringify(metadataServer) }),
  configureUI: (input: Record<string, unknown>) =>
    request<{ success: boolean }>("/system/config/ui", { method: "POST", body: JSON.stringify(input) }),
  configureUISetting: (key: string, value: unknown) =>
    request<{ key: string; value: unknown; success: boolean }>(`/system/config/ui/${encodeURIComponent(key)}`, { method: "PUT", body: JSON.stringify(value) }),
};

export const scrapeAttempts = {
  list: (params?: { entityType?: string; entityId?: number; limit?: number }) => {
    const query = new URLSearchParams();
    if (params?.entityType) query.set("entityType", params.entityType);
    if (params?.entityId != null) query.set("entityId", String(params.entityId));
    if (params?.limit != null) query.set("limit", String(params.limit));
    const suffix = query.toString();
    return request<ScrapeAttempt[]>(`/scrape-attempts${suffix ? `?${suffix}` : ""}`);
  },
  get: (id: string) => request<ScrapeAttempt>(`/scrape-attempts/${id}`),
  create: (data: CreateScrapeAttemptRequest) =>
    request<ScrapeAttempt>("/scrape-attempts", { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  apply: (id: string, data: ApplyVideoScrapeAttemptRequest) =>
    request<ScrapeAttempt>(`/scrape-attempts/${id}/apply`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  resolveRelations: (data: ResolveScrapeRelationsRequest) =>
    request<ResolveScrapeRelationsResult>("/scrape-attempts/resolve-relations", { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
  applyVideo: (id: string, data: ApplyVideoScrapeAttemptRequest) =>
    request<ScrapeAttempt>(`/scrape-attempts/${id}/apply`, { method: "POST", body: JSON.stringify(data), timeoutMs: LONG_API_REQUEST_TIMEOUT_MS }),
};

export const bookmarks = {
  list: () => request<BookmarkDto[]>("/me/bookmarks"),
  batch: (data: BookmarkBatchRequest) => request<BookmarkState[]>("/me/bookmarks/batch", { method: "POST", body: JSON.stringify(data) }),
  toggle: (data: BookmarkToggle) => request<BookmarkState>("/me/bookmarks", { method: "POST", body: JSON.stringify(data) }),
};

export const customFields = {
  list: (entityType?: string) => request<CustomFieldDefinition[]>(`/custom-fields${buildQuery(undefined, { entityType })}`),
  create: (data: CustomFieldDefinitionCreate) => request<CustomFieldDefinition>("/custom-fields", { method: "POST", body: JSON.stringify(data) }),
  replaceAll: (data: CustomFieldDefinition[]) => request<CustomFieldDefinition[]>("/custom-fields", { method: "PUT", body: JSON.stringify(data) }),
  update: (id: number, data: CustomFieldDefinitionUpdate) => request<CustomFieldDefinition>(`/custom-fields/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/custom-fields/${id}`, { method: "DELETE" }),
};

// ===== Jobs =====
export const jobs = {
  list: () => request<JobInfo[]>("/jobs"),
  history: () => request<JobInfo[]>("/jobs/history"),
  get: (id: string) => request<JobInfo>(`/jobs/${id}`),
  cancel: (id: string) => request<void>(`/jobs/${id}`, { method: "DELETE" }),
  reorder: (id: string, beforeJobId?: string | null) =>
    request<void>(`/jobs/${id}/reorder`, { method: "PUT", body: JSON.stringify({ beforeJobId: beforeJobId ?? null }) }),
};

// ===== Metadata Tasks =====
export interface ScanOptions {
  paths?: string[];
  scanGenerateCovers?: boolean;
  scanGeneratePreviews?: boolean;
  scanGenerateSprites?: boolean;
  scanGeneratePhashes?: boolean;
  scanGenerateMd5?: boolean;
  scanGenerateThumbnails?: boolean;
  scanGenerateImagePhashes?: boolean;
  scanGenerateAudioPhashes?: boolean;
  scanGenerateTextPhashes?: boolean;
  rescan?: boolean;
}

export interface GenerateOptions {
  thumbnails?: boolean;
  previews?: boolean;
  sprites?: boolean;
  segments?: boolean;
  segmentThumbnails?: boolean;
  segmentPreviews?: boolean;
  phashes?: boolean;
  md5?: boolean;
  imageThumbnails?: boolean;
  imagePhashes?: boolean;
  galleryThumbnails?: boolean;
  audioPhashes?: boolean;
  textPhashes?: boolean;
  overwrite?: boolean;
  videoIds?: number[];
  imageIds?: number[];
  audioIds?: number[];
  textIds?: number[];
  paths?: string[];
}

export interface LibraryFolder {
  name: string;
  path: string;
  hasChildren: boolean;
}

export interface CleanOptions {
  paths?: string[];
  dryRun?: boolean;
}

export interface CleanGeneratedOptions {
  screenshots?: boolean;
  sprites?: boolean;
  transcodes?: boolean;
  segments?: boolean;
  imageThumbnails?: boolean;
  dryRun?: boolean;
}

export interface ExportOptions {
  includeVideos?: boolean;
  includePerformers?: boolean;
  includeStudios?: boolean;
  includeTags?: boolean;
  includeGalleries?: boolean;
  includeGroups?: boolean;
}

export const metadata = {
  scan: (opts?: ScanOptions) =>
    request<{ jobId: string }>("/metadata/scan", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  generate: (opts?: GenerateOptions) =>
    request<{ jobId: string }>("/metadata/generate", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  libraryFolders: (path?: string, probeChildren = true) => {
    const params = new URLSearchParams();
    if (path) params.set("path", path);
    if (!probeChildren) params.set("probeChildren", "false");
    const query = params.toString();
    return request<LibraryFolder[]>(`/metadata/library-folders${query ? `?${query}` : ""}`);
  },
  filesystemPolicy: () => request<{ caseSensitive: boolean }>("/metadata/filesystem-policy"),
  clean: (opts?: CleanOptions) =>
    request<{ jobId: string }>("/metadata/clean", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  cleanGenerated: (opts?: CleanGeneratedOptions) =>
    request<{ jobId: string }>("/metadata/clean-generated", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  export: (opts?: ExportOptions) =>
    request<{ jobId: string }>("/metadata/export", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  identify: (opts?: {
    sources?: string[];
    videoIds?: number[];
    setCoverImage?: boolean;
    setTags?: boolean;
    setPerformers?: boolean;
    setStudio?: boolean;
    createTags?: boolean;
    createPerformers?: boolean;
    createStudios?: boolean;
    markOrganized?: boolean;
    skipMultipleMatches?: boolean;
    skipSingleNamePerformers?: boolean;
    fieldStrategies?: Record<string, "ignore" | "merge" | "overwrite">;
    performerGenders?: string[];
  }) =>
    request<{ jobId: string }>("/metadata/identify", { method: "POST", body: JSON.stringify(opts ?? {}) }),
  import: (opts?: { filePath: string; duplicateHandling?: boolean }) =>
    request<{ jobId: string }>("/metadata/import", { method: "POST", body: JSON.stringify(opts ?? {}) }),
};

// ===== Database =====
export const database = {
  backup: () => request<{ backupPath: string; sizeBytes: number; timestamp: string }>("/database/backup", { method: "POST", timeoutMs: null }),
  restore: (backupPath: string) =>
    request<{ message: string; backupPath: string; preRestoreBackupPath: string | null }>("/database/restore", {
      method: "POST",
      body: JSON.stringify({ backupPath }),
      timeoutMs: null,
    }),
  migrate: () => request<DatabaseMigrationResult>("/database/migrate", { method: "POST", timeoutMs: null }),
  latestBackup: async () => {
    const result = await requestOptional<{ path: string }>("/jobs/backup/latest");
    return result?.path ?? null;
  },
  optimize: () => request<void>("/database/optimize", { method: "POST", timeoutMs: null }),
  wipe: () =>
    request<{ message: string; backupPath: string; timestamp: string; configBackupPath: string | null }>(
      "/database/wipe",
      { method: "POST", timeoutMs: null },
    ),
  backupConfig: () =>
    request<{ backupPath: string; sizeBytes: number; timestamp: string }>("/database/config/backup", { method: "POST", timeoutMs: null }),
  restoreConfig: (backupPath: string) =>
    request<{ message: string; backupPath: string }>("/database/config/restore", {
      method: "POST",
      body: JSON.stringify({ backupPath }),
      timeoutMs: null,
    }),
  latestConfigBackup: async () => {
    const result = await requestOptional<{ path: string | null }>("/database/config/latest-backup");
    return result?.path ?? null;
  },
};

// ===== Stash Migration =====
export interface StashPreviewResult {
  isValid: boolean;
  error: string | null;
  videos: number;
  performers: number;
  tags: number;
  studios: number;
  groups: number;
  images: number;
  galleries: number;
  generatedContentFound: boolean;
  generatedPath: string | null;
}
export interface StashImportResult {
  videos: number;
  performers: number;
  tags: number;
  studios: number;
  groups: number;
  images: number;
  galleries: number;
}
export interface StashPathMapping {
  source: string;
  target: string;
}
export interface StashImportOptions {
  coveGeneratedPath?: string;
  migrateGeneratedContent?: boolean;
  pathMappings?: StashPathMapping[];
}
export const stashMigration = {
  preview: (stashDbPath: string) =>
    request<StashPreviewResult>("/stash-migration/preview", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ stashDbPath }),
    }),
  startImport: (stashDbPath: string, options?: StashImportOptions) =>
    request<{ jobId: string }>("/stash-migration/import", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        stashDbPath,
        generatedPath: options?.coveGeneratedPath,
        migrateGeneratedContent: options?.migrateGeneratedContent ?? true,
        pathMappings: options?.pathMappings,
      }),
    }),
  importResult: (jobId: string) => requestOptional<StashImportResult>(`/stash-migration/import/${jobId}`),
};

// ===== Logs =====
export interface LogEntry {
  timestamp: string;
  level: string;
  message: string;
  exception?: string;
  category?: string;
  jobId?: string;
  jobType?: string;
  operationId?: string;
}

export interface RuntimeLogLevelStatus {
  level: string;
  configuredLevel: string;
  traceExpiresAt?: string;
}

export const logs = {
  recent: (level?: string, limit?: number) => {
    const params = new URLSearchParams();
    if (level) params.set("level", level);
    if (limit) params.set("limit", String(limit));
    const qs = params.toString();
    return request<LogEntry[]>(`/logs${qs ? `?${qs}` : ""}`);
  },
};

// ===== Saved Filters =====
export const savedFilters = {
  list: (mode?: string) => request<SavedFilter[]>(`/savedfilters${mode ? `?mode=${mode}` : ""}`),
  get: (id: number) => request<SavedFilter>(`/savedfilters/${id}`),
  create: (data: SavedFilterCreate) => request<SavedFilter>("/savedfilters", { method: "POST", body: JSON.stringify(data) }),
  update: (id: number, data: SavedFilterUpdate) => request<SavedFilter>(`/savedfilters/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  delete: (id: number) => request<void>(`/savedfilters/${id}`, { method: "DELETE" }),
};

// ===== Personal Dashboards =====
export const dashboards = {
  bootstrap: (widgets?: DashboardWidget[]) =>
    request<Dashboard>("/dashboards/bootstrap", { method: "POST", body: JSON.stringify({ widgets }) }),
  list: () => request<DashboardSummary[]>("/dashboards"),
  get: (id: number) => request<Dashboard>(`/dashboards/${id}`),
  create: (name: string) => request<Dashboard>("/dashboards", { method: "POST", body: JSON.stringify({ name }) }),
  update: (id: number, data: { name: string; expectedVersion: number; widgets: DashboardWidget[] }) =>
    request<Dashboard>(`/dashboards/${id}`, { method: "PUT", body: JSON.stringify(data) }),
  duplicate: (id: number, name: string) =>
    request<Dashboard>(`/dashboards/${id}/duplicate`, { method: "POST", body: JSON.stringify({ name }) }),
  setDefault: (id: number) => request<Dashboard>(`/dashboards/${id}/default`, { method: "PUT" }),
  delete: (id: number) => request<void>(`/dashboards/${id}`, { method: "DELETE" }),
};

// ===== Plugins =====
export const plugins = {
  list: () => request<Plugin[]>("/plugins"),
  getTasks: () => request<PluginTask[]>("/plugins/tasks"),
  runTask: (data: RunPluginTaskRequest) => request<{ jobId: string }>("/plugins/run-task", { method: "POST", body: JSON.stringify(data) }),
  saveSettings: (data: PluginSettings) => request<void>("/plugins/settings", { method: "POST", body: JSON.stringify(data) }),
  reload: () => request<{ message: string }>("/plugins/reload", { method: "POST" }),
  getConfig: (pluginId: string) => request<Record<string, unknown>>(`/plugins/${encodeURIComponent(pluginId)}/config`),
  setConfig: (pluginId: string, values: Record<string, unknown>) =>
    request<void>(`/plugins/${encodeURIComponent(pluginId)}/config`, { method: "POST", body: JSON.stringify(values) }),
};

// ===== Extensions =====
export const extensions = {
  getManifest: () => request<ExtensionManifest>("/extensions/manifest"),
  invokeAction: <T = unknown>(apiEndpoint: string, payload: unknown) =>
    request<T>(normalizeApiPath(apiEndpoint), {
      method: "POST",
      body: JSON.stringify(payload),
    }),
  list: (category?: string) =>
    request<ExtensionInfo[]>(category ? `/extensions?category=${encodeURIComponent(category)}` : "/extensions"),
  enable: (id: string) => request<void>(`/extensions/${encodeURIComponent(id)}/enable`, { method: "POST" }),
  disable: (id: string) => request<void>(`/extensions/${encodeURIComponent(id)}/disable`, { method: "POST" }),
  getData: (id: string) => request<Record<string, string>>(`/extensions/${encodeURIComponent(id)}/data`),
  setData: (id: string, key: string, value: string) =>
    request<void>(`/extensions/${encodeURIComponent(id)}/data/${encodeURIComponent(key)}`, {
      method: "PUT",
      body: JSON.stringify(value),
    }),
  runJob: (id: string, jobId: string, parameters?: Record<string, string>) =>
    request<{ message: string }>(`/extensions/${encodeURIComponent(id)}/jobs/${encodeURIComponent(jobId)}/run`, {
      method: "POST",
      body: JSON.stringify(parameters ?? null),
    }),
  assetUrl: (extensionId: string, path: string) => `${API_BASE}/extensions/assets/${encodeURIComponent(extensionId)}/${path}`,
  /** Get all available extension categories. */
  getCategories: () => request<string[]>("/extensions/categories"),
  /** Validate all extension dependencies. */
  validateDependencies: () => request<DependencyProblem[]>("/extensions/dependencies/validate"),
  /** Get missing dependencies for a specific extension. */
  getMissingDependencies: (id: string) =>
    request<string[]>(`/extensions/${encodeURIComponent(id)}/dependencies/missing`),
  /** Registry: search for extensions. */
  registrySearch: (params: { q?: string; category?: string; type?: string; sort?: string; page?: number; pageSize?: number }) => {
    const qs = new URLSearchParams();
    if (params.q) qs.set("q", params.q);
    if (params.category) qs.set("category", params.category);
    if (params.type) qs.set("type", params.type);
    if (params.sort) qs.set("sort", params.sort);
    if (params.page) qs.set("page", String(params.page));
    if (params.pageSize) qs.set("pageSize", String(params.pageSize));
    return request<RegistrySearchResult>(`/extensions/registry/search?${qs.toString()}`);
  },
  /** Registry: get extension detail. */
  registryGetExtension: (extensionId: string) =>
    request<RegistryExtensionDetail>(`/extensions/registry/${encodeURIComponent(extensionId)}`),
  /** Registry: check for updates. */
  registryCheckUpdates: () => request<RegistryUpdateInfo[]>("/extensions/registry/updates"),
  /** Registry: get categories. */
  registryGetCategories: () => request<string[]>("/extensions/registry/categories"),
  /** Registry: install an extension. */
  registryInstall: (extensionId: string, version: string, installDependencies = false) =>
    request<RegistryInstallResult>("/extensions/registry/install", {
      method: "POST",
      body: JSON.stringify({ extensionId, version, installDependencies }),
      timeoutMs: null,
    }),
  /** Install an extension package from a user-provided URL. */
  installFromUrl: (url: string, trustUnverified = false) =>
    request<{ message: string; extensionId: string; version: string; path: string }>("/extensions/install-from-url", {
      method: "POST",
      body: JSON.stringify({ url, trustUnverified }),
      timeoutMs: null,
    }),
  /** Install an extension package from an uploaded ZIP. */
  installFromZip: (file: File, trustUnverified = false) => {
    const body = new FormData();
    body.append("file", file);
    body.append("trustUnverified", String(trustUnverified));
    return request<{ message: string; extensionId: string; version: string; path: string }>("/extensions/install-from-zip", {
      method: "POST",
      body,
      timeoutMs: UPLOAD_REQUEST_TIMEOUT_MS,
    });
  },
  /** Registry: resolve dependencies for an extension. */
  registryResolveDependencies: (extensionId: string) =>
    request<DependencyInfo[]>(`/extensions/registry/${extensionId}/dependencies`),
  /** Registry: uninstall an extension. */
  registryUninstall: (extensionId: string, uninstallDependents = false) =>
    request<RegistryUninstallResult>("/extensions/registry/uninstall", {
      method: "POST",
      body: JSON.stringify({ extensionId, uninstallDependents }),
      timeoutMs: null,
    }),
};

// ===== Auth / RBAC =====
export interface UserRow {
  id: number;
  username: string;
  displayName?: string | null;
  email?: string | null;
  isActive: boolean;
  isLocked: boolean;
  isSystem: boolean;
  mustChangePassword: boolean;
  hasPassword: boolean;
  lastLoginAt?: string | null;
  lastLoginIp?: string | null;
  createdAt: string;
  roles: string[];
}
export interface RoleRow {
  id: number;
  name: string;
  description?: string | null;
  isBuiltin: boolean;
  isSystem: boolean;
  source: string;
  permissions: string[];
}
export interface PermissionInfo {
  key: string;
  category: string;
  description: string;
  source: string;
  dangerous: boolean;
  implies: string[];
}
export interface AuditEventRow {
  id: number;
  occurredAt: string;
  actorUserId?: number | null;
  actorUsername?: string | null;
  actorKind: string;
  ip?: string | null;
  action: string;
  targetKind?: string | null;
  targetId?: string | null;
  outcome: string;
  detail?: string | null;
}
export interface ContentRuleRow {
  id: number;
  roleId: number;
  roleName: string;
  entityKind: string;
  effect: "allow" | "deny";
  scopeKind: "all" | "tag" | "studio" | "attribute" | "expression";
  scopeValue: string;
  appliesTo: "read" | "write" | "delete" | "all";
  createdAt: string;
  updatedAt: string;
}
export interface EntityOverrideRow {
  id: number;
  roleId: number;
  roleName: string;
  entityKind: string;
  entityId: string;
  effect: "allow" | "deny";
  appliesTo: "read" | "write" | "delete" | "all";
  createdAt: string;
}
export interface ApiTokenRow {
  id: string;
  name: string;
  prefix: string;
  scope: string[] | null;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
}
export interface ApiTokenIssuedRow extends ApiTokenRow {
  plaintextToken: string;
}
export interface ShareLinkRow {
  id: string;
  createdByUserId: number | null;
  createdByUsername: string | null;
  entityKind: string;
  entityIds: string[];
  createdAt: string;
  expiresAt: string | null;
  viewCount: number;
  hasPassword: boolean;
  revoked: boolean;
}
export interface ShareLinkIssuedRow {
  id: string;
  plaintextToken: string;
  entityKind: string;
  entityIds: string[];
  createdAt: string;
  expiresAt: string | null;
  hasPassword: boolean;
}

export interface BootstrapStatusRow {
  ownerExists: boolean;
  authEnabled: boolean;
  hasSetupToken: boolean;
}

export interface AuthLoginResponse {
  token: string;
  refreshToken: string;
  accessExpires: string;
  refreshExpires: string;
  user?: UserRow;
  username: string;
}

export interface ExternalLoginMethodRow {
  id: string;
  label: string;
  startUrl: string;
  order: number;
  extensionId: string;
  linkStartUrl?: string | null;
  showOnLoginPage?: boolean;
}

export interface ExternalIdentityLinkRow {
  id: number;
  userId: number;
  extensionId: string;
  providerId: string;
  providerLabel: string;
  accountLabel?: string | null;
  createdAt: string;
  lastUsedAt?: string | null;
}

export interface PendingExternalIdentityLinkRow {
  providerLabel: string;
  accountLabel?: string | null;
}

export interface ExternalLinkStartRow {
  redirectUrl?: string | null;
  confirmationCode?: string | null;
}

export interface InviteTokenRow {
  token: string;
  url: string;
  expiresAt: string;
}

export interface InviteTokenInfoRow {
  valid: boolean;
  usernameRequired: boolean;
  username?: string | null;
  expiresAt: string;
}

export const auth = {
  me: () => request<MeResponse>("/auth/me"),
  bootstrapStatus: () => request<BootstrapStatusRow>("/auth/bootstrap-status"),
  externalProviders: () => request<ExternalLoginMethodRow[]>("/auth/external/providers"),
  externalLinks: () => request<ExternalIdentityLinkRow[]>("/auth/external/links"),
  startExternalLink: (path: string) => request<ExternalLinkStartRow>(normalizeApiPath(path), { method: "POST" }),
  previewExternalLink: (code: string) => request<PendingExternalIdentityLinkRow>("/auth/external/links/preview", {
    method: "POST",
    body: JSON.stringify({ code }),
  }),
  confirmExternalLink: (code: string) => request<ExternalIdentityLinkRow>("/auth/external/links/confirm", {
    method: "POST",
    body: JSON.stringify({ code }),
  }),
  cancelExternalLink: (code: string) => request<void>("/auth/external/links/cancel", {
    method: "POST",
    body: JSON.stringify({ code }),
  }),
  removeExternalLink: (linkId: number) => request<void>(`/auth/external/links/${linkId}`, { method: "DELETE" }),
  bootstrapOwner: (username: string, password: string) =>
    request<AuthLoginResponse>("/auth/bootstrap-owner", {
      method: "POST",
      body: JSON.stringify({ username, password }),
    }),
  redeemSetupToken: (token: string, password: string, username?: string) =>
    request<AuthLoginResponse>("/auth/setup-token-redeem", {
      method: "POST",
      body: JSON.stringify({ token, password, username: username || undefined }),
    }),
  inviteInfo: (token: string) => request<InviteTokenInfoRow>(`/auth/invite-info?token=${encodeURIComponent(token)}`),
  redeemInvite: (token: string, password: string, username?: string) =>
    request<AuthLoginResponse>("/auth/invite-redeem", {
      method: "POST",
      body: JSON.stringify({ token, password, username: username || undefined }),
    }),
  login: (username: string, password: string) =>
    request<{ token: string; refreshToken: string; user: unknown; username: string }>("/auth/login", {
      method: "POST",
      body: JSON.stringify({ username, password }),
    }),
  logout: (refreshToken: string) =>
    request<{ message: string }>("/auth/logout", { method: "POST", body: JSON.stringify({ refreshToken }) }),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<{ message: string }>("/auth/change-password", {
      method: "POST",
      body: JSON.stringify({ currentPassword, newPassword }),
    }),
  revokeSessions: () => request<{ message: string }>("/auth/revoke-sessions", { method: "POST" }),
  updateUiPreferences: (preferences: UserUiPreferences | null) =>
    request<UserUiPreferences | null>("/auth/me/ui-preferences", {
      method: "PUT",
      body: JSON.stringify(preferences ?? {}),
    }),
};

export const usersApi = {
  list: () => request<UserRow[]>("/users"),
  get: (id: number) => request<UserRow>(`/users/${id}`),
  create: (req: { username: string; password: string; displayName?: string; email?: string; roles?: string[]; mustChangePassword?: boolean }) =>
    request<UserRow>("/users", { method: "POST", body: JSON.stringify(req) }),
  createInvite: (req: { username?: string; displayName?: string; email?: string; roles?: string[] }) =>
    request<InviteTokenRow>("/users/invite", { method: "POST", body: JSON.stringify(req) }),
  update: (id: number, req: { displayName?: string; email?: string; isActive?: boolean; mustChangePassword?: boolean }) =>
    request<UserRow>(`/users/${id}`, { method: "PUT", body: JSON.stringify(req) }),
  remove: (id: number) => request<void>(`/users/${id}`, { method: "DELETE" }),
  setRoles: (id: number, roles: string[]) =>
    request<UserRow>(`/users/${id}/roles`, { method: "POST", body: JSON.stringify({ roles }) }),
  adminChangePassword: (id: number, newPassword: string) =>
    request<void>(`/users/${id}/password`, { method: "POST", body: JSON.stringify({ newPassword }) }),
  invite: (id: number) => request<InviteTokenRow>(`/users/${id}/invite`, { method: "POST" }),
  unlock: (id: number) => request<void>(`/users/${id}/unlock`, { method: "POST" }),
  externalLinks: (id: number) => request<ExternalIdentityLinkRow[]>(`/users/${id}/external-links`),
  removeExternalLink: (id: number, linkId: number) =>
    request<void>(`/users/${id}/external-links/${linkId}`, { method: "DELETE" }),
};

export const rolesApi = {
  list: () => request<RoleRow[]>("/roles"),
  get: (id: number) => request<RoleRow>(`/roles/${id}`),
  permissions: () => request<PermissionInfo[]>("/roles/permissions"),
  create: (req: { name: string; description?: string; permissions?: string[] }) =>
    request<RoleRow>("/roles", { method: "POST", body: JSON.stringify(req) }),
  update: (id: number, req: { description?: string; permissions?: string[] }) =>
    request<RoleRow>(`/roles/${id}`, { method: "PUT", body: JSON.stringify(req) }),
  remove: (id: number) => request<void>(`/roles/${id}`, { method: "DELETE" }),
};

export const auditApi = {
  list: (opts?: { action?: string; actor?: string; outcome?: string; page?: number; perPage?: number }) => {
    const params = new URLSearchParams();
    if (opts?.action) params.set("action", opts.action);
    if (opts?.actor) params.set("actor", opts.actor);
    if (opts?.outcome) params.set("outcome", opts.outcome);
    if (opts?.page) params.set("page", String(opts.page));
    if (opts?.perPage) params.set("perPage", String(opts.perPage));
    const qs = params.toString();
    return request<{ items: AuditEventRow[]; totalCount: number; page: number; perPage: number }>(
      `/audit${qs ? "?" + qs : ""}`
    );
  },
};

export const contentRulesApi = {
  list: (roleId?: number) => {
    const qs = roleId ? `?roleId=${roleId}` : "";
    return request<ContentRuleRow[]>(`/content-rules${qs}`);
  },
  create: (req: { roleId: number; entityKind: string; effect: string; scopeKind: string; scopeValue: string; appliesTo: string }) =>
    request<ContentRuleRow>("/content-rules", { method: "POST", body: JSON.stringify(req) }),
  update: (id: number, req: Partial<Pick<ContentRuleRow, "effect" | "scopeKind" | "scopeValue" | "appliesTo">>) =>
    request<ContentRuleRow>(`/content-rules/${id}`, { method: "PUT", body: JSON.stringify(req) }),
  remove: (id: number) => request<void>(`/content-rules/${id}`, { method: "DELETE" }),
  listOverrides: (roleId?: number, entityKind?: string) => {
    const params = new URLSearchParams();
    if (roleId) params.set("roleId", String(roleId));
    if (entityKind) params.set("entityKind", entityKind);
    const qs = params.toString();
    return request<EntityOverrideRow[]>(`/content-rules/overrides${qs ? `?${qs}` : ""}`);
  },
  createOverride: (req: { roleId: number; entityKind: string; entityId: string; effect: string; appliesTo: string }) =>
    request<EntityOverrideRow>("/content-rules/overrides", { method: "POST", body: JSON.stringify(req) }),
  removeOverride: (id: number) => request<void>(`/content-rules/overrides/${id}`, { method: "DELETE" }),
};

export const apiTokensApi = {
  list: () => request<ApiTokenRow[]>("/apitokens"),
  create: (req: { name: string; scope?: string[]; expiresAt?: string }) =>
    request<ApiTokenIssuedRow>("/apitokens", { method: "POST", body: JSON.stringify(req) }),
  revoke: (id: string) => request<void>(`/apitokens/${id}`, { method: "DELETE" }),
};

export const shareLinksApi = {
  list: () => request<ShareLinkRow[]>("/share-links"),
  create: (req: { entityKind: string; entityIds: string[]; expiresAt?: string; password?: string }) =>
    request<ShareLinkIssuedRow>("/share-links", { method: "POST", body: JSON.stringify(req) }),
  revoke: (id: string) => request<void>(`/share-links/${id}`, { method: "DELETE" }),
};
