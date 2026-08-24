export interface CoveRecord {
  [key: string]: unknown;
}

export interface PerformerSummary extends CoveRecord {
  id: number;
  name: string;
  disambiguation?: string | null;
}

export interface TagReference extends CoveRecord {
  id: number;
  name: string;
  color?: string | null;
  tagGroupId?: number | null;
  tagGroupName?: string | null;
  tagGroupColor?: string | null;
}

export interface RemoteId extends CoveRecord {
  endpoint: string;
  remoteId: string;
}

export interface MetadataServerSummary {
  endpoint: string;
  name?: string;
}

export interface Performer extends PerformerSummary {
  aliases: string[];
  tags?: TagReference[];
  urls?: string[];
  details?: string | null;
  gender?: string | null;
  birthdate?: string | null;
  deathDate?: string | null;
  ethnicity?: string | null;
  country?: string | null;
  eyeColor?: string | null;
  hairColor?: string | null;
  heightCm?: number | null;
  weight?: number | null;
  measurements?: string | null;
  fakeTits?: string | null;
  penisLength?: number | null;
  circumcised?: string | null;
  careerStart?: string | null;
  careerEnd?: string | null;
  tattoos?: string | null;
  piercings?: string | null;
  favorite?: boolean;
  videoCount?: number;
  imageCount?: number;
  galleryCount?: number;
  groupCount?: number;
  audioCount?: number;
  textCount?: number;
  faceCount?: number;
  likeCount?: number;
  remoteIds?: RemoteId[];
  customFields?: Record<string, unknown> | null;
  createdAt?: string;
  updatedAt?: string;
}

export interface Tag extends TagReference {
  aliases: string[];
  videoCount?: number;
  imageCount?: number;
  galleryCount?: number;
  performerCount?: number;
  studioCount?: number;
}

export interface Video extends CoveRecord {
  id: number;
  title?: string | null;
  date?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  code?: string | null;
  details?: string | null;
  director?: string | null;
  captions?: string | null;
  organized?: boolean;
  isVr?: boolean;
  urls?: string[];
  tags?: TagReference[];
  groups?: Array<CoveRecord>;
  galleries?: Array<CoveRecord>;
  remoteIds?: RemoteId[];
  parentVideoId?: number | null;
  parentVideoTitle?: string | null;
  clipStartSec?: number | null;
  clipEndSec?: number | null;
  childVideoCount?: number;
  customFields?: Record<string, unknown> | null;
  createdAt?: string;
  updatedAt?: string;
  performers: PerformerSummary[];
  files: Array<CoveRecord & {
    basename?: string;
    duration?: number;
    width?: number;
    height?: number;
    path?: string;
    format?: string;
    videoCodec?: string;
    audioCodec?: string;
    frameRate?: number;
    bitRate?: number;
    size?: number;
    fingerprints?: Array<CoveRecord>;
    captions?: Array<CoveRecord> | null;
  }>;
}

export interface Audio extends CoveRecord {
  id: number;
  title?: string | null;
  date?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  code?: string | null;
  details?: string | null;
  organized?: boolean;
  urls?: string[];
  tags?: TagReference[];
  groups?: Array<CoveRecord>;
  fileCount?: number;
  maxDuration?: number;
  hasVideoFiles?: boolean;
  customFields?: Record<string, unknown> | null;
  createdAt?: string;
  updatedAt?: string;
  performers: PerformerSummary[];
  tracks: Array<CoveRecord & { id?: number; orderIndex?: number; title?: string | null; startSec?: number; endSec?: number | null }>;
  files: Array<CoveRecord & {
    basename?: string;
    duration?: number;
    format?: string;
    audioCodec?: string;
    bitRate?: number;
    path?: string;
    sampleRate?: number | null;
    channels?: number | null;
    size?: number;
    hasVideoTrack?: boolean;
  }>;
}

export interface ImageRecord extends CoveRecord {
  id: number;
  title?: string | null;
  date?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  performers: PerformerSummary[];
  files: Array<CoveRecord & { basename?: string; format?: string; width?: number; height?: number; size?: number }>;
  tags?: TagReference[];
  galleries?: Array<CoveRecord>;
  groups?: Array<CoveRecord>;
  urls?: string[];
  details?: string | null;
  organized?: boolean;
}

export interface SimilarVideoResult extends CoveRecord {
  video: Video;
  distance: number;
  sectionIndex: number;
  startSec?: number;
  endSec?: number;
}

export interface SimilarImageResult extends CoveRecord {
  image: ImageRecord;
  distance: number;
}

export interface GalleryRecord extends CoveRecord {
  id: number;
  title?: string | null;
  displayName?: string | null;
  date?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  imageCount?: number;
  videoCount?: number;
  performers: PerformerSummary[];
  files: Array<CoveRecord & { path?: string; size?: number }>;
  tags?: TagReference[];
  urls?: string[];
  details?: string | null;
  organized?: boolean;
}

