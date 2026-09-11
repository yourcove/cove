import type {
  DuplicateGroupFilter,
  DuplicateGroupSort,
  DuplicateKeeperRule,
  DuplicateKeeperRuleType,
  DuplicateMatchType,
  DuplicateResolutionAction,
  EntityEngagement,
  Video,
  VideoFile,
} from "../../api/types";
import { formatDuration, formatFileSize, getResolutionLabel } from "../shared";

export const MATCH_METHODS: Array<{
  value: DuplicateMatchType;
  label: string;
  description: string;
  detail: string;
}> = [
  {
    value: "phash",
    label: "Looks the same",
    description: "Visual fingerprints",
    detail: "Finds re-encodes, different resolutions and trimmed copies by comparing perceptual hashes.",
  },
  {
    value: "fingerprint",
    label: "Identical files",
    description: "MD5 / OSHash",
    detail: "Finds byte-for-byte copies of the same file. Fast and never wrong.",
  },
  {
    value: "title",
    label: "Same title",
    description: "Normalized titles",
    detail: "Groups videos whose titles match, ignoring case and extra spaces.",
  },
  {
    value: "remoteId",
    label: "Same scene ID",
    description: "Scraper / StashBox IDs",
    detail: "Groups videos linked to the same remote scene. Great for spotting mis-tagged videos.",
  },
];

export const ACCURACY_PRESETS: Array<{ label: string; distance: number; hint: string }> = [
  { label: "Exact", distance: 0, hint: "Identical frames only" },
  { label: "High", distance: 4, hint: "Re-encodes and resolution changes" },
  { label: "Medium", distance: 8, hint: "Adds watermarks and color shifts" },
  { label: "Low", distance: 12, hint: "Loose matches — expect false positives" },
];

export function accuracyLabel(distance: number) {
  return ACCURACY_PRESETS.find((preset) => preset.distance === distance)?.label ?? `Distance ${distance}`;
}

export function describeSearch(search: { matchType: DuplicateMatchType; distance: number; durationDiff?: number }) {
  const method = MATCH_METHODS.find((item) => item.value === search.matchType)?.label ?? search.matchType;
  if (search.matchType !== "phash") return method;
  return `${method} · ${accuracyLabel(search.distance)} accuracy`;
}

export interface KeeperRuleDefinition {
  type: DuplicateKeeperRuleType;
  label: string;
  short: string;
  description: string;
  requiresValues?: "codec" | "path";
  requiresFilesRead?: boolean;
}

export const KEEPER_RULES: KeeperRuleDefinition[] = [
  { type: "resolution", label: "Highest resolution", short: "resolution", description: "More pixels wins." },
  {
    type: "duration",
    label: "Longest duration",
    short: "duration",
    description: "Keeps uncut copies over trimmed ones.",
  },
  {
    type: "bitrate",
    label: "Highest bitrate",
    short: "bitrate",
    description: "More data per second, usually better quality.",
  },
  { type: "framerate", label: "Highest frame rate", short: "frame rate", description: "60 fps beats 30 fps." },
  {
    type: "codec",
    label: "Preferred codec",
    short: "codec",
    description: "Earlier codecs in your list win.",
    requiresValues: "codec",
  },
  { type: "size-largest", label: "Largest file", short: "file size", description: "Bigger total file size wins." },
  { type: "size-smallest", label: "Smallest file", short: "smaller file", description: "Saves the most space." },
  {
    type: "metadata",
    label: "Most metadata",
    short: "metadata",
    description: "Counts title, date, studio, tags, performers, links and more.",
  },
  {
    type: "engagement",
    label: "Most watched",
    short: "watch history",
    description: "Plays, favorites, O-counts and ratings.",
  },
  { type: "organized", label: "Organized first", short: "organized", description: "Videos marked organized win." },
  {
    type: "path",
    label: "Preferred folder",
    short: "folder",
    description: "Files whose path contains an earlier entry win.",
    requiresValues: "path",
    requiresFilesRead: true,
  },
  { type: "date-oldest", label: "Added first", short: "added first", description: "The copy Cove found first wins." },
  { type: "date-newest", label: "Added most recently", short: "added last", description: "The newest copy wins." },
];

export const DEFAULT_KEEPER_RULES: DuplicateKeeperRule[] = [
  { type: "resolution" },
  { type: "duration" },
  { type: "bitrate" },
  { type: "metadata" },
  { type: "engagement" },
  { type: "date-oldest" },
];

