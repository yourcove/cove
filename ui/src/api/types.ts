// ===== Entity Types =====

export interface Video {
  id: number;
  title?: string;
  code?: string;
  details?: string;
  captions?: string;
  director?: string;
  date?: string;
  organized: boolean;
  isVr?: boolean;
  studioId?: number;
  studioName?: string;
  urls: string[];
  tags: Tag[];
  performers: PerformerSummary[];
  files: VideoFile[];
  groups: GroupSummary[];
  galleries: GallerySummary[];
  remoteIds: VideoRemoteId[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  contextTagApplications?: TagApplication[];
  fieldProvenance?: FieldProvenance[];
  parentVideoId?: number | null;
  parentVideoTitle?: string | null;
  clipStartSec?: number | null;
  clipEndSec?: number | null;
  imagePath?: string | null;
}

export interface VideoListEntry {
  kind: "video" | "compilation";
  id: number;
  video?: Video;
  group?: Group;
}

export interface VideoRemoteId {
  endpoint: string;
  remoteId: string;
}

export interface VideoCreate {
  title?: string;
  code?: string;
  details?: string;
  captions?: string;
  director?: string;
  date?: string;
  rating?: number;
  organized?: boolean;
  isVr?: boolean;
  studioId?: number;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  galleryIds?: number[];
  groups?: VideoGroupInput[];
  remoteIds?: VideoRemoteId[];
  customFields?: Record<string, unknown>;
  parentVideoId?: number | null;
  clipStartSec?: number | null;
  clipEndSec?: number | null;
}

export interface FileBackedCreate {
  filePath: string;
}

export interface VideoUpdate extends Partial<VideoCreate> {
  clearFields?: string[];
}

export interface Performer {
  id: number;
  name: string;
  imagePath?: string;
  disambiguation?: string;
  gender?: string;
  birthdate?: string;
  deathDate?: string;
  ethnicity?: string;
  country?: string;
  eyeColor?: string;
  hairColor?: string;
  heightCm?: number;
  weight?: number;
  measurements?: string;
  fakeTits?: string;
  penisLength?: number;
  circumcised?: string;
  careerStart?: string;
  careerEnd?: string;
  tattoos?: string;
  piercings?: string;
  favorite: boolean;
  details?: string;
  urls: string[];
  aliases: string[];
  tags: Tag[];
  remoteIds: PerformerRemoteId[];
  videoCount: number;
  imageCount: number;
  galleryCount: number;
  groupCount: number;
  audioCount: number;
  textCount: number;
  faceCount?: number;
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fieldProvenance?: FieldProvenance[];
}

export interface PerformerRemoteId {
  endpoint: string;
  remoteId: string;
}

export interface PerformerSummary {
  id: number;
  name: string;
  disambiguation?: string;
  gender?: string;
  birthdate?: string;
  favorite: boolean;
  imagePath?: string;
  videoCount?: number;
  imageCount?: number;
  galleryCount?: number;
  audioCount?: number;
  textCount?: number;
}

export interface PerformerCreate {
  name: string;
  disambiguation?: string;
  gender?: string;
  birthdate?: string;
  deathDate?: string;
  ethnicity?: string;
  country?: string;
  eyeColor?: string;
  hairColor?: string;
  heightCm?: number;
  weight?: number;
  measurements?: string;
  fakeTits?: string;
  penisLength?: number;
  circumcised?: string;
  careerStart?: string;
  careerEnd?: string;
  tattoos?: string;
  piercings?: string;
  favorite?: boolean;
  rating?: number;
  details?: string;
  urls?: string[];
  aliases?: string[];
  tagIds?: number[];
  remoteIds?: PerformerRemoteId[];
  customFields?: Record<string, unknown>;
}

export interface PerformerUpdate extends Partial<PerformerCreate> {
  clearFields?: string[];
}

export interface PerformerScrapeRequest {
  inputKind?: "url" | "name";
  scraperId?: string;
  url?: string;
  name?: string;
  createMissingTags?: boolean;
}

export interface ScrapedPerformer {
  sourceScraperId?: string;
  name?: string;
  disambiguation?: string;
  gender?: string;
  birthdate?: string;
  country?: string;
  ethnicity?: string;
  eyeColor?: string;
  hairColor?: string;
  heightCm?: number;
  weight?: number;
  measurements?: string;
  tattoos?: string;
  piercings?: string;
  details?: string;
  imageUrl?: string;
  urls: string[];
  aliases: string[];
  tagNames: string[];
}

export interface PerformerScrapePreview {
  scraped: ScrapedPerformer;
  inputKind: "url" | "name";
  sourceValue?: string;
}

export interface Tag {
  id: number;
  name: string;
  description?: string;
  imagePath?: string;
  hasImage?: boolean;
  favorite: boolean;
  organized: boolean;
  showAsSegment?: boolean | null;
  segmentColorOverride?: string | null;
  segmentLaneOverride?: number | null;
  color?: string | null;
  tagGroupId?: number | null;
  tagGroupName?: string | null;
  tagGroupColor?: string | null;
  minOccurrenceSec?: number | null;
  minOccurrencePercent?: number | null;
  isDerived?: boolean;
  canRemove?: boolean;
  canReportIncorrect?: boolean;
  effectiveDurationSec?: number | null;
  effectiveDurationPercent?: number | null;
  aliases: string[];
  videoCount?: number;
  segmentCount?: number;
  imageCount?: number;
  galleryCount?: number;
  groupCount?: number;
  performerCount?: number;
  studioCount?: number;
  provenance?: TagProvenance[];
}

export interface TagRemoteId {
  endpoint: string;
  remoteId: string;
}

export interface TagProvenance {
  sourceKey: string;
  sourceRunId?: string;
  modelKey?: string;
  confidence?: number;
  appliedAt: string;
  contextType?: string;
  contextId?: number;
  totalDurationSec?: number;
  hostDurationSec?: number;
}

export interface FieldProvenance {
  fieldKey: string;
  sourceKey: string;
  sourceRunId?: string;
  modelKey?: string;
  value?: unknown;
  confidence?: number;
  createdAt: string;
}

export interface TagGroup {
  id: number;
  name: string;
  description?: string | null;
  color?: string | null;
  sortOrder: number;
  tagCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface TagGroupCreate {
  name: string;
  description?: string | null;
  color?: string | null;
  sortOrder?: number | null;
}

export interface TagGroupUpdate extends Partial<TagGroupCreate> {}

export interface TagApplication {
  id: number;
  hostType: string;
  hostId: number;
  contextType?: string | null;
  contextId?: number | null;
  tag: Tag;
  sourceKey: string;
  sourceRunId?: string | null;
  modelKey?: string | null;
  confidence?: number | null;
  totalDurationSec?: number | null;
  hostDurationSec?: number | null;
  appliedAt: string;
}

export interface TagApplicationCreate {
  hostType: string;
  hostId: number;
  tagId: number;
  sourceKey?: string;
  contextType?: string | null;
  contextId?: number | null;
  sourceRunId?: string | null;
  modelKey?: string | null;
  confidence?: number | null;
  totalDurationSec?: number | null;
  hostDurationSec?: number | null;
}

export interface TagDetail extends Tag {
  sortName?: string;
  parents: Tag[];
  children: Tag[];
  videoCount: number;
  performerCount: number;
  imageCount: number;
  galleryCount: number;
  studioCount: number;
  groupCount: number;
  audioCount: number;
  textCount: number;
  segmentCount: number;
  remoteIds: TagRemoteId[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fieldProvenance?: FieldProvenance[];
}

export interface TagGraphNode {
  id: number;
  name: string;
  favorite: boolean;
  description?: string;
  imagePath?: string;
  tagGroupId?: number;
  tagGroupName?: string;
  tagGroupColor?: string;
  parentIds: number[];
  childIds: number[];
  totalUsageCount: number;
  videoCount: number;
  segmentCount: number;
  imageCount: number;
  galleryCount: number;
  groupCount: number;
  performerCount: number;
  studioCount: number;
}

export interface TagGraphLink {
  sourceId: number;
  targetId: number;
}

export interface TagGraphResponse {
  items: TagGraphNode[];
  links: TagGraphLink[];
  totalCount: number;
}

export interface TagCreate {
  name: string;
  sortName?: string;
  description?: string;
  favorite?: boolean;
  organized?: boolean;
  color?: string | null;
  tagGroupId?: number | null;
  minOccurrenceSec?: number | null;
  minOccurrencePercent?: number | null;
  showAsSegment?: boolean | null;
  segmentColorOverride?: string | null;
  segmentLaneOverride?: number | null;
  aliases?: string[];
  parentIds?: number[];
  childIds?: number[];
  remoteIds?: TagRemoteId[];
  customFields?: Record<string, unknown>;
}

export interface TagUpdate extends Partial<TagCreate> {
  clearFields?: string[];
}

export interface Studio {
  id: number;
  name: string;
  imagePath?: string;
  parentId?: number;
  parentName?: string;
  favorite: boolean;
  details?: string;
  organized: boolean;
  urls: string[];
  aliases: string[];
  tags: Tag[];
  remoteIds: StudioRemoteId[];
  videoCount: number;
  imageCount: number;
  galleryCount: number;
  groupCount: number;
  performerCount: number;
  childStudioCount: number;
  audioCount: number;
  textCount: number;
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fieldProvenance?: FieldProvenance[];
}

export interface StudioRemoteId {
  endpoint: string;
  remoteId: string;
}

export interface StudioCreate {
  name: string;
  parentId?: number;
  rating?: number;
  favorite?: boolean;
  details?: string;
  organized?: boolean;
  urls?: string[];
  aliases?: string[];
  tagIds?: number[];
  remoteIds?: StudioRemoteId[];
  customFields?: Record<string, unknown>;
}

export interface StudioUpdate extends Partial<StudioCreate> {
  clearFields?: string[];
}

export interface Gallery {
  id: number;
  title?: string;
  code?: string;
  date?: string;
  details?: string;
  photographer?: string;
  organized: boolean;
  coverPath?: string;
  coverImageId?: number;
  studioId?: number;
  studioName?: string;
  urls: string[];
  tags: Tag[];
  performers: PerformerSummary[];
  imageCount: number;
  videoCount: number;
  videoIds: number[];
  folderPath?: string;
  files: GalleryFileInfo[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fieldProvenance?: FieldProvenance[];
  // Filename/folder-name fallback for display when title is null (scan no longer stores the filename
  // as the title). Prefers a zip-gallery file basename, else the folder name.
  displayName?: string;
}

export interface GalleryFileInfo {
  id: number;
  path: string;
  size: number;
  modTime: string;
  fingerprints: { type: string; value: string }[];
}

export interface GalleryChapter {
  id: number;
  title: string;
  imageIndex: number;
  galleryId: number;
  createdAt: string;
  updatedAt: string;
}

export interface GalleryChapterCreate {
  title: string;
  imageIndex: number;
}

export interface GalleryChapterUpdate {
  title?: string;
  imageIndex?: number;
}

export interface GalleryCreate {
  title?: string;
  code?: string;
  date?: string;
  details?: string;
  photographer?: string;
  rating?: number;
  organized?: boolean;
  studioId?: number;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  videoIds?: number[];
  customFields?: Record<string, unknown>;
}

export interface GalleryUpdate extends Partial<GalleryCreate> {
  clearFields?: string[];
}

export interface ImageFile {
  id: number;
  path: string;
  basename: string;
  format: string;
  width: number;
  height: number;
  size: number;
}

export interface Image {
  id: number;
  title?: string;
  code?: string;
  details?: string;
  photographer?: string;
  organized: boolean;
  studioId?: number;
  studioName?: string;
  date?: string;
  urls: string[];
  tags: Tag[];
  performers: PerformerSummary[];
  galleryCount: number;
  galleryIds: number[];
  galleries: GallerySummary[];
  groups?: GroupSummary[];
  files: ImageFile[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  contextTagApplications?: TagApplication[];
  fieldProvenance?: FieldProvenance[];
}


export interface VisualSimilarVideo {
  video: Video;
  distance: number;
  sectionIndex: number;
  startSec?: number;
  endSec?: number;
}

export interface AudioSimilarVideo {
  video: Video;
  distance: number;
  sectionIndex: number;
  startSec?: number;
  endSec?: number;
}

export interface VisualSimilarImage {
  image: Image;
  distance: number;
}
export interface ImageCreate {
  title?: string;
  code?: string;
  details?: string;
  photographer?: string;
  rating?: number;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  galleryIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
}

export interface ImageUpdate {
  title?: string;
  code?: string;
  details?: string;
  photographer?: string;
  rating?: number;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  galleryIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
  clearFields?: string[];
}

export interface AudioFile {
  id: number;
  path: string;
  basename: string;
  format: string;
  duration: number;
  audioCodec: string;
  bitRate: number;
  sampleRate?: number | null;
  channels?: number | null;
  size: number;
  hasVideoTrack: boolean;
}

export interface AudioTrackInfo {
  id: number;
  orderIndex: number;
  title?: string;
  startSec: number;
  endSec?: number | null;
}

export interface Audio {
  id: number;
  title?: string;
  code?: string;
  details?: string;
  organized: boolean;
  studioId?: number;
  studioName?: string;
  date?: string;
  imagePath?: string | null;
  urls: string[];
  tags: Tag[];
  performers: PerformerSummary[];
  tracks: AudioTrackInfo[];
  files: AudioFile[];
  groups: GroupSummary[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fileCount: number;
  maxDuration: number;
  hasVideoFiles: boolean;
  contextTagApplications?: TagApplication[];
  fieldProvenance?: FieldProvenance[];
}

export interface AudioCreate {
  title?: string;
  code?: string;
  details?: string;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
}

export interface AudioUpdate {
  title?: string;
  code?: string;
  details?: string;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
  clearFields?: string[];
}

export interface TextFile {
  id: number;
  path: string;
  basename: string;
  format: string;
  pageCount?: number | null;
  wordCount?: number | null;
  excerptText?: string | null;
  size: number;
}

export interface TextDocument {
  id: number;
  title?: string;
  code?: string;
  details?: string;
  organized: boolean;
  studioId?: number;
  studioName?: string;
  date?: string;
  imagePath?: string | null;
  urls: string[];
  tags: Tag[];
  performers: PerformerSummary[];
  files: TextFile[];
  groups: GroupSummary[];
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  fileCount: number;
  maxWordCount?: number | null;
  maxPageCount?: number | null;
  contextTagApplications?: TagApplication[];
  fieldProvenance?: FieldProvenance[];
}

export interface TextCreate {
  title?: string;
  code?: string;
  details?: string;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
}

export interface TextUpdate {
  title?: string;
  code?: string;
  details?: string;
  organized?: boolean;
  studioId?: number;
  date?: string;
  urls?: string[];
  tagIds?: number[];
  performerIds?: number[];
  customFields?: Record<string, unknown>;
  groupIds?: VideoGroupInput[];
  clearFields?: string[];
}

export interface TextContent {
  format: string;
  renderMode: "text" | "markdown" | "html";
  content: string;
}

export interface DeleteEntityOptions {
  deleteFile?: boolean;
  deleteGenerated?: boolean;
}

export type GroupKind = "static" | "dynamic";

export interface Group {
  id: number;
  name: string;
  aliases?: string;
  date?: string;
  studioId?: number;
  studioName?: string;
  director?: string;
  description?: string;
  frontImagePath?: string;
  backImagePath?: string;
  urls: string[];
  tags: Tag[];
  videoCount: number;
  imageCount?: number;
  audioCount?: number;
  textCount?: number;
  galleryCount?: number;
  performerCount?: number;
  studioCount?: number;
  tagItemCount?: number;
  faceCount?: number;
  segmentCount?: number;
  itemCount?: number;
  isCompilation?: boolean;
  subGroupCount: number;
  containingGroupCount: number;
  customFields?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  kind?: GroupKind;
  querySourceKey?: string | null;
  queryJson?: string | null;
  lastResolvedAt?: string | null;
  cachedItemCount?: number | null;
  showInVideoLists?: boolean;
  allowedHostTypes?: string[];
  sortOrder?: number;
  fieldProvenance?: FieldProvenance[];
}

export interface GroupReorder {
  ids: number[];
  startIndex?: number;
}

export interface GroupSummary {
  id: number;
  name: string;
  videoIndex: number;
}

export type GroupItemKind = "video" | "videoRange" | "image" | "audio" | "text" | "group" | "performer" | "studio" | "tag" | "gallery" | "face" | "segment";

export interface GroupItem {
  id: number;
  groupId: number;
  orderIndex: number;
  kind: GroupItemKind;
  videoId?: number | null;
  videoTitle?: string;
  hostType?: string;
  hostId?: number;
  imageId?: number | null;
  imageTitle?: string | null;
  childGroupId?: number | null;
  childGroupName?: string | null;
  startSec?: number;
  endSec?: number;
  title?: string;
  notes?: string;
  sourceSpanKey?: string;
  sourceProfileId?: number;
  sourceQueryJson?: string;
  snapshotAt?: string;
  createdAt: string;
  updatedAt: string;
}

export interface GroupItemCreate {
  orderIndex: number;
  kind: GroupItemKind;
  videoId?: number;
  startSec?: number;
  endSec?: number;
  title?: string;
  notes?: string;
  sourceSpanKey?: string;
  sourceProfileId?: number;
  sourceQueryJson?: string;
  hostType?: string;
  hostId?: number;
}

export interface GroupItemUpdate {
  orderIndex: number;
  kind: GroupItemKind;
  startSec?: number;
  endSec?: number;
  title?: string;
  notes?: string;
}

export interface GroupItemsReorder {
  ids: number[];
  startIndex?: number;
}

export interface GroupItemsRemoveHosts {
  kind: GroupItemKind;
  hostIds: number[];
}

export interface GroupItemSpanInput {
  spanKey?: string;
  videoId?: number;
  startSec?: number;
  endSec?: number;
  title?: string;
  profileId?: number;
  derivedQuery?: SegmentSpanDerivedQuery;
}

export interface GroupItemsFromSpans {
  spans: GroupItemSpanInput[];
}

export interface GroupPlaybackManifestItem {
  groupItemId: number;
  hostType: string;
  hostId: number;
  videoId?: number | null;
  audioId?: number | null;
  imageId?: number | null;
  textId?: number | null;
  segmentId?: number | null;
  videoTitle?: string;
  src: string;
  startSec: number;
  endSec?: number;
  durationSec?: number;
  displayDurationSec?: number | null;
  posterPath?: string;
  title?: string;
  format?: string | null;
  hasVideoTrack: boolean;
}

export interface GroupPlaybackManifest {
  items: GroupPlaybackManifestItem[];
}

export interface GallerySummary {
  id: number;
  title?: string;
  date?: string;
}

export interface GroupCreate {
  name: string;
  aliases?: string;
  date?: string;
  rating?: number;
  studioId?: number;
  director?: string;
  description?: string;
  urls?: string[];
  tagIds?: number[];
  customFields?: Record<string, unknown>;
  kind?: GroupKind;
  querySourceKey?: string;
  queryJson?: string | null;
  showInVideoLists?: boolean;
  allowedHostTypes?: string[];
  sortOrder?: number;
}

export interface GroupUpdate extends Partial<GroupCreate> {
  clearFields?: string[];
}

export interface BookmarkDto {
  hostType: AffinityHostType;
  hostId: number;
  createdAt: string;
}

export interface BookmarkToggle {
  hostType: AffinityHostType;
  hostId: number;
  saved: boolean;
}

export interface BookmarkState {
  hostType: AffinityHostType;
  hostId: number;
  saved: boolean;
  createdAt?: string | null;
}

export interface BookmarkBatchRequest {
  hostType: AffinityHostType;
  hostIds: number[];
}

export interface DynamicGroupSource {
  key: string;
  displayName: string;
}

export interface GroupQueryUpdate {
  querySourceKey: string;
  queryJson?: string | null;
  cacheTtlSec?: number | null;
}

export interface VideoFile {
  id: number;
  path: string;
  basename: string;
  format: string;
  width: number;
  height: number;
  duration: number;
  videoCodec: string;
  audioCodec: string;
  frameRate: number;
  bitRate: number;
  size: number;
  fingerprints: Fingerprint[];
  captions?: Caption[];
}

export interface Caption {
  id: number;
  languageCode: string;
  captionType: string;
  filename: string;
}

export interface Fingerprint {
  type: string;
  value: string;
}

export interface TagSegmentWall {
  id: number;
  title?: string;
  startSec: number;
  endSec?: number;
  kind: string;
  sourceKey: string;
  confidence?: number;
  videoId: number;
  videoTitle: string;
}

export type SegmentHostType = "video" | "image" | "audio";
export type DetectionHostType = "video" | "image";
export type AffinityHostType = "video" | "audio" | "text" | "image" | "performer" | "face" | "tag" | "studio" | "gallery" | "group" | "segment";
export type InteractionHostType = AffinityHostType | "segment" | "search" | "collection";

export interface Segment {
  id: number;
  hostType: SegmentHostType;
  hostId: number;
  startSec: number;
  endSec?: number;
  tagId?: number;
  tagName?: string;
  kind?: string;
  refId?: number;
  refLabel?: string;
  performerId?: number;
  performerName?: string;
  payload?: unknown;
  sourceKey: string;
  sourceRunId?: string;
  confidence?: number;
  title?: string;
  colorHint?: string;
  createdAt: string;
  updatedAt: string;
  fieldProvenance?: FieldProvenance[];
}

export interface SegmentRecord extends Segment {
  hostTitle?: string;
}

export interface SegmentCreate {
  startSec: number;
  endSec?: number;
  tagId?: number;
  kind?: string;
  refId?: number;
  payload?: unknown;
  sourceKey?: string;
  sourceRunId?: string;
  confidence?: number;
  title?: string;
  colorHint?: string;
}

export interface SegmentUpdate extends SegmentCreate {
  sourceKey: string;
}

export interface ResolvedSpan {
  spanKey: string;
  hostType: SegmentHostType;
  hostId: number;
  startSec: number;
  endSec: number;
  sourceKey?: string;
  kind?: string;
  tagId?: number;
  tagName?: string;
  colorHint?: string;
  lane?: number;
  collapsedToInstant: boolean;
  segmentIds: number[];
}

export interface ResolvedSpanInterval {
  startSec: number;
  endSec: number;
}

export interface ResolvedSpanDetail {
  span: ResolvedSpan;
  videoId: number;
  videoTitle?: string;
  intervals: ResolvedSpanInterval[];
  profileId: number;
  profileVersion: number;
}

export interface VideoResolvedSpans {
  spans: ResolvedSpan[];
  profileId: number;
  profileVersion: number;
}

export interface ResolvedSpanList {
  spans: ResolvedSpan[];
}

export interface SegmentDerivedQueryOperandDescriptor {
  sourceKey?: string;
  kind?: string;
  tagIds?: number[];
  performerIds?: number[];
  faceIds?: number[];
  minConfidence?: number;
}

export interface SegmentDerivedQueryDescriptor {
  operator: SegmentSpanOperator;
  operands: SegmentDerivedQueryOperandDescriptor[];
  mergeGapSec?: number;
  minDurationSec?: number;
}

export type SegmentSpanOperator = "union" | "intersection" | "difference";

export interface SegmentSpanOperand {
  sourceKey?: string;
  kind?: string;
  tagIds?: number[];
  refIds?: number[];
  minConfidence?: number;
}

export interface SegmentSpanQueryRequest {
  profile?: number;
  operator: SegmentSpanOperator;
  operands: SegmentSpanOperand[];
  mergeGapSec?: number;
  minDurationSec?: number;
}

export interface SegmentDisplayProfile {
  id: number;
  name: string;
  description?: string;
  userId?: number;
  isSystem: boolean;
  isDefault: boolean;
  version: number;
  createdAt: string;
  updatedAt: string;
}

export interface SegmentDisplayProfileCreate {
  name: string;
  description?: string;
  isDefault: boolean;
}

export interface SegmentDisplayProfileUpdate {
  name: string;
  description?: string;
}

export interface SegmentSpanDerivedQuery {
  operator: SegmentSpanOperator;
  operands: SegmentSpanOperand[];
  mergeGapSec?: number;
  minDurationSec?: number;
}

export interface SegmentSpanSearchRequest {
  profile?: number;
  derivedQuery?: SegmentSpanDerivedQuery;
  page?: number;
  perPage?: number;
  sort?: string;
  direction?: "asc" | "desc";
  seed?: number;
  q?: string;
  videoTitle?: string;
  videoIds?: number[];
  excludeVideoIds?: number[];
  tagIds?: number[];
  kind?: string;
  sourceKey?: string;
  refIds?: number[];
  performerIds?: number[];
  confidence?: number;
  confidence2?: number;
  confidenceModifier?: CriterionModifier;
  durationSec?: number;
  durationSec2?: number;
  durationModifier?: CriterionModifier;
  title?: string;
  titleModifier?: CriterionModifier;
  hostType?: string;
  sourceCategory?: "user" | "extensions";
  sourceRunId?: string;
  sourceRunIdModifier?: CriterionModifier;
  colorHint?: string;
  colorHintModifier?: CriterionModifier;
  hasImage?: boolean;
  hasPayload?: boolean;
  startSec?: number;
  startSec2?: number;
  startSecModifier?: CriterionModifier;
  endSec?: number;
  endSec2?: number;
  endSecModifier?: CriterionModifier;
  createdAt?: string;
  createdAt2?: string;
  createdAtModifier?: CriterionModifier;
  updatedAt?: string;
  updatedAt2?: string;
  updatedAtModifier?: CriterionModifier;
}

export interface SegmentSpanSearchResultItem {
  span: ResolvedSpan;
  videoId: number;
  videoTitle?: string;
  videoUpdatedAt?: string;
  profileId: number;
}

export interface SegmentSpanSearchResponse {
  items: SegmentSpanSearchResultItem[];
  /** Exact when known cheaply; -1 when the page was served via early termination (use hasMore for nav
   *  and fetch the exact total from the spans/count endpoint). */
  totalCount: number;
  page: number;
  perPage: number;
  hasMore?: boolean;
}

export interface SegmentSpanCountResponse {
  totalCount: number;
}

export interface SegmentDistinctValue {
  value: string;
  count: number;
}

export interface SegmentDisplayRule {
  id: number;
  sourceKey?: string;
  kind?: string;
  tagId?: number;
  tagName?: string;
  tagCategory?: string;
  hostType?: SegmentHostType;
  visible: boolean;
  minConfidence?: number;
  minDurationSec?: number;
  mergeGapSec?: number;
  collapseToInstant: boolean;
  colorOverride?: string;
  lane?: number;
  priority?: number;
  userId?: number;
  createdAt: string;
  updatedAt: string;
}

export interface SegmentDisplayRuleCreate {
  sourceKey?: string;
  kind?: string;
  tagId?: number;
  tagCategory?: string;
  hostType?: SegmentHostType;
  visible: boolean;
  minConfidence?: number;
  minDurationSec?: number;
  mergeGapSec?: number;
  collapseToInstant: boolean;
  colorOverride?: string;
  lane?: number;
  priority?: number;
}

export interface SegmentDisplayRuleUpdate extends SegmentDisplayRuleCreate {}

export interface SegmentDisplayProfilePreviewRequest {
  videoId: number;
  rules: SegmentDisplayRuleCreate[];
}

export interface Detection {
  id: number;
  hostType: DetectionHostType;
  hostId: number;
  observedAtSec?: number;
  frameWidth: number;
  frameHeight: number;
  class: string;
  score: number;
  x: number;
  y: number;
  w: number;
  h: number;
  extra?: unknown;
  refKind?: string;
  refId?: number;
  groupKey?: string;
  sourceKey: string;
  sourceRunId?: string;
  createdAt: string;
  updatedAt: string;
}

export interface DetectionCreate {
  observedAtSec?: number;
  frameWidth: number;
  frameHeight: number;
  class: string;
  score: number;
  x: number;
  y: number;
  w: number;
  h: number;
  extra?: unknown;
  refKind?: string;
  refId?: number;
  groupKey?: string;
  sourceKey?: string;
  sourceRunId?: string;
}

export interface DetectionUpdate extends DetectionCreate {
  sourceKey: string;
}

export interface FaceTopSuggestion {
  performerId: number;
  performerName: string;
  coverImageUrl?: string;
  confidence: number;
  localPerformerId?: number;
  externalUrl?: string;
  localPerformerHasImage?: boolean;
  localPerformerIsLocalOnly?: boolean;
}

export interface Face {
  id: number;
  label?: string;
  performerId?: number;
  performerName?: string;
  coverImageUrl?: string;
  ignored: boolean;
  mergedIntoFaceId?: number;
  detectionCount: number;
  videoCount: number;
  imageCount: number;
  primarySourceKey?: string;
  createdAt: string;
  updatedAt: string;
  appearanceCount: number;
  frameSampleCount: number;
  topSuggestion?: FaceTopSuggestion;
  fieldProvenance?: FieldProvenance[];
  /** 1-based position among the linked performer's faces, with the total. 0 when unlinked/not computed. */
  performerFaceIndex?: number;
  performerFaceCount?: number;
}

export interface FaceAppearance {
  appearanceId: number;
  hostType: "video" | "image";
  hostId: number;
  title: string;
  thumbnailUrl: string;
  frameSampleCount: number;
  retainedSpatialSampleCount: number;
  segmentCount: number;
  firstSeenAtSec?: number;
  lastSeenAtSec?: number;
  topConfidence?: number;
}

export interface FaceAppearancesResponse {
  items: FaceAppearance[];
  totalVideos: number;
  totalImages: number;
}

export interface FaceHostFace {
  id: number;
  label?: string;
  performerId?: number;
  performerName?: string;
  coverImageUrl?: string;
  appearanceCount: number;
  frameSampleCount: number;
  videoCount: number;
  imageCount: number;
  firstSeenAtSec?: number;
  lastSeenAtSec?: number;
  topConfidence?: number;
}

export interface FaceCreate {
  label?: string;
  performerId?: number;
  ignored?: boolean;
  primarySourceKey?: string;
}

export interface FaceUpdate {
  label?: string;
  performerId?: number;
  ignored: boolean;
  primarySourceKey?: string;
}

export interface FaceLink {
  performerId?: number;
  setPerformerImage?: boolean;
}

export interface FaceBatchLinkTopSuggestionRequest {
  faceIds: number[];
  // Create + link performers for reference (SAIE) matches with no local performer, scraping a
  // configured metadata server where available. When false those faces are skipped.
  createFromReference?: boolean;
  // Link faces whose top matches conflict (the same face matched 2+ performers). When false such faces
  // are skipped. When true, mergeConflicting decides whether to merge the competing matches into the top
  // one or just link the top match directly.
  linkConflicting?: boolean;
  mergeConflicting?: boolean;
}

export interface FaceBatchDeleteRequest {
  faceIds: number[];
}

export interface FaceBatchSkipped {
  faceId: number;
  reason: string;
}

export interface FaceBatchFailed {
  faceId: number;
  error: string;
}

export interface FaceBatchOperationResult {
  succeeded: number[];
  skipped: FaceBatchSkipped[];
  failed: FaceBatchFailed[];
}

export interface FaceCreatePerformer {
  name: string;
  setPerformerImage?: boolean;
}

export interface FaceMerge {
  targetFaceId: number;
}

export interface FaceIgnore {
  ignored: boolean;
}

export interface FaceDeleteImpact {
  detectionCount: number;
  embeddingCount: number;
  segmentCount: number;
  hasCoverImage: boolean;
  releasedMergedFaceCount: number;
}

export interface FaceNotPresentResult {
  faceFound: boolean;
  hostHadFace: boolean;
  movedHostCount: number;
  targetFaceId?: number;
  createdNewFace: boolean;
  mergedIntoTarget: boolean;
  sourceFaceEmptied: boolean;
}

export interface AiFaceCoverRepairRequest {
  force?: boolean;
  faceIds?: number[];
}

export interface AiFaceCoverRepairResult {
  scannedCount: number;
  repairedCount: number;
  skippedCount: number;
  failedCount: number;
  errors: string[];
}

export interface FaceSimilar {
  id: number;
  label?: string;
  performerId?: number;
  performerName?: string;
  coverImageUrl?: string;
  ignored: boolean;
  mergedIntoFaceId?: number;
  detectionCount: number;
  videoCount: number;
  imageCount: number;
  primarySourceKey?: string;
  createdAt: string;
  updatedAt: string;
  appearanceCount: number;
  frameSampleCount: number;
  distance: number;
}

export interface FaceSuggestionEvidence {
  faceId: number;
  thumbnailUrl?: string;
  similarity: number;
}

export interface FaceSuggestion {
  performerId: number;
  performerName: string;
  coverImageUrl?: string;
  confidence: number;
  why: string;
  evidence: FaceSuggestionEvidence[];
  localPerformerId?: number;
  externalUrl?: string;
  localPerformerHasImage?: boolean;
  localPerformerIsLocalOnly?: boolean;
  // Shared by all competing matches for the same face when 2+ reference sources disagree.
  conflictGroupId?: string;
  // True when linking this reference match will refresh the performer from its metadata server
  // ("Update existing performers from metadata servers" is on). The compare dialog hides the
  // "use face image for this local performer" option in that case.
  referenceWillRefreshFromMetadata?: boolean;
  // The originating metadata server's GraphQL endpoint and its id for this performer. Sent back on
  // accept so the host records the remote id on the linked performer (and scrapes it when enabled).
  referenceEndpoint?: string;
  referenceExternalId?: string;
}

export type AiDataKind = "embedding" | "detection" | "segment" | "tagApplication" | "face";

export interface AiDataSelector {
  sourceKey?: string;
  sourceRunId?: string;
  model?: string;
  modality?: string;
  hostType?: string;
  hostId?: number;
  kinds?: AiDataKind[];
}

export interface AiDataSummaryItem {
  kind: string;
  detail?: string;
  sourceKey: string;
  sourceRunId?: string;
  model?: string;
  hostType: string;
  count: number;
}

export interface AiDataSummary {
  items: AiDataSummaryItem[];
  totals: Record<string, number>;
  totalCount: number;
}

export interface AiDataPurgeResult {
  removedCounts: Record<string, number>;
}

export interface AiDataPurgeRequest extends AiDataSelector {
  dryRun?: boolean;
}

export interface EntityEngagement {
  hostId: number;
  isFavorite: boolean;
  rating?: number;
  resumeTime: number;
  playDuration: number;
  playCount: number;
  lastPlayedAt?: string;
  likeCount: number;
  derivedLikeCount: number;
  pageVisitCount: number;
  completeCount: number;
}

export interface EntityRatings {
  hostId: number;
  ratings: Record<string, number>;
}

export interface EntityFavorite {
  isFavorite: boolean;
}

export interface EngagementInteractionWrite {
  hostType: InteractionHostType;
  hostId?: number;
  kind: string;
  positionSec?: number;
  durationSec?: number;
  sessionId?: string;
  meta?: Record<string, unknown>;
}

export interface EngagementInteraction {
  id: number;
  hostType: InteractionHostType;
  hostId?: number;
  kind: string;
  at: string;
  positionSec?: number;
  durationSec?: number;
  sessionId?: string;
  meta?: Record<string, unknown>;
}

export interface VideoInteractionEvent {
  kind: string;
  at: string;
  meta?: unknown;
}

export interface PlaybackIntervalInput {
  startSec: number;
  endSec: number;
}

export interface PlaybackIntervalsRequest {
  hostType: string;
  hostId: number;
  sessionId: string;
  mediaDurationSec: number;
  currentPositionSec: number;
  state: string;
  intervals: PlaybackIntervalInput[];
  surface?: string;
  scopeKey?: string;
  parentHostType?: string;
  parentHostId?: number;
  itemHostType?: string;
  itemHostId?: number;
  groupItemId?: number;
  segmentId?: number;
  clipStartSec?: number;
  clipEndSec?: number | null;
  autoplay?: boolean;
  muted?: boolean;
  fullscreen?: boolean;
  playbackRate?: number;
  route?: string;
  referrer?: string;
  recommendationSource?: string;
  context?: Record<string, unknown>;
}

export interface PlaybackInterval {
  startSec: number;
  endSec: number;
  recordedAt: string;
}

export interface VideoPlaybackSession {
  sessionId: string;
  startedAt: string;
  lastSeenAt: string;
  endedAt?: string | null;
  state: string;
  mediaDurationSec: number;
  totalWatchedSec: number;
  lastPositionSec?: number | null;
  isCompleted: boolean;
  intervals: PlaybackInterval[];
}

export interface VideoHistory {
  playHistory: string[];
  likeHistory: string[];
  events?: VideoInteractionEvent[];
  allTimeWatchedIntervals?: PlaybackInterval[];
  totalDistinctWatchedSec?: number;
  sessions?: VideoPlaybackSession[];
}

export interface EntityEngagementBatchRequest {
  hostType: AffinityHostType;
  hostIds: number[];
}

export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  perPage: number;
}

export interface Stats {
  videoCount: number;
  imageCount: number;
  galleryCount: number;
  performerCount: number;
  studioCount: number;
  tagCount: number;
  groupCount: number;
  audioCount: number;
  textCount: number;
  segmentCount: number;
  faceCount: number;
  faceAppearanceCount: number;
  embeddingCount: number;
  detectionCount: number;
  tagApplicationCount: number;
  aiRunCount: number;
  videoFileSize: number;
  imageFileSize: number;
  audioFileSize: number;
  textFileSize: number;
  totalFileSize: number;
  videoDuration: number;
  audioDuration: number;
  totalPlayDuration: number;
  videoPlayCount: number;
  audioPlayCount: number;
  textReadCount: number;
  imageViewCount: number;
  segmentViewCount: number;
  videoCompleteCount: number;
  audioCompleteCount: number;
  textCompleteCount: number;
  imageCompleteCount: number;
  segmentCompleteCount: number;
  videoConsumedSeconds: number;
  audioConsumedSeconds: number;
  textConsumedSeconds: number;
  imageConsumedSeconds: number;
  segmentConsumedSeconds: number;
  totalLikes: number;
  totalDerivedLikes: number;
  totalFavorites: number;
}

export interface SystemStatus {
  version: string;
  appDir: string | null;
  configFile: string | null;
  databasePath: string;
  migrationRequired: boolean;
  pendingMigrations: string[] | null;
  authEnabled?: boolean;
  migrationStatusUnknown?: boolean;
  migrationStatusError?: string | null;
}

export interface DatabaseMigrationResult {
  message: string;
  appliedMigrations: string[];
  pendingMigrations: string[];
  preMigrationBackupPath: string | null;
  migrationRequired: boolean;
}

export type RatingSystemType = "stars" | "decimal";
export type RatingStarPrecision = "full" | "half" | "quarter" | "tenth";

export interface RatingSystemOptions {
  type: RatingSystemType;
  starPrecision: RatingStarPrecision;
}

export type AuthUserKind = "user" | "shareLink" | "apiToken" | "system" | "anonymous";

export interface UserThemePreferences {
  activeThemeId?: string | null;
  activeComponentStyles?: string[] | null;
  activeLayoutStyle?: string | null;
  customThemeColors?: Record<string, string> | null;
  styleOptions?: Record<string, Record<string, string>> | null;
}

export interface UserUiPreferences {
  theme?: UserThemePreferences | null;
  ratingSystemOptions?: RatingSystemOptions | null;
  tracking?: UserTrackingPreferences | null;
  videos?: UserVideosPreferences | null;
  keybindingOverrides?: Record<string, string> | null;
  playback?: UserPlaybackPreferences | null;
  /** JSON blob of the user's customized home page rows (opaque to the server). */
  homePageContent?: string | null;
  /** Per-list-mode default saved filter, keyed by mode (e.g. "videos") -> opaque filter JSON. */
  defaultFilters?: Record<string, string> | null;
}

export interface UserTrackingPreferences {
  enabled?: boolean | null;
  minViewSeconds?: number | null;
  viewCompletionRatio?: number | null;
  minImageDetailViewSeconds?: number | null;
  minDerivedLikeSessionSeconds?: number | null;
  sessionIdleTimeoutSec?: number | null;
  dwellPositiveSec?: number | null;
}

export interface UserVideosPreferences {
  includeCompilationGroups?: boolean | null;
}

export interface UserPlaybackPreferences {
  skipSeconds?: number | null;
}

export interface MeResponse {
  user: {
    id: string;
    username: string;
    roles?: string[];
    kind?: AuthUserKind;
    uiPreferences?: UserUiPreferences | null;
  };
  permissions: string[];
  readGrantedEntityKinds?: string[];
}

export interface InterfaceConfig {
  language?: string;
  menuItems: string[];
  handyConnectionEnabled: boolean;
  handyKey?: string;
  defaultDurationForImages?: number;
  disableDropdownCreatePerformer: boolean;
  disableDropdownCreateStudio: boolean;
  disableDropdownCreateTag: boolean;
}

export type CustomFieldEntityType = "video" | "audio" | "text" | "performer" | "tag" | "studio" | "gallery" | "image" | "group" | "face";
export type CustomFieldType = "text" | "longText" | "number" | "boolean" | "date" | "timestamp" | "duration" | "percent" | "url" | "enum" | "tag" | "performer" | "studio" | "video" | "gallery" | "image" | "group";

export interface CustomFieldDefinition {
  id?: number;
  key: string;
  label: string;
  type: CustomFieldType;
  entityTypes: CustomFieldEntityType[];
  options: string[];
  filterable: boolean;
  sortable: boolean;
  isMultiValue?: boolean;
  displayOrder?: number;
  createdAt?: string;
  updatedAt?: string;
}

export interface CustomFieldDefinitionCreate {
  key?: string;
  label: string;
  type: CustomFieldType;
  entityTypes: CustomFieldEntityType[];
  options: string[];
  filterable: boolean;
  sortable: boolean;
  isMultiValue?: boolean;
  displayOrder?: number | null;
}

export interface CustomFieldDefinitionUpdate {
  key?: string;
  label?: string;
  type?: CustomFieldType;
  entityTypes?: CustomFieldEntityType[];
  options?: string[];
  filterable?: boolean;
  sortable?: boolean;
  isMultiValue?: boolean;
  displayOrder?: number | null;
}

export interface UiConfig {
  title?: string;
  faviconPath?: string;
  logoPath?: string;
  troubleshootingModeEnabled: boolean;
  abbreviateCounters: boolean;
  ratingSystemOptions: RatingSystemOptions;
  showStudioAsText: boolean;
  customCss?: string;
  customJs?: string;
  enableCSSCustomization: boolean;
  enableJSCustomization: boolean;
  customLocalesPath?: string;
  autostartVideo: boolean;
  autostartVideoOnPlaySelected: boolean;
  autoplayOnListClick: boolean;
  maxLoopDuration: number;
  alwaysResumeOnPlayback: boolean;
  playerVideoStartPercent: number;
  playerVideoStartMinDuration: number;
  continuePlaylistDefault: boolean;
  showAbLoopControls: boolean;
  soundOnPreview: boolean;
  previewSegmentDuration: number;
  previewSegments: number;
  previewExcludeStart: string;
  previewExcludeEnd: string;
  wallShowTitle: boolean;
  wallPlayback: number;
  wallPreviewType: string;
  imageObjectFit: string;
  videoObjectFit: string;
  feedVideoSource: string;
  feedVideoSound: boolean;
  feedVideoStartPercent: number;
  feedVideoStartMinDuration: number;
  deleteFileDefault: boolean;
  slideshowDelay: number;
  noBrowser: boolean;
  notificationsEnabled: boolean;
  keybindingOverrides: Record<string, string>;
}

export interface SecurityConfig {
  enabled: boolean;
  username?: string;
  allowAnonymousShareLinks: boolean;
  knownProxies: string[];
  trustedHosts: string[];
  newPassword?: string;
}

export interface MetadataServer {
  endpoint: string;
  apiKey: string;
  name: string;
  maxRequestsPerMinute: number;
}

export interface IdentifyDefaultsConfig {
  createTags: boolean;
  createPerformers: boolean;
  createStudios: boolean;
  autoApplyMinFingerprintMatches?: number;
  autoApplyMaxDurationDifferenceSeconds?: number;
  autoApplyMaxPhashDistance?: number;
}

export interface MetadataBatchDefaultsConfig {
  refreshAlreadyTagged: boolean;
  createParentStudios: boolean;
  excludeFields: string[];
}

export interface ScrapeApplyDefaultsConfig {
  createMissingTags: boolean;
  createMissingPerformers: boolean;
  createMissingStudio: boolean;
  markOrganized: boolean;
  hydratePerformers: boolean;
}

export interface ScraperPreference {
  entityType?: string;
  site: string;
  scraperId: string;
}

export interface ScrapingConfig {
  scraperDirectories: string[];
  metadataServers: MetadataServer[];
  scraperPreferences: ScraperPreference[];
  identifyDefaults: IdentifyDefaultsConfig;
  scrapeApplyDefaults?: ScrapeApplyDefaultsConfig;
  metadataBatchDefaults: MetadataBatchDefaultsConfig;
}

export interface CoveConfig {
  covePaths: CovePathConfig[];
  downloaderPathOverrides: DownloaderPathOverrideConfig[];
  generatedPath?: string;
  cachePath?: string;
  host: string;
  port: number;
  maxParallelTasks: number;
  maxConcurrentDownloads: number;
  calculateMd5: boolean;
  frameExtractionMode: string;
  videoExtensions: string[];
  imageExtensions: string[];
  galleryExtensions: string[];
  audioExtensions: string[];
  textExtensions: string[];
  excludePatterns: string[];
  excludeImagePatterns: string[];
  excludeGalleryPatterns: string[];
  createGalleriesFromFolders: boolean;
  writeImageThumbnails: boolean;
  createImageClipsFromVideos: boolean;
  galleryCoverRegex: string;
  deleteGeneratedDefault: boolean;
  maxStreamingTranscodeSize: number;
  // Unified hardware acceleration: "off" | "auto" | "nvenc" | "qsv" | "vaapi" | "amf" | "videotoolbox".
  hardwareAcceleration: string;
  hardwareEncodeSessionLimit: number;
  ffmpegInputArgs?: string;
  ffmpegOutputArgs?: string;
  previewPreset: string;
  previewAudio: string;
  logLevel: string;
  ffmpegPath?: string;
  ffprobePath?: string;
  interface: InterfaceConfig;
  ui: UiConfig;
  security: SecurityConfig;
  scraping: ScrapingConfig;
  customFieldDefinitions: CustomFieldDefinition[];
}

/** Verified ffmpeg hardware capabilities reported by the server (GET /system/ffmpeg-capabilities). */
export interface FfmpegCapabilities {
  ffmpegFound: boolean;
  ffmpegPath?: string | null;
  /** Accelerators whose H.264 encoder is built in AND passed a real test-encode: nvenc/qsv/vaapi/amf/videotoolbox. */
  accelerators: string[];
  /** Informational `ffmpeg -hwaccels` decode methods. */
  decoders: string[];
  probedAtUtc: string;
}

export interface CovePathConfig {
  path: string;
  excludeVideo: boolean;
  excludeImage: boolean;
  excludeAudio: boolean;
  excludeText: boolean;
}

export interface DownloaderPathOverrideConfig {
  downloaderId: string;
  site?: string;
  path: string;
}

export interface JobInfo {
  id: string;
  type: string;
  description: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  subTask?: string;
  startedAt: string;
  completedAt?: string;
  error?: string;
  unitsTotal?: number;
  unitsCompleted?: number;
  unitsSucceeded?: number;
  unitsFailed?: number;
  unitsSkipped?: number;
  summary?: string;
  /** Server-computed estimate of seconds remaining (null when unknown/stalled). */
  etaSeconds?: number | null;
  /** UTC timestamp the ETA was computed at, so the client can count it down smoothly. */
  updatedAt?: string | null;
}

export interface FindFilter {
  q?: string;
  page?: number;
  perPage?: number;
  sort?: string;
  direction?: "asc" | "desc";
  seed?: number;
}

export interface SavedFilter {
  id: number;
  mode: string;
  name: string;
  findFilter?: string;
  objectFilter?: string;
  uiOptions?: string;
}

export interface SavedFilterCreate {
  mode: string;
  name: string;
  findFilter?: string;
  objectFilter?: string;
  uiOptions?: string;
}

export interface SavedFilterUpdate {
  mode?: string;
  name?: string;
  findFilter?: string;
  objectFilter?: string;
  uiOptions?: string;
}

export interface ScraperSummary {
  id: string;
  name: string;
  entityType: string;
  supportedScrapes: string[];
  urls: string[];
  sourcePath: string;
  preferenceSites?: string[] | null;
}

export interface ScrapeAttempt {
  id: string;
  scraperId: string;
  entityType: string;
  entityId?: number | null;
  inputKind: string;
  inputJson?: string | null;
  resultJson?: string | null;
  candidateResultsJson?: string | null;
  entitySnapshotJson?: string | null;
  status: string;
  error?: string | null;
  createdAt: string;
  appliedAt?: string | null;
}

export interface CreateScrapeAttemptRequest {
  scraperId: string;
  entityType: string;
  entityId?: number;
  inputKind: string;
  url?: string;
  name?: string;
  fragment?: Record<string, unknown>;
}

export type ScrapeCollectionItemAction = "include" | "create" | "exclude";

export interface ScrapeCollectionItemSelection {
  name: string;
  action: ScrapeCollectionItemAction;
}

export interface ApplyVideoScrapeAttemptRequest {
  replaceFields?: string[];
  collectionModes?: Record<string, string>;
  createMissingTags?: boolean;
  createMissingPerformers?: boolean;
  createMissingStudio?: boolean;
  markOrganized?: boolean;
  hydratePerformers?: boolean;
  selectedCandidateIndex?: number;
  tagSelections?: ScrapeCollectionItemSelection[];
  performerSelections?: ScrapeCollectionItemSelection[];
}

export type ApplyScrapeAttemptRequest = ApplyVideoScrapeAttemptRequest;

export interface ResolveScrapeRelationsRequest {
  performers: string[];
  tags: string[];
}

// One entry per requested name that matched an existing entity. matchedName is the existing
// entity's primary name — it differs from input when the match was via an alias.
export interface ScrapeRelationMatch {
  input: string;
  matchedName: string;
}

export interface ResolveScrapeRelationsResult {
  performers: ScrapeRelationMatch[];
  tags: ScrapeRelationMatch[];
}

export interface DownloaderDescriptor {
  id: string;
  name: string;
  supportedEntity: string;
  supportedUrlPatterns: string[];
  capabilities: string[];
}

export interface DownloaderQualityOption {
  id: string;
  label: string;
  description?: string;
}

export interface DownloaderMatch {
  downloaderId: string;
  downloaderName: string;
  supportedEntity: string;
  normalizedUrl: string;
  label?: string;
  qualityOptions: DownloaderQualityOption[];
  sourceUrl?: string | null;
}

export interface DownloaderMatchRequest {
  url: string;
}

export interface DownloaderPreflightRequest {
  url: string;
  entity: string;
  entityId?: number;
}

export interface DownloaderPreflightResponse {
  isDuplicate: boolean;
  duplicateReason?: string;
}

export interface DownloaderStartRequest {
  downloaderId: string;
  url: string;
  entity: string;
  entityId?: number;
  qualityId?: string;
  autoApplyMetadata?: boolean;
  allowDuplicateDownload?: boolean;
  sourceUrl?: string;
  createMissingTags?: boolean;
  createMissingPerformers?: boolean;
  createMissingStudio?: boolean;
  markOrganized?: boolean;
  hydratePerformers?: boolean;
}

export interface DownloaderBatchGenerateOptions {
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
  overwrite?: boolean;
  videoIds?: number[];
  paths?: string[];
}

export interface DownloaderBatchItem {
  downloaderId?: string;
  url: string;
  entity: string;
  entityId?: number;
  qualityId?: string;
  sourceUrl?: string;
  label?: string;
  title?: string;
  createEntityIfMissing?: boolean;
  autoApplyMetadata?: boolean;
  createMissingTags?: boolean;
  createMissingPerformers?: boolean;
  createMissingStudio?: boolean;
  markOrganized?: boolean;
  hydratePerformers?: boolean;
  galleryIds?: number[];
  groupIds?: VideoGroupInput[];
}

export interface DownloaderBatchFollowUp {
  scrapeVideos?: boolean;
  allowDuplicateDownloads?: boolean;
  generate?: DownloaderBatchGenerateOptions;
}

export interface DownloaderBatchStartRequest {
  items: DownloaderBatchItem[];
  followUp?: DownloaderBatchFollowUp;
  preflightBeforeQueue?: boolean;
}

export interface DownloaderBatchIssue {
  kind: "skipped" | "failed";
  label: string;
  reason: string;
}

export interface DownloaderBatchStartResponse {
  jobId: string;
  queuedCount: number;
  issues?: DownloaderBatchIssue[];
}

export interface MetadataServerValidationResult {
  valid: boolean;
  status: string;
  username?: string;
}

export interface MetadataServerPerformerMatch {
  endpoint: string;
  serverName: string;
  id: string;
  name: string;
  disambiguation?: string;
  gender?: string;
  birthDate?: string;
  country?: string;
  imageUrl?: string;
  deleted: boolean;
  mergedIntoId?: string;
  aliases: string[];
  urls: string[];
}

export interface MetadataServerPerformerImportRequest {
  endpoint: string;
  performerId: string;
  fieldStrategies?: Record<string, "ignore" | "merge" | "overwrite">;
}

export interface MetadataServerFindByIdsRequest {
  endpoint: string;
  ids: string[];
}

export interface MetadataServerPerformerBatchTagRequest {
  endpoint: string;
  ids?: number[];
  filter?: PerformerFilterCriteria;
  selectAll?: boolean;
  refreshAlreadyTagged?: boolean;
  excludeFields?: string[];
}

export interface MetadataServerStudioMatch {
  endpoint: string;
  serverName: string;
  id: string;
  name: string;
  imageUrl?: string;
  aliases: string[];
  urls: string[];
  parentName?: string;
}

export interface MetadataServerStudioImportRequest {
  endpoint: string;
  studioId: string;
  fieldStrategies?: Record<string, string>;
}

export interface MetadataServerStudioBatchTagRequest {
  endpoint: string;
  ids?: number[];
  filter?: StudioFilterCriteria;
  selectAll?: boolean;
  refreshAlreadyTagged?: boolean;
  excludeFields?: string[];
  createParentStudios?: boolean;
}

export interface MetadataServerTagMatch {
  endpoint: string;
  metadataServerName: string;
  id: string;
  name: string;
  description?: string;
  aliases: string[];
}

export interface MetadataServerTagImportRequest {
  endpoint: string;
  tagId: string;
}

export interface MetadataServerTagBatchTagRequest {
  endpoint: string;
  ids?: number[];
  filter?: TagFilterCriteria;
  selectAll?: boolean;
  refreshAlreadyTagged?: boolean;
  excludeFields?: string[];
}

export interface MetadataServerEntityCandidate {
  remoteId: string;
  name: string;
  existsLocally: boolean;
  localId?: number;
}

export interface MetadataServerVideoEntityOverride {
  remoteId: string;
  name: string;
  action: string;
  localId?: number;
}

export interface MetadataServerVideoMatch {
  endpoint: string;
  serverName: string;
  id: string;
  title?: string;
  code?: string;
  date?: string;
  director?: string;
  details?: string;
  studioName?: string;
  imageUrl?: string;
  duration?: number;
  performerNames: string[];
  tagNames: string[];
  urls: string[];
  fingerprintAlgorithms: string[];
  matchCount: number;
  fingerprints: MetadataServerFingerprint[];
  studioCandidate?: MetadataServerEntityCandidate;
  performerCandidates: MetadataServerEntityCandidate[];
  tagCandidates: MetadataServerEntityCandidate[];
}

export interface MetadataServerFingerprint {
  algorithm: string;
  hash: string;
  duration?: number;
}

export interface MetadataServerVideoImportRequest {
  endpoint: string;
  videoId: string;
  setCoverImage?: boolean;
  // When true, replace even an explicitly set cover; otherwise only an auto-generated frame is replaced.
  overwriteExplicitCover?: boolean;
  setTags?: boolean;
  setPerformers?: boolean;
  setStudio?: boolean;
  onlyExistingTags?: boolean;
  onlyExistingPerformers?: boolean;
  onlyExistingStudio?: boolean;
  markOrganized?: boolean;
  excludedTagNames?: string[];
  excludedPerformerNames?: string[];
  studioOverride?: MetadataServerVideoEntityOverride;
  performerOverrides?: MetadataServerVideoEntityOverride[];
  tagOverrides?: MetadataServerVideoEntityOverride[];
  fieldStrategies?: Record<string, "ignore" | "merge" | "overwrite">;
}

// ===== Filter Criteria =====

export type CriterionModifier =
  | "EQUALS" | "NOT_EQUALS" | "GREATER_THAN" | "LESS_THAN"
  | "INCLUDES" | "EXCLUDES" | "INCLUDES_ALL" | "EXCLUDES_ALL"
  | "IS_NULL" | "NOT_NULL" | "BETWEEN" | "NOT_BETWEEN"
  | "MATCHES_REGEX" | "NOT_MATCHES_REGEX";

export interface IntCriterion {
  value: number;
  value2?: number;
  modifier: CriterionModifier;
}

export interface StringCriterion {
  value: string;
  modifier: CriterionModifier;
}

export interface CustomFieldCriterion extends StringCriterion {
  key: string;
  type?: CustomFieldType;
  value2?: string;
  displayValue?: string;
  displayValue2?: string;
}

export interface BoolCriterion {
  value: boolean;
}

export interface MultiIdCriterion {
  value: number[];
  modifier: CriterionModifier;
  excludes?: number[];
  requiredIds?: number[];
  requiredIdsDepth?: number;
  depth?: number;
}

export interface DateCriterion {
  value: string;
  value2?: string;
  modifier: CriterionModifier;
}

export interface TimestampCriterion {
  value: string;
  value2?: string;
  modifier: CriterionModifier;
}

export interface TagDurationClause {
  tagId?: number;
  value?: number;
  value2?: number;
  modifier: CriterionModifier;
  unit?: "seconds" | "percent";
  contextMode?: "any" | "host" | "context";
  contextType?: string;
}

export interface TagDurationCriterion extends TagDurationClause {
  clauses?: TagDurationClause[];
  _names?: Record<string, string>;
}

export type FingerprintAlgorithm = "md5" | "oshash" | "phash";

export interface FingerprintCriterion {
  type: FingerprintAlgorithm;
  value: string;
  modifier: CriterionModifier;
}

export interface VideoFilterCriteria {
  title?: string;
  code?: string;
  path?: string;
  organized?: boolean;
  isVr?: boolean;
  includeCompilationGroups?: boolean;
  studioId?: number;
  groupId?: number;
  tagIds?: number[];
  performerIds?: number[];
  ratingCriterion?: IntCriterion;
  likeCounterCriterion?: IntCriterion;
  durationCriterion?: IntCriterion;
  resolutionCriterion?: IntCriterion;
  playCountCriterion?: IntCriterion;
  performerCountCriterion?: IntCriterion;
  tagsCriterion?: MultiIdCriterion;
  tagDurationCriterion?: TagDurationCriterion;
  performersCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  groupsCriterion?: MultiIdCriterion;
  organizedCriterion?: BoolCriterion;
  isVrCriterion?: BoolCriterion;
  hasSegmentsCriterion?: BoolCriterion;
  pathCriterion?: StringCriterion;
  fingerprintCriterion?: FingerprintCriterion;
  hashCriterion?: StringCriterion;
  checksumCriterion?: StringCriterion;
  duplicatedPhashCriterion?: BoolCriterion;
  duplicatedTitleCriterion?: BoolCriterion;
  duplicatedRemoteIdCriterion?: BoolCriterion;
  urlCriterion?: StringCriterion;
  dateCriterion?: DateCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  performerFavoriteCriterion?: BoolCriterion;
  videoCodecCriterion?: StringCriterion;
  audioCodecCriterion?: StringCriterion;
  frameRateCriterion?: IntCriterion;
  bitrateInterval?: IntCriterion;
  fileCountCriterion?: IntCriterion;
  remoteIdCriterion?: StringCriterion;
  remoteIdValueCriterion?: StringCriterion;
  remoteIdCountCriterion?: IntCriterion;
  isMissingCriterion?: BoolCriterion;
  duplicatedCriterion?: StringCriterion;
  titleCriterion?: StringCriterion;
  codeCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  directorCriterion?: StringCriterion;
  tagCountCriterion?: IntCriterion;
  resumeTimeCriterion?: IntCriterion;
  playDurationCriterion?: IntCriterion;
  lastPlayedAtCriterion?: TimestampCriterion;
  galleriesCriterion?: MultiIdCriterion;
  performerTagsCriterion?: MultiIdCriterion;
  performerAgeCriterion?: IntCriterion;
  captionsCriterion?: StringCriterion;
  orientationCriterion?: StringCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface PerformerFilterCriteria {
  name?: string;
  favorite?: boolean;
  rating?: number;
  tagIds?: number[];
  nameCriterion?: StringCriterion;
  ratingCriterion?: IntCriterion;
  ageCriterion?: IntCriterion;
  genderCriterion?: StringCriterion;
  ethnicityCriterion?: StringCriterion;
  countryCriterion?: StringCriterion;
  favoriteCriterion?: BoolCriterion;
  tagsCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  videoCountCriterion?: IntCriterion;
  studioCountCriterion?: IntCriterion;
  imageCountCriterion?: IntCriterion;
  galleryCountCriterion?: IntCriterion;
  birthdateCriterion?: DateCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  pathCriterion?: StringCriterion;
  urlCriterion?: StringCriterion;
  weightCriterion?: IntCriterion;
  heightCriterion?: IntCriterion;
  isMissingCriterion?: BoolCriterion;
  remoteIdCriterion?: StringCriterion;
  remoteIdValueCriterion?: StringCriterion;
  remoteIdCountCriterion?: IntCriterion;
  disambiguationCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  eyeColorCriterion?: StringCriterion;
  hairColorCriterion?: StringCriterion;
  measurementsCriterion?: StringCriterion;
  fakeTitsCriterion?: StringCriterion;
  penisLengthCriterion?: IntCriterion;
  circumcisedCriterion?: StringCriterion;
  careerStartCriterion?: DateCriterion;
  careerEndCriterion?: DateCriterion;
  careerLengthCriterion?: IntCriterion;
  tattooCriterion?: StringCriterion;
  piercingsCriterion?: StringCriterion;
  aliasesCriterion?: StringCriterion;
  deathDateCriterion?: DateCriterion;
  playCountCriterion?: IntCriterion;
  likeCounterCriterion?: IntCriterion;
  groupsCriterion?: MultiIdCriterion;
  tagCountCriterion?: IntCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface TagFilterCriteria {
  name?: string;
  favorite?: boolean;
  rating?: number;
  favoriteCriterion?: BoolCriterion;
  ratingCriterion?: IntCriterion;
  videoCountCriterion?: IntCriterion;
  videoCountIncludesChildren?: boolean;
  performerCountCriterion?: IntCriterion;
  performerCountIncludesChildren?: boolean;
  parentsCriterion?: MultiIdCriterion;
  childrenCriterion?: MultiIdCriterion;
  tagGroupsCriterion?: MultiIdCriterion;
  isMissingCriterion?: BoolCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  nameCriterion?: StringCriterion;
  sortNameCriterion?: StringCriterion;
  remoteIdCriterion?: StringCriterion;
  remoteIdValueCriterion?: StringCriterion;
  remoteIdCountCriterion?: IntCriterion;
  aliasesCriterion?: StringCriterion;
  descriptionCriterion?: StringCriterion;
  imageCountCriterion?: IntCriterion;
  imageCountIncludesChildren?: boolean;
  galleryCountCriterion?: IntCriterion;
  galleryCountIncludesChildren?: boolean;
  studioCountCriterion?: IntCriterion;
  studioCountIncludesChildren?: boolean;
  groupCountCriterion?: IntCriterion;
  groupCountIncludesChildren?: boolean;
  parentCountCriterion?: IntCriterion;
  childCountCriterion?: IntCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
  extensionCriteria?: ExtensionFilterCriterion[];
}

export interface ExtensionFilterCriterion {
  extensionId: string;
  filterId: string;
  modifier: string;
  value: unknown;
}

export interface StudioFilterCriteria {
  name?: string;
  favorite?: boolean;
  parentId?: number;
  tagIds?: number[];
  ratingCriterion?: IntCriterion;
  favoriteCriterion?: BoolCriterion;
  tagsCriterion?: MultiIdCriterion;
  videoCountCriterion?: IntCriterion;
  urlCriterion?: StringCriterion;
  remoteIdCriterion?: StringCriterion;
  remoteIdValueCriterion?: StringCriterion;
  remoteIdCountCriterion?: IntCriterion;
  isMissingCriterion?: BoolCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  nameCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  aliasesCriterion?: StringCriterion;
  parentsCriterion?: MultiIdCriterion;
  parentCountCriterion?: IntCriterion;
  childCountCriterion?: IntCriterion;
  tagCountCriterion?: IntCriterion;
  groupCountCriterion?: IntCriterion;
  organizedCriterion?: BoolCriterion;
  galleryCountCriterion?: IntCriterion;
  imageCountCriterion?: IntCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface GalleryFilterCriteria {
  title?: string;
  organized?: boolean;
  studioId?: number;
  tagIds?: number[];
  performerIds?: number[];
  ratingCriterion?: IntCriterion;
  organizedCriterion?: BoolCriterion;
  tagsCriterion?: MultiIdCriterion;
  performersCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  imageCountCriterion?: IntCriterion;
  titleCriterion?: StringCriterion;
  dateCriterion?: DateCriterion;
  pathCriterion?: StringCriterion;
  fingerprintCriterion?: FingerprintCriterion;
  checksumCriterion?: StringCriterion;
  urlCriterion?: StringCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  performerFavoriteCriterion?: BoolCriterion;
  isMissingCriterion?: BoolCriterion;
  codeCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  photographerCriterion?: StringCriterion;
  fileCountCriterion?: IntCriterion;
  tagCountCriterion?: IntCriterion;
  performerCountCriterion?: IntCriterion;
  performerAgeCriterion?: IntCriterion;
  typicalResolutionCriterion?: IntCriterion;
  videosCriterion?: MultiIdCriterion;
  performerTagsCriterion?: MultiIdCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface ImageFilterCriteria {
  title?: string;
  organized?: boolean;
  studioId?: number;
  galleryId?: number;
  tagIds?: number[];
  performerIds?: number[];
  ratingCriterion?: IntCriterion;
  organizedCriterion?: BoolCriterion;
  tagsCriterion?: MultiIdCriterion;
  performersCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  galleriesCriterion?: MultiIdCriterion;
  titleCriterion?: StringCriterion;
  likeCounterCriterion?: IntCriterion;
  resolutionCriterion?: IntCriterion;
  pathCriterion?: StringCriterion;
  fingerprintCriterion?: FingerprintCriterion;
  checksumCriterion?: StringCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  performerFavoriteCriterion?: BoolCriterion;
  isMissingCriterion?: BoolCriterion;
  codeCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  photographerCriterion?: StringCriterion;
  urlCriterion?: StringCriterion;
  dateCriterion?: DateCriterion;
  fileCountCriterion?: IntCriterion;
  tagCountCriterion?: IntCriterion;
  performerCountCriterion?: IntCriterion;
  performerAgeCriterion?: IntCriterion;
  orientationCriterion?: StringCriterion;
  performerTagsCriterion?: MultiIdCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface AudioFilterCriteria {
  ratingCriterion?: IntCriterion;
  titleCriterion?: StringCriterion;
  codeCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  pathCriterion?: StringCriterion;
  formatCriterion?: StringCriterion;
  audioCodecCriterion?: StringCriterion;
  urlCriterion?: StringCriterion;
  organizedCriterion?: BoolCriterion;
  hasVideoFilesCriterion?: BoolCriterion;
  hasCoverCriterion?: BoolCriterion;
  dateCriterion?: DateCriterion;
  durationCriterion?: IntCriterion;
  bitRateCriterion?: IntCriterion;
  fileSizeCriterion?: IntCriterion;
  fileModTimeCriterion?: TimestampCriterion;
  fileCountCriterion?: IntCriterion;
  trackCountCriterion?: IntCriterion;
  trackTitleCriterion?: StringCriterion;
  sampleRateCriterion?: IntCriterion;
  channelsCriterion?: IntCriterion;
  playCountCriterion?: IntCriterion;
  likeCounterCriterion?: IntCriterion;
  playDurationCriterion?: IntCriterion;
  lastPlayedAtCriterion?: TimestampCriterion;
  tagCountCriterion?: IntCriterion;
  performerCountCriterion?: IntCriterion;
  performerTagsCriterion?: MultiIdCriterion;
  tagsCriterion?: MultiIdCriterion;
  performersCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  groupsCriterion?: MultiIdCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface TextFilterCriteria {
  ratingCriterion?: IntCriterion;
  titleCriterion?: StringCriterion;
  codeCriterion?: StringCriterion;
  detailsCriterion?: StringCriterion;
  contentCriterion?: StringCriterion;
  pathCriterion?: StringCriterion;
  formatCriterion?: StringCriterion;
  urlCriterion?: StringCriterion;
  organizedCriterion?: BoolCriterion;
  hasCoverCriterion?: BoolCriterion;
  dateCriterion?: DateCriterion;
  wordCountCriterion?: IntCriterion;
  pageCountCriterion?: IntCriterion;
  fileSizeCriterion?: IntCriterion;
  fileModTimeCriterion?: TimestampCriterion;
  fileCountCriterion?: IntCriterion;
  playCountCriterion?: IntCriterion;
  likeCounterCriterion?: IntCriterion;
  playDurationCriterion?: IntCriterion;
  lastReadAtCriterion?: TimestampCriterion;
  tagCountCriterion?: IntCriterion;
  performerCountCriterion?: IntCriterion;
  performerTagsCriterion?: MultiIdCriterion;
  tagsCriterion?: MultiIdCriterion;
  performersCriterion?: MultiIdCriterion;
  studiosCriterion?: MultiIdCriterion;
  groupsCriterion?: MultiIdCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface GroupFilterCriteria {
  name?: string;
  studioId?: number;
  nameCriterion?: StringCriterion;
  ratingCriterion?: IntCriterion;
  durationCriterion?: IntCriterion;
  studiosCriterion?: MultiIdCriterion;
  tagsCriterion?: MultiIdCriterion;
  dateCriterion?: DateCriterion;
  urlCriterion?: StringCriterion;
  createdAtCriterion?: TimestampCriterion;
  updatedAtCriterion?: TimestampCriterion;
  kindCriterion?: StringCriterion;
  aliasesCriterion?: StringCriterion;
  querySourceKeyCriterion?: StringCriterion;
  allowedHostTypesCriterion?: StringCriterion;
  hasQueryCriterion?: BoolCriterion;
  isBuiltInCriterion?: BoolCriterion;
  showInVideoListsCriterion?: BoolCriterion;
  lastResolvedAtCriterion?: TimestampCriterion;
  sortOrderCriterion?: IntCriterion;
  cachedItemCountCriterion?: IntCriterion;
  isMissingCriterion?: BoolCriterion;
  directorCriterion?: StringCriterion;
  synopsisCriterion?: StringCriterion;
  performersCriterion?: MultiIdCriterion;
  itemCountCriterion?: IntCriterion;
  videoCountCriterion?: IntCriterion;
  imageCountCriterion?: IntCriterion;
  audioCountCriterion?: IntCriterion;
  textCountCriterion?: IntCriterion;
  galleryCountCriterion?: IntCriterion;
  performerItemCountCriterion?: IntCriterion;
  studioItemCountCriterion?: IntCriterion;
  tagItemCountCriterion?: IntCriterion;
  faceCountCriterion?: IntCriterion;
  segmentCountCriterion?: IntCriterion;
  subGroupCountCriterion?: IntCriterion;
  containingGroupCountCriterion?: IntCriterion;
  tagCountCriterion?: IntCriterion;
  customFieldCriterion?: CustomFieldCriterion;
  customFieldCriteria?: CustomFieldCriterion[];
}

export interface FilteredQueryRequest<T = Record<string, unknown>> {
  findFilter?: FindFilter;
  objectFilter?: T;
}

// ===== Bulk Edit Types =====

export type BulkUpdateMode = "SET" | "ADD" | "REMOVE";

export interface VideoGroupInput {
  groupId: number;
  videoIndex: number;
}

export interface BulkVideoUpdate {
  ids: number[];
  clearFields?: string[];
  rating?: number;
  organized?: boolean;
  studioId?: number | null;
  date?: string;
  code?: string;
  director?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
  performerIds?: number[];
  performerMode?: BulkUpdateMode;
  galleryIds?: number[];
  galleryMode?: BulkUpdateMode;
  groupIds?: VideoGroupInput[];
  groupMode?: BulkUpdateMode;
}

export interface BulkPerformerUpdate {
  ids: number[];
  rating?: number;
  favorite?: boolean;
  gender?: string;
  details?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
}

export interface BulkTagUpdate {
  ids: number[];
  clearFields?: string[];
  description?: string;
  color?: string;
  tagGroupId?: number | null;
  minOccurrenceSec?: number;
  minOccurrencePercent?: number;
  organized?: boolean;
  favorite?: boolean;
  rating?: number;
  parentIds?: number[];
  parentMode?: BulkUpdateMode;
  childIds?: number[];
  childMode?: BulkUpdateMode;
}

export interface BulkStudioUpdate {
  ids: number[];
  clearFields?: string[];
  rating?: number;
  favorite?: boolean;
  details?: string;
  organized?: boolean;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
}

export interface BulkGalleryUpdate {
  ids: number[];
  clearFields?: string[];
  rating?: number;
  organized?: boolean;
  studioId?: number | null;
  date?: string;
  code?: string;
  details?: string;
  photographer?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
  performerIds?: number[];
  performerMode?: BulkUpdateMode;
}

export interface BulkImageUpdate {
  ids: number[];
  clearFields?: string[];
  rating?: number;
  organized?: boolean;
  studioId?: number | null;
  date?: string;
  code?: string;
  details?: string;
  photographer?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
  performerIds?: number[];
  performerMode?: BulkUpdateMode;
  galleryIds?: number[];
  galleryMode?: BulkUpdateMode;
}

export interface BulkAudioUpdate {
  ids: number[];
  clearFields?: string[];
  organized?: boolean;
  studioId?: number | null;
  date?: string;
  code?: string;
  details?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
  performerIds?: number[];
  performerMode?: BulkUpdateMode;
}

export interface BulkTextUpdate {
  ids: number[];
  clearFields?: string[];
  organized?: boolean;
  studioId?: number | null;
  date?: string;
  code?: string;
  details?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
  performerIds?: number[];
  performerMode?: BulkUpdateMode;
}

export interface BulkGroupUpdate {
  ids: number[];
  clearFields?: string[];
  rating?: number;
  studioId?: number | null;
  date?: string;
  director?: string;
  description?: string;
  tagIds?: number[];
  tagMode?: BulkUpdateMode;
}

// ===== Plugin Types =====
export interface Plugin {
  id: string;
  name: string;
  description: string;
  version: string;
  enabled: boolean;
  tasks: PluginTask[];
  settings?: PluginSettingSchema[];
  url?: string;
}

export interface PluginSettingSchema {
  name: string;
  type: "STRING" | "NUMBER" | "BOOLEAN";
  displayName?: string;
  description?: string;
}

export interface PluginTask {
  name: string;
  description: string;
}

export interface RunPluginTaskRequest {
  pluginId: string;
  taskName: string;
  args?: Record<string, string>;
}

export interface PluginSettings {
  enabledMap: Record<string, boolean>;
}

// ===== Extension System Types =====
export interface ExtensionManifest {
  extensionBundles: ExtensionUiBundle[];
  pages: ExtensionPageDef[];
  slots: ExtensionSlotContribution[];
  tabs: ExtensionTabContribution[];
  features: ExtensionFeatureDef[];
  themes: ExtensionThemeDef[];
  componentStyles: ExtensionComponentStyleDef[];
  layoutStyles: ExtensionLayoutStyleDef[];
  settingsTabs: ExtensionSettingsTab[];
  settingsPanels: ExtensionSettingsPanel[];
  componentOverrides: ExtensionComponentOverride[];
  pageOverrides: ExtensionPageOverride[];
  dialogOverrides: ExtensionDialogOverride[];
  actions: ExtensionAction[];
  tutorialTopics?: ExtensionTutorialTopic[];
  listFilters?: ExtensionListFilterContribution[];
  listSorts?: ExtensionListSortContribution[];
  frontendRuntimeVersion?: string;
  jsBundleUrl?: string;
  cssBundleUrl?: string;
}

export interface ExtensionUiBundle {
  extensionId: string;
  version: string;
  jsBundleUrl?: string;
  cssBundleUrl?: string;
}

export interface ExtensionListFilterOption {
  value: string;
  label: string;
}

export interface ExtensionListFilterContribution {
  id: string;
  entityType: string;
  label: string;
  criterionType: string;
  extensionId: string;
  filterKey?: string;
  customFieldKey?: string;
  customFieldType?: string;
  entityReferenceType?: string;
  modifiers?: CriterionModifier[];
  options?: ExtensionListFilterOption[];
  filterId?: string;
  order: number;
}

export interface ExtensionListSortContribution {
  id: string;
  entityType: string;
  label: string;
  extensionId: string;
  sortKey?: string;
  customFieldKey?: string;
  customFieldType?: string;
  order: number;
}

export interface ExtensionTutorialTopic {
  id: string;
  title: string;
  description?: string;
  pages?: string[];
  contexts?: string[];
  extensionId?: string;
  order: number;
  slides?: ExtensionTutorialSlide[];
  parentTopicId?: string;
  /** When "setup", this topic is the extension's setup guide and is surfaced after install. */
  kind?: string;
}

export interface ExtensionTutorialSlide {
  id: string;
  title: string;
  caption?: string;
  bodyMarkdown?: string;
  points?: string[];
  imageSrc?: string;
  imageAlt?: string;
  mockKind?: string;
  links?: ExtensionTutorialLink[];
}

export interface ExtensionTutorialLink {
  label: string;
  url: string;
}

export interface ExtensionPageDef {
  route: string;
  label: string;
  icon?: string;
  detailRoute?: string;
  showInNav: boolean;
  navOrder: number;
  requiredPermission?: string;
  requiredPermissions?: string[];
  requiredPermissionMode?: "all" | "any";
  componentName?: string;
  extensionId?: string;
}

export interface ExtensionSlotContribution {
  id: string;
  slot: string;
  extensionId: string;
  contentType: "component" | "html";
  componentName?: string;
  html?: string;
  order: number;
}

export interface ExtensionTabContribution {
  key: string;
  label: string;
  pageType: string;
  extensionId: string;
  componentName: string;
  order: number;
  countEndpoint?: string;
  icon?: string;
  manualContexts?: string[];
  requiredPermission?: string;
  requiredPermissions?: string[];
  requiredPermissionMode?: "all" | "any";
}

export interface ExtensionFeatureDef {
  key: string;
  extensionId: string;
  options?: Record<string, string>;
}

export interface ExtensionThemeDef {
  id: string;
  name: string;
  description?: string;
  cssVariables?: Record<string, string>;
  cssUrl?: string;
  componentStyle?: string;
  layoutStyle?: string;
  backgroundAnimation?: string;
  colorScheme?: string;
}

export interface ExtensionComponentStyleDef {
  id: string;
  name: string;
  description?: string;
}

export interface ExtensionLayoutStyleDef {
  id: string;
  name: string;
  description?: string;
}

export interface ExtensionSettingsTab {
  key: string;
  label: string;
  extensionId: string;
  order: number;
  icon?: string;
  parentTabKey?: string;
  description?: string;
  searchKeywords?: string[];
  aliases?: string[];
  layout?: "panels" | "page";
}

export interface ExtensionSettingsPanel {
  id: string;
  label: string;
  extensionId: string;
  componentName: string;
  order: number;
  targetTab?: string;
  targetSection?: string;
}

export interface ExtensionComponentOverride {
  targetComponent: string;
  extensionId: string;
  componentName: string;
  priority: number;
}

export interface ExtensionPageOverride {
  targetPage: string;
  extensionId: string;
  componentName: string;
  priority: number;
}

export interface ExtensionDialogOverride {
  dialogId: string;
  extensionId: string;
  componentName: string;
  priority: number;
}

export interface ExtensionAction {
  id: string;
  label: string;
  extensionId: string;
  /** "toolbar", "context-menu", "bulk" */
  actionType: string;
  entityTypes: string[];
  icon?: string;
  apiEndpoint?: string;
  handlerName?: string;
  order: number;
  pages?: string[];
  suppressSuccessAlert?: boolean;
  requiredPermission?: string;
}

export interface ExtensionInfo {
  id: string;
  name: string;
  version: string;
  description?: string;
  author?: string;
  url?: string;
  iconUrl?: string;
  enabled: boolean;
  hasUI: boolean;
  hasApi: boolean;
  hasState: boolean;
  hasJobs: boolean;
  hasEvents: boolean;
  hasData: boolean;
  hasMiddleware: boolean;
  hasActions: boolean;
  categories: string[];
  minCoveVersion?: string;
  dependencies: Record<string, string>;
  externalDependencies: ExtensionExternalDependency[];
  settings: ExtensionSettingManifest[];
  kind: string;
  source: string;
  installedAt?: string;
  jobs: { id: string; name: string; description?: string }[];
}

export interface ExtensionExternalDependency {
  id: string;
  name: string;
  kind: string;
  required: boolean;
  description?: string;
  versionRequirement?: string;
  executables: string[];
  environmentVariables: string[];
  configurationKeys: string[];
  installHint?: string;
  nativeHint?: string;
  dockerHint?: string;
  url?: string;
  extensionIds: string[];
}

export interface ExtensionSettingManifest {
  name: string;
  type: string;
  displayName?: string;
  description?: string;
  extensionIds: string[];
}

// ===== Registry Types =====
export interface RegistrySearchResult {
  items: RegistryExtensionSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface RegistryExtensionSummary {
  id: string;
  name: string;
  version: string;
  description?: string;
  author?: string;
  iconUrl?: string;
  kind?: string;
  categories: string[];
  updatedAt?: string;
  minCoveVersion?: string;
}

export interface RegistryExtensionDetail extends RegistryExtensionSummary {
  url?: string;
  readme?: string;
  changelog?: string;
  screenshots: string[];
  dependencies: Record<string, string>;
  externalDependencies: ExtensionExternalDependency[];
  settings: ExtensionSettingManifest[];
  versions: RegistryVersionInfo[];
}

export interface RegistryVersionInfo {
  version: string;
  releasedAt?: string;
  changelog?: string;
  minCoveVersion?: string;
  checksum?: string;
}

export interface RegistryUpdateInfo {
  extensionId: string;
  currentVersion: string;
  latestVersion: string;
  changelog?: string;
}

export interface RegistryInstallResult {
  message?: string;
  path?: string;
  requiresDependencies?: boolean;
  extension?: { id: string; name: string; version: string };
  missingDependencies?: DependencyInfo[];
  installedDependencies?: string[];
}

export interface DependencyInfo {
  id: string;
  versionConstraint: string;
  name?: string;
  resolvedVersion?: string;
  available: boolean;
  installed: boolean;
}

export interface DependencyProblem {
  extensionId: string;
  dependencyId?: string;
  message: string;
}

export interface ExtensionDependencyImpact {
  id: string;
  name: string;
  version: string;
  enabled: boolean;
  kind: string;
  source: string;
}

export interface RegistryUninstallResult {
  message?: string;
  requiresDependents?: boolean;
  extension?: ExtensionDependencyImpact;
  dependents?: ExtensionDependencyImpact[];
  uninstalledExtensions?: string[];
}