export interface StudioRecord extends CoveRecord {
  id: number;
  name: string;
  parentId?: number | null;
  parentName?: string | null;
  aliases: string[];
  videoCount?: number;
  imageCount?: number;
  galleryCount?: number;
  childStudioCount?: number;
  tags?: TagReference[];
  urls?: string[];
  details?: string | null;
  organized?: boolean;
}

export interface GroupRecord extends CoveRecord {
  id: number;
  name: string;
  aliases?: string | null;
  date?: string | null;
  studioName?: string | null;
  description?: string | null;
  kind?: string | number;
  itemCount?: number;
  videoCount?: number;
  imageCount?: number;
  audioCount?: number;
  textCount?: number;
  tags: TagReference[];
  urls?: string[];
}

export interface GroupItem extends CoveRecord {
  id: number;
  groupId: number;
  orderIndex: number;
  kind: string;
  videoId?: number | null;
  videoTitle?: string | null;
  hostType: string;
  hostId: number;
  imageId?: number | null;
  imageTitle?: string | null;
  childGroupId?: number | null;
  childGroupName?: string | null;
  startSec?: number | null;
  endSec?: number | null;
  title?: string | null;
  notes?: string | null;
  sourceSpanKey?: string | null;
  sourceProfileId?: number | null;
  sourceQueryJson?: string | null;
  snapshotAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface TextRecord extends CoveRecord {
  id: number;
  title?: string | null;
  code?: string | null;
  details?: string | null;
  date?: string | null;
  studioName?: string | null;
  organized?: boolean;
  fileCount?: number;
  maxWordCount?: number | null;
  maxPageCount?: number | null;
  performers: PerformerSummary[];
  tags: TagReference[];
  groups: Array<CoveRecord>;
  files: Array<CoveRecord & { basename?: string; path?: string; format?: string; wordCount?: number | null; pageCount?: number | null; size?: number }>;
  urls?: string[];
}

export interface SegmentRecord extends CoveRecord {
  id: number;
  hostType: string | number;
  hostId: number;
  hostTitle?: string | null;
  startSec: number;
  endSec?: number | null;
  tagName?: string | null;
  kind?: string | null;
  sourceKey: string;
  confidence?: number | null;
  title?: string | null;
  performerName?: string | null;
}

export interface PaginatedResponse<T> extends CoveRecord {
  items: T[];
  totalCount: number;
  page: number;
  perPage: number;
}

export interface SavedFilter extends CoveRecord {
  id: number;
  mode: string;
  name: string;
  findFilter?: string | null;
  objectFilter?: string | null;
  uiOptions?: string | null;
}

export interface FindFilter extends CoveRecord {
  q?: string;
  page?: number;
  perPage?: number;
  sort?: string;
  direction?: "asc" | "desc";
  sorts?: Array<{ key: string; direction: "asc" | "desc" }>;
  seed?: number;
}

export interface ListQueryOptions {
  appliedFilterSummary?: string;
  defaultFilterApplied?: boolean;
  q?: string;
  seed?: number;
  stabilizeSort?: boolean;
  page?: number;
  perPage?: number;
  limit?: number;
  unlimited?: boolean;
  sorts?: Array<{ key: string; direction: "asc" | "desc" }>;
  objectFilter?: Record<string, unknown>;
}

export interface ListResult<T> {
  items: T[];
  totalCount: number;
}

export interface JobInfo extends CoveRecord {
  id: string;
  type: string;
  description: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  error?: string;
  summary?: string;
}

export interface GlobalSearchItem extends CoveRecord {
  id: number;
  title: string;
  subtitle?: string | null;
}

export interface GlobalSearchGroup extends CoveRecord {
  type: string;
  items: GlobalSearchItem[];
}

export interface GlobalSearchResponse extends CoveRecord {
  groups: GlobalSearchGroup[];
  failedTypes: string[];
}

export interface MeResponse extends CoveRecord {
  user: CoveRecord & {
    username?: string;
    kind?: string;
    uiPreferences?: {
      theme?: {
        activeThemeId?: string | null;
        customThemeColors?: Record<string, string> | null;
      } | null;
      defaultFilters?: Record<string, string> | null;
    } | null;
  };
  permissions?: string[];
}

export interface SystemStatus extends CoveRecord {
  version?: string;
  authEnabled?: boolean;
}

export interface LoginResponse extends CoveRecord {
  token: string;
  refreshToken: string;
  accessExpires?: string;
  refreshExpires?: string;
  user?: CoveRecord & { username?: string };
  username?: string;
}

export type StoredCredential =
  | {
      type: "session";
      accessToken: string;
      refreshToken: string;
      accessExpires?: string;
      refreshExpires?: string;
    }
  | { type: "apiToken"; token: string };

export interface StoredProfile {
  server: string;
  credential?: StoredCredential;
}

export interface CoveCliConfig {
  version: 1;
  defaultProfile?: string;
  profiles: Record<string, StoredProfile>;
}