export const CODEC_CHOICES = ["av1", "hevc", "vp9", "h264", "mpeg4", "wmv3", "mpeg2video"];

export function ruleDefinition(type: string | null | undefined) {
  return KEEPER_RULES.find((rule) => rule.type === type);
}

export function describeDecision(rule: string | null | undefined, source: string | null | undefined) {
  if (source === "manual") return "Chosen by you";
  if (!rule) return null;
  if (rule === "tiebreak") return "Auto: all rules tied";
  const definition = ruleDefinition(rule);
  return definition ? `Auto: ${definition.label.toLowerCase()}` : null;
}

export const GROUP_FILTERS: Array<{ value: DuplicateGroupFilter; label: string }> = [
  { value: "unresolved", label: "To review" },
  { value: "queued", label: "In progress" },
  { value: "resolved", label: "Resolved" },
  { value: "ignored", label: "Not duplicates" },
];

export const GROUP_SORTS: Array<{ value: DuplicateGroupSort; label: string }> = [
  { value: "position", label: "Discovery order" },
  { value: "reclaimable", label: "Most space to free" },
  { value: "largest", label: "Largest files" },
  { value: "members", label: "Most copies" },
  { value: "recent", label: "Recently resolved" },
];

export const PAGE_SIZES = [5, 10, 20, 50];

export function primaryFile(video: Video): VideoFile | undefined {
  return video.files.find((file) => file.id === video.primaryFileId) ?? video.files[0];
}

export function totalSize(video: Video) {
  return video.files.reduce((sum, file) => sum + (file.size || 0), 0);
}

export function displayTitle(video: Video) {
  return video.title || primaryFile(video)?.basename || `Video #${video.id}`;
}

export function formatBitrate(bitsPerSecond: number) {
  if (!bitsPerSecond) return "—";
  if (bitsPerSecond >= 1_000_000) return `${(bitsPerSecond / 1_000_000).toFixed(1)} Mbps`;
  return `${Math.round(bitsPerSecond / 1000)} kbps`;
}

export function formatCodec(codec: string | undefined) {
  if (!codec) return "—";
  const normalized = codec.toLowerCase();
  const labels: Record<string, string> = {
    h264: "H.264",
    avc1: "H.264",
    hevc: "HEVC",
    h265: "HEVC",
    av1: "AV1",
    vp9: "VP9",
    mpeg4: "MPEG-4",
    wmv3: "WMV",
    mpeg2video: "MPEG-2",
  };
  return labels[normalized] ?? codec.toUpperCase();
}

export function phashOf(video: Video) {
  for (const file of video.files) {
    const value = file.fingerprints.find((fingerprint) => fingerprint.type === "phash")?.value;
    if (value) return value;
  }
  return undefined;
}

/** Hamming distance between two hex pHash strings, or null when either is missing or malformed. */
export function phashDistance(left: string | undefined, right: string | undefined) {
  if (!left || !right) return null;
  try {
    let value = BigInt(`0x${left.replace(/^0x/i, "")}`) ^ BigInt(`0x${right.replace(/^0x/i, "")}`);
    let count = 0;
    while (value > 0n) {
      count += Number(value & 1n);
      value >>= 1n;
    }
    return count;
  } catch {
    return null;
  }
}

/** Summarizes how alike the members of a group are, for the group header. */
export function describeSimilarity(videos: Video[]) {
  const durations = videos.map((video) => primaryFile(video)?.duration ?? 0).filter((duration) => duration > 0);
  const spread = durations.length > 1 ? Math.max(...durations) - Math.min(...durations) : 0;
  const hashes = videos.map(phashOf);
  let maxDistance: number | null = null;
  for (let left = 0; left < hashes.length; left++) {
    for (let right = left + 1; right < hashes.length; right++) {
      const distance = phashDistance(hashes[left], hashes[right]);
      if (distance != null) maxDistance = Math.max(maxDistance ?? 0, distance);
    }
  }
  const identicalFiles = sharesFingerprint(videos, "md5") || sharesFingerprint(videos, "oshash");
  return { durationSpread: spread, maxPhashDistance: maxDistance, identicalFiles };
}

function sharesFingerprint(videos: Video[], type: string) {
  const values = videos.map(
    (video) => video.files.flatMap((file) => file.fingerprints).find((fingerprint) => fingerprint.type === type)?.value,
  );
  return values.length > 1 && values.every((value) => value && value === values[0]);
}

export function metadataScore(video: Video) {
  return (
    (video.title ? 1 : 0) +
    (video.details ? 1 : 0) +
    (video.date ? 1 : 0) +
    (video.studioId ? 1 : 0) +
    (video.code ? 1 : 0) +
    (video.director ? 1 : 0) +
    (video.imagePath ? 1 : 0) +
    video.tags.length +
    video.performers.length +
    video.galleries.length +
    video.urls.length +
    video.remoteIds.length +
    video.groups.length
  );
}

export type ComparisonTone = "best" | "worse" | "same" | "neutral";

export interface ComparisonRow {
  key: string;
  label: string;
  section: "file" | "library";
  /** Present value per video, formatted for display. */
  render: (video: Video, engagement?: EntityEngagement) => string;
  /** Numeric score where bigger is better; omit for rows that only highlight differences. */
  score?: (video: Video, engagement?: EntityEngagement) => number | null;
  /** Title attribute with the unabridged value. */
  title?: (video: Video) => string | undefined;
}

export const COMPARISON_ROWS: ComparisonRow[] = [
  {
    key: "resolution",
    label: "Resolution",
    section: "file",
    render: (video) => {
      const file = primaryFile(video);
      if (!file?.width) return "—";
      const label = getResolutionLabel(file.width, file.height);
      return label ? `${label} · ${file.width}×${file.height}` : `${file.width}×${file.height}`;
    },
    score: (video) => {
      const file = primaryFile(video);
      return file ? file.width * file.height : null;
    },
  },
  {
    key: "duration",
    label: "Duration",
    section: "file",
    render: (video) => {
      const duration = primaryFile(video)?.duration ?? 0;
      return duration > 0 ? formatDuration(duration) : "—";
    },
    score: (video) => Math.round(primaryFile(video)?.duration ?? 0) || null,
  },
  {
    key: "size",
    label: "File size",
    section: "file",
    render: (video) => {
      const size = totalSize(video);
      return size > 0 ? formatFileSize(size) : "—";
    },
    score: (video) => totalSize(video) || null,
  },
  {
    key: "bitrate",
    label: "Bitrate",
    section: "file",
    render: (video) => formatBitrate(primaryFile(video)?.bitRate ?? 0),
    score: (video) => primaryFile(video)?.bitRate || null,
  },
  {
    key: "codec",
    label: "Codec",
    section: "file",
    render: (video) => {
      const file = primaryFile(video);
      if (!file) return "—";
      return [formatCodec(file.videoCodec), file.audioCodec ? file.audioCodec.toUpperCase() : null]
        .filter(Boolean)
        .join(" · ");
    },
  },
  {
    key: "framerate",
    label: "Frame rate",
    section: "file",
    render: (video) => {
      const rate = primaryFile(video)?.frameRate ?? 0;
      return rate > 0 ? `${Math.round(rate * 100) / 100} fps` : "—";
    },
    score: (video) => Math.round((primaryFile(video)?.frameRate ?? 0) * 100) / 100 || null,
  },
  {
    key: "format",
    label: "Container",
    section: "file",
    render: (video) => primaryFile(video)?.format?.toUpperCase() || "—",
  },
  {
    key: "metadata",
    label: "Metadata",
    section: "library",
    render: (video) => {
      const parts = [
        video.studioName,
        video.performers.length
          ? `${video.performers.length} performer${video.performers.length === 1 ? "" : "s"}`
          : null,
        video.tags.length ? `${video.tags.length} tag${video.tags.length === 1 ? "" : "s"}` : null,
      ].filter(Boolean);
      return parts.length ? parts.join(" · ") : "None";
    },
    score: (video) => metadataScore(video),
    title: (video) =>
      [
        video.title && `Title: ${video.title}`,
        video.date && `Date: ${video.date}`,
        video.studioName && `Studio: ${video.studioName}`,
        video.performers.length && `Performers: ${video.performers.map((performer) => performer.name).join(", ")}`,
        video.tags.length && `Tags: ${video.tags.map((tag) => tag.name).join(", ")}`,
        video.remoteIds.length && `Remote IDs: ${video.remoteIds.length}`,
      ]
        .filter(Boolean)
        .join("\n") || undefined,
  },
  {
    key: "engagement",
    label: "Watched",
    section: "library",
    render: (_video, engagement) => {
      if (!engagement) return "—";
      const parts = [
        engagement.playCount ? `${engagement.playCount} play${engagement.playCount === 1 ? "" : "s"}` : null,
        engagement.likeCount ? `${engagement.likeCount} O` : null,
        engagement.rating ? `★ ${engagement.rating}` : null,
        engagement.isFavorite ? "♥" : null,
      ].filter(Boolean);
      return parts.length ? parts.join(" · ") : "Never";
    },
    score: (_video, engagement) =>
      engagement
        ? engagement.playCount + engagement.likeCount + (engagement.isFavorite ? 1 : 0) + (engagement.rating ? 1 : 0)
        : null,
  },
  {
    key: "added",
    label: "Added",
    section: "library",
    render: (video) => new Date(video.createdAt).toLocaleDateString(),
  },
];

/** Tones for one comparison row: the best value(s) when members differ, and "same" when they all match. */
export function rowTones(row: ComparisonRow, videos: Video[], engagement: Map<number, EntityEngagement>) {
  const rendered = videos.map((video) => row.render(video, engagement.get(video.id)));
  const allSame = rendered.every((value) => value === rendered[0]);
  if (allSame) return { tones: videos.map(() => "same" as ComparisonTone), allSame };
  if (!row.score) return { tones: videos.map(() => "neutral" as ComparisonTone), allSame };
  const scores = videos.map((video) => row.score!(video, engagement.get(video.id)));
  const known = scores.filter((score): score is number => score != null);
  if (known.length < 2) return { tones: videos.map(() => "neutral" as ComparisonTone), allSame };
  const best = Math.max(...known);
  return {
    tones: scores.map((score) => (score == null ? "neutral" : score === best ? "best" : "worse")) as ComparisonTone[],
    allSame,
  };
}

export function folderOf(path: string | undefined) {
  if (!path) return "";
  const normalized = path.replace(/\\/g, "/");
  const index = normalized.lastIndexOf("/");
  return index > 0 ? normalized.slice(0, index) : "";
}

export interface ResolutionPreferences {
  action: DuplicateResolutionAction;
  deleteFiles: boolean;
  deleteGenerated: boolean;
  confirmEachGroup: boolean;
}

export interface SearchPreferences {
  matchType: DuplicateMatchType;
  distance: number;
  durationDiff: number;
  minimumDuration: number;
  includePaths: string[];
  excludePaths: string[];
  keeperRules: DuplicateKeeperRule[];
  pageSize: number;
  showIdenticalRows: boolean;
}

export const DEFAULT_SEARCH_PREFERENCES: SearchPreferences = {
  matchType: "phash",
  distance: 4,
  durationDiff: 5,
  minimumDuration: 0,
  includePaths: [],
  excludePaths: [],
  keeperRules: DEFAULT_KEEPER_RULES,
  pageSize: 10,
  showIdenticalRows: false,
};

export const DEFAULT_RESOLUTION_PREFERENCES: ResolutionPreferences = {
  action: "merge",
  deleteFiles: false,
  deleteGenerated: true,
  confirmEachGroup: true,
};

const SEARCH_PREFERENCES_KEY = "cove.duplicates.search.v2";
const RESOLUTION_PREFERENCES_KEY = "cove.duplicates.resolution.v2";

function readStored<T extends object>(key: string, defaults: T): T {
  try {
    const raw = window.localStorage.getItem(key);
    if (!raw) return defaults;
    const parsed = JSON.parse(raw) as Partial<T>;
    return { ...defaults, ...parsed };
  } catch {
    return defaults;
  }
}

function writeStored(key: string, value: unknown) {
  try {
    window.localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage can be unavailable (private windows); preferences then last for the session only.
  }
}

export const readSearchPreferences = () => readStored(SEARCH_PREFERENCES_KEY, DEFAULT_SEARCH_PREFERENCES);
export const writeSearchPreferences = (value: SearchPreferences) => writeStored(SEARCH_PREFERENCES_KEY, value);
export const readResolutionPreferences = () => readStored(RESOLUTION_PREFERENCES_KEY, DEFAULT_RESOLUTION_PREFERENCES);
export const writeResolutionPreferences = (value: ResolutionPreferences) =>
  writeStored(RESOLUTION_PREFERENCES_KEY, value);
