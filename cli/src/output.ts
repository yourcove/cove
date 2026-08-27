import type { Audio, GalleryRecord, GlobalSearchResponse, GroupItem, GroupRecord, ImageRecord, MetadataServerSummary, Performer, PerformerSummary, RemoteId, SavedFilter, SegmentRecord, SimilarImageResult, SimilarVideoResult, StudioRecord, Tag, TagReference, TextRecord, Video } from "./types";
import { savedFilterDefaultSort } from "./saved-filters";
import { cleanInline as clean, colorizeHex, stripTerminalSequences, terminalColorsEnabled, terminalHyperlinksEnabled, uiPalette } from "./ui";
import type { UiColor, UiPalette } from "./ui";

export interface RenderContext {
  appliedFilterSummary?: string;
  color?: UiColor;
  defaultFilterApplied?: boolean;
  hyperlinks?: boolean;
  listPosition?: { offset: number; page?: number; perPage?: number };
  metadataServers?: MetadataServerSummary[];
  server?: string;
  terminalWidth?: number;
  totalCount?: number;
}

type EntityKind = "audio" | "gallery" | "group" | "image" | "performer" | "segment" | "studio" | "tag" | "text" | "video";
export type CatalogEntityKind = Exclude<EntityKind, "audio" | "performer" | "video">;

interface ResolvedRenderContext {
  appliedFilterSummary?: string;
  color: UiColor;
  defaultFilterApplied: boolean;
  hyperlinks: boolean;
  listPosition?: { offset: number; page?: number; perPage?: number };
  metadataServers: MetadataServerSummary[];
  server?: string;
  terminalWidth: number;
  totalCount?: number;
}

const COVE_ENTITY_ROUTES: Record<EntityKind, string> = {
  audio: "audio",
  gallery: "gallery",
  group: "group",
  image: "image",
  performer: "performer",
  segment: "segment",
  studio: "studio",
  tag: "tag",
  text: "text",
  video: "video",
};

const METADATA_ENTITY_ROUTES = { performer: "performers", video: "scenes" } as const;

function resolveRenderContext(context: RenderContext = {}): ResolvedRenderContext {
  return {
    ...(context.appliedFilterSummary ? { appliedFilterSummary: clean(context.appliedFilterSummary) } : {}),
    color: context.color ?? terminalColorsEnabled(),
    defaultFilterApplied: context.defaultFilterApplied === true,
    hyperlinks: context.hyperlinks ?? terminalHyperlinksEnabled(),
    ...(context.listPosition ? { listPosition: context.listPosition } : {}),
    metadataServers: context.metadataServers ?? [],
    ...(context.server ? { server: context.server } : {}),
    terminalWidth: Math.max(30, context.terminalWidth ?? process.stdout.columns ?? 100),
    ...(context.totalCount === undefined ? {} : { totalCount: context.totalCount }),
  };
}

const countFormatter = new Intl.NumberFormat("en-US");
const graphemeSegmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" });

function formatCount(value: number): string {
  return countFormatter.format(value);
}

interface WidthColumn {
  width: number;
}

interface Column<T> extends WidthColumn {
  label: string;
  value: (item: T) => string;
  format?: (value: string, item: T, paint: UiPalette) => string;
}

function duration(seconds: unknown): string {
  if (typeof seconds !== "number" || !Number.isFinite(seconds)) return "—";
  const rounded = Math.round(seconds);
  const hours = Math.floor(rounded / 3600);
  const minutes = Math.floor((rounded % 3600) / 60);
  const remainder = rounded % 60;
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}` : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function resolution(file: Video["files"][number] | undefined): string {
  return typeof file?.width === "number" && typeof file.height === "number" ? `${file.width}×${file.height}` : "—";
}

function fit(value: string, width: number): string {
  const normalized = clean(value);
  if (Bun.stringWidth(normalized) <= width) return normalized + " ".repeat(width - Bun.stringWidth(normalized));
  if (width <= 1) return "…";
  let result = "";
  for (const { segment } of graphemeSegmenter.segment(normalized)) {
    if (Bun.stringWidth(result + segment) > width - 1) break;
    result += segment;
  }
  return `${result}…${" ".repeat(Math.max(0, width - Bun.stringWidth(result) - 1))}`;
}

function compactRow(cells: string[], columns: WidthColumn[], formats: Array<((value: string) => string) | undefined> = []): string {
  return cells.map((cell, index) => {
    const fitted = fit(cell, columns[index]!.width);
    const value = index === cells.length - 1 ? fitted.trimEnd() : fitted;
    return (formats[index] ?? ((text: string) => text))(value);
  }).join("  ");
}

function preferredColumnWidth<T>(label: string, items: T[], value: (item: T) => string): number {
  return items.reduce((width, item) => Math.max(width, Bun.stringWidth(clean(value(item)))), Bun.stringWidth(label));
}

function sizeColumns<T>(items: T[], columns: Column<T>[], terminalWidth: number): Column<T>[] {
  const available = Math.max(columns.length, terminalWidth - Math.max(0, columns.length - 1) * 2);
  const widths = columns.map(column => Math.max(1, column.width));
  let minimumTotal = widths.reduce((sum, width) => sum + width, 0);
  while (minimumTotal > available) {
    const index = widths.reduce((largest, width, candidate) => width > widths[largest]! && width > 1 ? candidate : largest, 0);
    if (widths[index] === 1) break;
    widths[index] = widths[index]! - 1;
    minimumTotal -= 1;
  }

  const preferred = columns.map((column, index) => Math.max(widths[index]!, preferredColumnWidth(column.label, items, column.value)));
  const demand = preferred.map((width, index) => width - widths[index]!);
  let remaining = Math.max(0, available - minimumTotal);
  while (remaining > 0) {
    const active = demand.map((value, index) => ({ index, value })).filter(item => item.value > 0);
    if (!active.length) break;
    const satisfiable = active.filter(item => item.value <= remaining).sort((left, right) => left.value - right.value || left.index - right.index)[0];
    if (satisfiable) {
      widths[satisfiable.index] = widths[satisfiable.index]! + satisfiable.value;
      demand[satisfiable.index] = 0;
      remaining -= satisfiable.value;
      continue;
    }
    const share = Math.floor(remaining / active.length);
    for (const item of active) {
      const increment = Math.min(item.value, share);
      widths[item.index] = widths[item.index]! + increment;
      demand[item.index] = demand[item.index]! - increment;
      remaining -= increment;
    }
    for (const item of active) {
      if (remaining === 0) break;
      widths[item.index] = widths[item.index]! + 1;
      demand[item.index] = demand[item.index]! - 1;
      remaining -= 1;
    }
  }
  return columns.map((column, index) => ({ ...column, width: widths[index]! }));
}

function renderListTitle(heading: string, summary: string, width: number, paint: UiPalette): string {
  const combined = `${heading} ${summary}`;
  if (Bun.stringWidth(combined) <= width) return `${paint.bold(heading)} ${paint.dim(summary)}`;
  return wrapDetail(combined, width).map((line, index) => index === 0 ? paint.bold(line) : paint.dim(line)).join("\n");
}

function listChrome(
  heading: string,
  nouns: [singular: string, plural: string],
  itemCount: number,
  totalCount: number,
  context: ResolvedRenderContext,
): { title: string; footer: string } {
  const paint = uiPalette(context.color);
  const noun = totalCount === 1 ? nouns[0] : nouns[1];
  if (!context.listPosition) {
    const footer = itemCount < totalCount ? `\n\n${wrapDetail(`Showing ${formatCount(itemCount)} of ${formatCount(totalCount)} ${nouns[1]}.`, context.terminalWidth).map(line => paint.dim(line)).join("\n")}` : "";
    return { title: renderListTitle(clean(heading), `· ${formatCount(totalCount)} ${noun}`, context.terminalWidth, paint), footer };
  }
  const position = listPositionSummary(itemCount, totalCount, context.listPosition);
  const pagination = position ?? (context.listPosition ? `${formatCount(totalCount)} ${noun}` : undefined);
  const headerLines = [paint.bold(clean(heading))];
  if (context.appliedFilterSummary) headerLines.push(...wrapDetail(context.appliedFilterSummary, context.terminalWidth).map(line => paint.dim(line)));
  if (pagination) headerLines.push("", ...wrapDetail(pagination, context.terminalWidth).map(line => paint.dim(line)));
  const footer = pagination ? `\n\n${wrapDetail(pagination, context.terminalWidth).map(line => paint.dim(line)).join("\n")}` : "";
  return { title: headerLines.join("\n"), footer };
}

function renderCompactList<T>(
  heading: string,
  nouns: [singular: string, plural: string],
  items: T[],
  totalCount: number,
  columns: Column<T>[],
  context: ResolvedRenderContext,
  emptyHint = "Try changing the filters.",
): string {
  const paint = uiPalette(context.color);
  const { title, footer } = listChrome(heading, nouns, items.length, totalCount, context);
  if (items.length === 0) {
    const empty = wrapDetail(`No ${nouns[1]} found.`, context.terminalWidth);
    const hint = wrapDetail(emptyHint, context.terminalWidth).map(line => paint.dim(line));
    return `${title}\n\n${[...empty, ...hint].join("\n")}${footer}`;
  }
  const sizedColumns = sizeColumns(items, columns, context.terminalWidth);
  const header = paint.dim(compactRow(sizedColumns.map(column => column.label.toUpperCase()), sizedColumns));
  const rows = items.map(item => compactRow(
    sizedColumns.map(column => column.value(item)),
    sizedColumns,
    sizedColumns.map((column, index) => value => column.format?.(value, item, paint) ?? (index === 0 ? paint.accent(value) : value)),
  ));
  return `${title}\n\n${[header, ...rows].join("\n")}${footer}`;
}

function listPositionSummary(itemCount: number, totalCount: number, position: ResolvedRenderContext["listPosition"]): string | undefined {
  if (!position) return undefined;
  if (itemCount === 0) {
    if (position.page === undefined || position.perPage === undefined) return undefined;
    const totalPages = Math.max(1, Math.ceil(totalCount / position.perPage));
    const pageContext = position.page > totalPages
      ? `Page ${formatCount(position.page)} requested · ${formatCount(totalPages)} ${totalPages === 1 ? "page" : "pages"} available`
      : `Page ${formatCount(position.page)}/${formatCount(totalPages)}`;
    return `0 of ${formatCount(totalCount)} · ${pageContext}`;
  }
  const first = position.offset + 1;
  const last = Math.min(totalCount, position.offset + itemCount);
  const range = `${formatCount(first)}-${formatCount(last)} of ${formatCount(totalCount)}`;
  if (position.page === undefined || position.perPage === undefined) return range;
  const totalPages = Math.max(1, Math.ceil(totalCount / position.perPage));
  return `${range} · Page ${formatCount(position.page)}/${formatCount(totalPages)}`;
}

function tagLabels(tags: TagReference[] | undefined, context: ResolvedRenderContext): string {
  if (!tags?.length) return "—";
  return tags.map(tag => colorizeHex(entityLink(tag.name, "tag", tag.id, context), tag.color ?? tag.tagGroupColor, context.color)).join(", ");
}

function entityLinksAvailable(context: ResolvedRenderContext): boolean {
  return !!(context.hyperlinks && context.server && safeHttpTarget(context.server));
}

function linkedEntityCell(value: string, kind: EntityKind, id: number, context: ResolvedRenderContext): string {
  const trailing = / +$/.exec(value)?.[0] ?? "";
  const label = trailing ? value.slice(0, -trailing.length) : value;
  return `${entityLink(label, kind, id, context)}${trailing}`;
}

function linkedEntityColumn<T extends { id: number }>(label: string, width: number, value: (item: T) => string, kind: EntityKind, context: ResolvedRenderContext, decorate?: (value: string, item: T, paint: UiPalette) => string): Column<T> {
  return {
    label,
    width,
    value,
    format: (cell, item, paint) => {
      const linked = linkedEntityCell(cell, kind, item.id, context);
      return decorate?.(linked, item, paint) ?? paint.accent(linked);
    },
  };
}

function withFallbackId<T extends { id: number }>(columns: Column<T>[], context: ResolvedRenderContext): Column<T>[] {
  if (!entityLinksAvailable(context)) columns.push({ label: "ID", width: 6, value: item => String(item.id) });
  return columns;
}

function searchEntityKind(value: string): EntityKind | undefined {
  const normalized = clean(value).toLowerCase();
  return Object.prototype.hasOwnProperty.call(COVE_ENTITY_ROUTES, normalized) ? normalized as EntityKind : undefined;
}

interface StyledText {
  plain: string;
  rendered: string;
}

interface MediaCardRecord {
  id: number;
  title?: string | null;
  displayName?: string | null;
  date?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  performers: PerformerSummary[];
  files: Array<Record<string, unknown> & { basename?: string; duration?: number; width?: number; height?: number }>;
  imageCount?: number;
}

function truncateText(value: string, width: number): string {
  return width <= 0 ? "" : fit(value, width).trimEnd();
}

function validMediaPerformers(item: MediaCardRecord): PerformerSummary[] {
  return (Array.isArray(item.performers) ? item.performers as unknown[] : []).filter((performer): performer is PerformerSummary =>
    !!performer && typeof performer === "object" && typeof (performer as Partial<PerformerSummary>).id === "number" && typeof (performer as Partial<PerformerSummary>).name === "string"
  );
}

function mediaPrimaryMetadata(item: MediaCardRecord, kind: "audio" | "gallery" | "image" | "video", width: number, context: ResolvedRenderContext): StyledText {
  const date = typeof item.date === "string" ? clean(item.date) : "";
  const file = item.files?.[0];
  const seconds = file?.duration;
  const mediaInfo = kind === "gallery"
    ? typeof item.imageCount === "number" && Number.isSafeInteger(item.imageCount) && item.imageCount >= 0 ? formatCount(item.imageCount) : ""
    : kind === "image"
    ? typeof file?.width === "number" && Number.isFinite(file.width) && file.width > 0 && typeof file.height === "number" && Number.isFinite(file.height) && file.height > 0 ? `${file.width}×${file.height}` : ""
    : typeof seconds === "number" && Number.isFinite(seconds) ? duration(seconds) : "";
  const studio = typeof item.studioName === "string" ? clean(item.studioName) : "";
  const separator = " · ";
  const separatorWidth = Bun.stringWidth(separator);
  const parts: Array<{ kind: "date" | "media" | "studio"; label: string }> = [];
  let used = 0;

  if (date && width > 0) {
    const label = truncateText(date, width);
    parts.unshift({ kind: "date", label });
    used = Bun.stringWidth(label);
  }
  if (mediaInfo) {
    const available = width - used - (parts.length ? separatorWidth : 0);
    if (Bun.stringWidth(mediaInfo) <= available) {
      parts.unshift({ kind: "media", label: mediaInfo });
      used += Bun.stringWidth(mediaInfo) + (parts.length > 1 ? separatorWidth : 0);
    }
  }
  if (studio) {
    const available = width - used - (parts.length ? separatorWidth : 0);
    if (available >= (parts.length ? 4 : 1)) {
      const label = truncateText(studio, available);
      parts.unshift({ kind: "studio", label });
    }
  }

  const plain = parts.map(part => part.label).join(separator);
  const rendered = parts.map(part => part.kind === "studio" && typeof item.studioId === "number"
    ? entityLink(part.label, "studio", item.studioId, context)
    : part.label
  ).join(separator);
  return { plain, rendered };
}

function mediaPrimaryLine(item: MediaCardRecord, kind: "audio" | "gallery" | "image" | "video", context: ResolvedRenderContext, paint: UiPalette): string {
  const sourceTitle = typeof item.title === "string" && clean(item.title)
    ? clean(item.title)
    : typeof item.displayName === "string" && clean(item.displayName)
    ? clean(item.displayName)
    : typeof item.files?.[0]?.basename === "string" && clean(item.files[0].basename!)
    ? clean(item.files[0].basename!)
    : `${kind === "video" ? "Video" : kind === "image" ? "Image" : kind === "gallery" ? "Gallery" : "Audio"} ${item.id}`;
  const metadataLimit = Math.max(1, Math.min(Math.floor(context.terminalWidth * 0.48), context.terminalWidth - 10));
  const metadata = mediaPrimaryMetadata(item, kind, metadataLimit, context);
  const metadataWidth = Bun.stringWidth(metadata.plain);
  const titleWidth = Math.max(1, context.terminalWidth - metadataWidth - (metadata.plain ? 2 : 0));
  const title = truncateText(sourceTitle, titleWidth);
  const linkedTitle = entityLink(title, kind, item.id, context);
  if (!metadata.plain) return paint.accent(linkedTitle);
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(title) - metadataWidth));
  return `${paint.accent(linkedTitle)}${gap}${metadata.rendered}`;
}

function mediaPerformerSummary(performers: PerformerSummary[], width: number, context: ResolvedRenderContext): StyledText {
  if (width <= 0 || performers.length === 0) return { plain: "", rendered: "" };
  const labels = performers.map(performer => ({ performer, label: clean(performer.name) || `Performer ${performer.id}` }));
  const renderPrefix = (included: number): StyledText => {
    const omitted = labels.length - included;
    const suffix = omitted > 0 ? ` +${formatCount(omitted)}` : "";
    return {
      plain: `${labels.slice(0, included).map(item => item.label).join(", ")}${suffix}`,
      rendered: `${labels.slice(0, included).map(item => entityLink(item.label, "performer", item.performer.id, context)).join(", ")}${suffix}`,
    };
  };

  let prefixWidth = 0;
  let included = 0;
  for (let index = 0; index < labels.length; index += 1) {
    prefixWidth += (index > 0 ? 2 : 0) + Bun.stringWidth(labels[index]!.label);
    const omitted = labels.length - index - 1;
    const suffixWidth = omitted > 0 ? Bun.stringWidth(` +${formatCount(omitted)}`) : 0;
    if (prefixWidth + suffixWidth <= width) included = index + 1;
  }
  if (included > 0) return renderPrefix(included);

  const suffix = labels.length > 1 ? ` +${formatCount(labels.length - 1)}` : "";
  const labelWidth = width - Bun.stringWidth(suffix);
  if (labelWidth > 0) {
    const label = truncateText(labels[0]!.label, labelWidth);
    return { plain: `${label}${suffix}`, rendered: `${entityLink(label, "performer", labels[0]!.performer.id, context)}${suffix}` };
  }
  const hidden = truncateText(`+${formatCount(labels.length)}`, width);
  return { plain: hidden, rendered: hidden };
}

function mediaSecondaryLine(item: MediaCardRecord, context: ResolvedRenderContext, paint: UiPalette, detail = ""): string {
  const performers = validMediaPerformers(item);
  const id = String(item.id);
  const fullTrailing = detail ? `${detail} · ${id}` : id;
  const compactTrailing = detail ? `${detail.split(" · ")[0]} · ${id}` : id;
  const trailing = Bun.stringWidth(fullTrailing) <= context.terminalWidth - 2 ? fullTrailing : compactTrailing;
  const performerWidth = Math.max(0, context.terminalWidth - Bun.stringWidth(trailing) - 2);
  const performer = mediaPerformerSummary(performers, performerWidth, context);
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(performer.plain) - Bun.stringWidth(trailing)));
  return `${performer.rendered}${gap}${paint.dim(trailing)}`;
}

function renderMediaCards<T extends MediaCardRecord>(
  label: string,
  nouns: [singular: string, plural: string],
  items: T[],
  kind: "audio" | "gallery" | "image" | "video",
  emptyMessage: string,
  context: RenderContext,
  secondaryDetail?: (item: T, index: number) => string,
): string {
  const resolved = resolveRenderContext(context);
  const paint = uiPalette(resolved.color);
  const totalCount = resolved.totalCount ?? items.length;
  const { title, footer } = listChrome(label, nouns, items.length, totalCount, resolved);
  if (items.length === 0) {
    const empty = wrapDetail(emptyMessage, resolved.terminalWidth);
    const hint = wrapDetail("Try changing the filters.", resolved.terminalWidth).map(line => paint.dim(line));
    return `${title}\n\n${[...empty, ...hint].join("\n")}${footer}`;
  }
  const rows = items.flatMap((item, index) => [mediaPrimaryLine(item, kind, resolved, paint), mediaSecondaryLine(item, resolved, paint, secondaryDetail?.(item, index))]);
  return `${title}\n\n${rows.join("\n")}${footer}`;
}

function similarityDetail(distance: number, sectionIndex?: number, startSec?: number, endSec?: number): string {
  const match = `${Math.round((1 - Math.max(0, Math.min(1, distance))) * 100)}% match`;
  if (sectionIndex === undefined || sectionIndex <= 0 || startSec === undefined) return match;
  const range = endSec === undefined ? duration(startSec) : `${duration(startSec)}–${duration(endSec)}`;
  return `${match} · ${range}`;
}

export function renderSimilarVideos(results: SimilarVideoResult[], signal: "visual" | "audio", context: RenderContext = {}): string {
  const label = signal === "visual" ? "Visually similar videos" : "Audio-similar videos";
  return renderMediaCards(label, ["match", "matches"], results.map(result => result.video), "video", "No similar videos found.", context,
    (_video, index) => similarityDetail(results[index]!.distance, results[index]!.sectionIndex, results[index]!.startSec, results[index]!.endSec));
}

export function renderSimilarImages(results: SimilarImageResult[], context: RenderContext = {}): string {
  return renderMediaCards("Visually similar images", ["match", "matches"], results.map(result => result.image), "image", "No similar images found.", context,
    (_image, index) => similarityDetail(results[index]!.distance));
}

export function renderVideos(performer: Performer, videos: Video[], context: RenderContext = {}): string {
  return renderVideoResults(performer.disambiguation ? `${performer.name} (${performer.disambiguation})` : performer.name, videos, context);
}

export function renderVideoResults(label: string, videos: Video[], context: RenderContext = {}): string {
  return renderMediaCards(label, ["match", "matches"], videos, "video", "No matches found.", context);
}

export function renderGlobalSearch(result: GlobalSearchResponse, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const paint = uiPalette(resolved.color);
  const sections = result.groups.map(group => {
    const type = clean(group.type);
    const kind = searchEntityKind(type);
    const title = (item: (typeof group.items)[number]) => item.title || `${type.charAt(0).toUpperCase()}${type.slice(1)} ${item.id}`;
    const contextValue = (item: (typeof group.items)[number]) => item.subtitle ?? "—";
    const columns: Column<(typeof group.items)[number]>[] = [
      kind ? linkedEntityColumn("TITLE", 13, title, kind, resolved) : { label: "TITLE", width: 13, value: title },
      { label: "CONTEXT", width: 7, value: contextValue },
    ];
    if (!kind || !entityLinksAvailable(resolved)) columns.push({ label: "ID", width: 6, value: item => String(item.id) });
    return renderCompactList(`${type.charAt(0).toUpperCase()}${type.slice(1)}`, ["match", "matches"], group.items, group.items.length, columns, resolved);
  });
  if (!sections.length) sections.push("No matching results.");
  if (result.failedTypes.length) {
    sections.push(wrapDetail(`Some searches failed: ${result.failedTypes.map(clean).join(", ")}`, resolved.terminalWidth).map(line => paint.warning(line)).join("\n"));
  }
  return sections.join("\n\n");
}

export function renderLoginSummary(server: string, profile: string, username: string | undefined, context: RenderContext = {}): string {
  return renderStatusCard("Logged in", [["Server", server], ["Profile", profile], ["Account", username ?? "anonymous"]], "success", resolveRenderContext(context));
}

export function renderAuthSummary(server: string, profile: string, version: string | undefined, status: string, authenticated: boolean, context: RenderContext = {}): string {
  return renderStatusCard(`Cove ${version ?? "unknown"}`, [["Server", server], ["Profile", profile], ["Status", status]], authenticated ? "success" : "neutral", resolveRenderContext(context));
}

export function renderLogoutSummary(profile: string, context: RenderContext = {}): string {
  return renderStatusCard("Logged out", [["Profile", profile]], "success", resolveRenderContext(context));
}

export function renderProfiles(items: Array<{ name: string; server: string; default: boolean; authentication: string }>, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const columns: Column<(typeof items)[number]>[] = [
    { label: "PROFILE", width: 14, value: item => `${item.default ? "●" : " "} ${item.name}` },
    { label: "SERVER", width: 12, value: item => item.server },
  ];
  if (resolved.terminalWidth >= 58) columns.push({ label: "AUTH", width: 12, value: item => item.authentication });
  return renderCompactList("Profiles", ["profile", "profiles"], items, items.length, columns, resolved, "Run `cove-cli auth login --server <url>` to add one.");
}

export function renderProfileChange(title: string, profile: string, context: RenderContext = {}): string {
  return renderStatusCard(title, [["Profile", profile]], "success", resolveRenderContext(context));
}

export function renderAudios(audios: Audio[], context: RenderContext = {}): string {
  return renderMediaCards("Audios", ["audio", "audios"], audios, "audio", "No audios found.", context);
}

export function renderImages(images: ImageRecord[], context: RenderContext = {}): string {
  return renderMediaCards("Images", ["image", "images"], images, "image", "No images found.", context);
}

export function renderGalleries(items: GalleryRecord[], context: RenderContext = {}): string {
  return renderMediaCards("Galleries", ["gallery", "galleries"], items, "gallery", "No galleries found.", context);
}

function tagPrimaryLine(item: Tag, context: ResolvedRenderContext, paint: UiPalette): string {
  const sourceName = clean(item.name) || `Tag ${item.id}`;
  const sourceGroup = typeof item.tagGroupName === "string" ? clean(item.tagGroupName) : "";
  const tagColor = typeof item.color === "string" ? item.color : typeof item.tagGroupColor === "string" ? item.tagGroupColor : undefined;
  const groupColor = typeof item.tagGroupColor === "string" ? item.tagGroupColor : undefined;
  const groupLimit = Math.max(1, Math.min(Math.floor(context.terminalWidth * 0.48), context.terminalWidth - 10));
  const group = truncateText(sourceGroup, groupLimit);
  const groupWidth = Bun.stringWidth(group);
  const nameWidth = Math.max(1, context.terminalWidth - groupWidth - (group ? 2 : 0));
  const name = truncateText(sourceName, nameWidth);
  const linkedName = entityLink(name, "tag", item.id, context);
  const renderedName = tagColor ? colorizeHex(linkedName, tagColor, context.color) : paint.accent(linkedName);
  if (!group) return renderedName;
  const renderedGroup = colorizeHex(group, groupColor, context.color);
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(name) - groupWidth));
  return `${renderedName}${gap}${renderedGroup}`;
}

function tagSecondaryLine(item: Tag, context: ResolvedRenderContext): string {
  return summarySecondaryLine([
    countLabel(safeListCount(item.videoCount), "video", "videos"),
    countLabel(safeListCount(item.imageCount), "image", "images"),
    countLabel(safeListCount(item.galleryCount), "gallery", "galleries"),
    countLabel(safeListCount(item.performerCount), "performer", "performers"),
    countLabel(safeListCount(item.studioCount), "studio", "studios"),
  ], item.id, context);
}

export function renderTags(items: Tag[], context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const paint = uiPalette(resolved.color);
  const totalCount = resolved.totalCount ?? items.length;
  const { title, footer } = listChrome("Tags", ["tag", "tags"], items.length, totalCount, resolved);
  if (items.length === 0) {
    const empty = wrapDetail("No tags found.", resolved.terminalWidth);
    const hint = wrapDetail("Try changing the filters.", resolved.terminalWidth).map(line => paint.dim(line));
    return `${title}\n\n${[...empty, ...hint].join("\n")}${footer}`;
  }
  const rows = items.flatMap(item => [tagPrimaryLine(item, resolved, paint), tagSecondaryLine(item, resolved)]);
  return `${title}\n\n${rows.join("\n")}${footer}`;
}

function performerListLabel(item: Performer): string {
  const name = clean(item.name) || `Performer ${item.id}`;
  const disambiguation = typeof item.disambiguation === "string" ? clean(item.disambiguation) : "";
  return disambiguation ? `${name} (${disambiguation})` : name;
}

function performerPrimaryLine(item: Performer, context: ResolvedRenderContext, paint: UiPalette): string {
  const sourceName = performerListLabel(item);
  const birthdate = typeof item.birthdate === "string" ? clean(item.birthdate) : "";
  const country = typeof item.country === "string" ? clean(item.country) : "";
  const metadataLimit = Math.max(1, Math.min(Math.floor(context.terminalWidth * 0.48), context.terminalWidth - 10));
  const metadata = birthdate
    ? country && Bun.stringWidth(`${country} · ${birthdate}`) <= metadataLimit ? `${country} · ${birthdate}` : truncateText(birthdate, metadataLimit)
    : truncateText(country, metadataLimit);
  const metadataWidth = Bun.stringWidth(metadata);
  const nameWidth = Math.max(1, context.terminalWidth - metadataWidth - (metadata ? 2 : 0));
  const name = truncateText(sourceName, nameWidth);
  const linkedName = entityLink(name, "performer", item.id, context);
  if (!metadata) return paint.accent(linkedName);
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(name) - metadataWidth));
  return `${paint.accent(linkedName)}${gap}${metadata}`;
}

function safeListCount(value: unknown): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

function countLabel(value: number, singular: string, plural: string): string {
  return `${formatCount(value)} ${value === 1 ? singular : plural}`;
}

function summarySecondaryLine(parts: string[], idValue: number, context: ResolvedRenderContext): string {
  const showId = !entityLinksAvailable(context);
  const id = showId ? String(idValue) : "";
  const available = Math.max(1, context.terminalWidth - (showId ? Bun.stringWidth(id) + 2 : 0));
  const visible: string[] = [];
  for (const part of parts) {
    const candidate = [...visible, part].join(" · ");
    if (Bun.stringWidth(candidate) > available) break;
    visible.push(part);
  }
  const summary = visible.length ? visible.join(" · ") : truncateText(parts[0] ?? "", available);
  if (!showId) return summary;
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(summary) - Bun.stringWidth(id)));
  return `${summary}${gap}${id}`;
}

function performerSecondaryLine(item: Performer, context: ResolvedRenderContext): string {
  return summarySecondaryLine([
    countLabel(Array.isArray(item.tags) ? item.tags.length : 0, "tag", "tags"),
    countLabel(safeListCount(item.videoCount), "video", "videos"),
    countLabel(safeListCount(item.imageCount), "image", "images"),
    countLabel(safeListCount(item.galleryCount), "gallery", "galleries"),
    countLabel(safeListCount(item.audioCount), "audio", "audios"),
    countLabel(safeListCount(item.textCount), "text", "texts"),
    countLabel(safeListCount(item.likeCount), "like", "likes"),
  ], item.id, context);
}

function studioPrimaryLine(item: StudioRecord, context: ResolvedRenderContext, paint: UiPalette): string {
  const sourceName = clean(item.name) || `Studio ${item.id}`;
  const sourceParent = typeof item.parentName === "string" ? clean(item.parentName) : "";
  const parentLimit = Math.max(1, Math.min(Math.floor(context.terminalWidth * 0.48), context.terminalWidth - 10));
  const parent = truncateText(sourceParent, parentLimit);
  const parentWidth = Bun.stringWidth(parent);
  const nameWidth = Math.max(1, context.terminalWidth - parentWidth - (parent ? 2 : 0));
  const name = truncateText(sourceName, nameWidth);
  const linkedName = entityLink(name, "studio", item.id, context);
  if (!parent) return paint.accent(linkedName);
  const renderedParent = typeof item.parentId === "number" && Number.isSafeInteger(item.parentId) && item.parentId > 0 ? entityLink(parent, "studio", item.parentId, context) : parent;
  const gap = " ".repeat(Math.max(2, context.terminalWidth - Bun.stringWidth(name) - parentWidth));
  return `${paint.accent(linkedName)}${gap}${renderedParent}`;
}

function studioSecondaryLine(item: StudioRecord, context: ResolvedRenderContext): string {
  return summarySecondaryLine([
    countLabel(Array.isArray(item.tags) ? item.tags.length : 0, "tag", "tags"),
    countLabel(safeListCount(item.videoCount), "video", "videos"),
    countLabel(safeListCount(item.imageCount), "image", "images"),
    countLabel(safeListCount(item.galleryCount), "gallery", "galleries"),
    countLabel(safeListCount(item.childStudioCount), "child", "children"),
  ], item.id, context);
}

export function renderPerformers(items: Performer[], context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const paint = uiPalette(resolved.color);
  const totalCount = resolved.totalCount ?? items.length;
  const { title, footer } = listChrome("Performers", ["performer", "performers"], items.length, totalCount, resolved);
  if (items.length === 0) {
    const empty = wrapDetail("No performers found.", resolved.terminalWidth);
    const hint = wrapDetail("Try changing the filters.", resolved.terminalWidth).map(line => paint.dim(line));
    return `${title}\n\n${[...empty, ...hint].join("\n")}${footer}`;
  }
  const rows = items.flatMap(item => [performerPrimaryLine(item, resolved, paint), performerSecondaryLine(item, resolved)]);
  return `${title}\n\n${rows.join("\n")}${footer}`;
}

export function renderPerformer(item: Performer, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const label = item.disambiguation ? `${item.name} (${item.disambiguation})` : item.name;
  const heading = entityLink(label, "performer", item.id, resolved);
  const metadata = [`#${item.id}`, item.gender, item.country].filter((value): value is string => !!value).join(" · ");
  return renderSectionedDetail(heading, metadata, [
    { heading: "Overview", values: [
      ["ID", String(item.id)], ["Birthdate", item.birthdate ?? undefined], ["Death date", item.deathDate ?? undefined], ["Ethnicity", item.ethnicity ?? undefined],
      ["Eye color", item.eyeColor ?? undefined], ["Hair color", item.hairColor ?? undefined], ["Height", item.heightCm == null ? undefined : `${item.heightCm} cm`],
      ["Weight", item.weight == null ? undefined : String(item.weight)], ["Measurements", item.measurements ?? undefined], ["Fake tits", item.fakeTits ?? undefined],
      ["Penis length", item.penisLength == null ? undefined : String(item.penisLength)], ["Circumcised", item.circumcised ?? undefined],
      ["Career", item.careerStart || item.careerEnd ? `${item.careerStart ?? "?"} – ${item.careerEnd ?? "present"}` : undefined],
      ["Tattoos", item.tattoos ?? undefined], ["Piercings", item.piercings ?? undefined], ["Favorite", item.favorite === undefined ? undefined : item.favorite ? "yes" : "no"],
      ["Created", item.createdAt], ["Updated", item.updatedAt],
    ] },
    { heading: "Library", values: [
      ["Aliases", item.aliases.length ? item.aliases.join(", ") : "—"], ["Tags", tagLabels(item.tags, resolved), true],
      ["Videos", item.videoCount === undefined ? undefined : String(item.videoCount)], ["Images", item.imageCount === undefined ? undefined : String(item.imageCount)],
      ["Galleries", item.galleryCount === undefined ? undefined : String(item.galleryCount)], ["Groups", item.groupCount === undefined ? undefined : String(item.groupCount)],
      ["Audios", item.audioCount === undefined ? undefined : String(item.audioCount)], ["Texts", item.textCount === undefined ? undefined : String(item.textCount)],
      ["Faces", item.faceCount === undefined ? undefined : String(item.faceCount)], ["Likes", item.likeCount === undefined ? undefined : String(item.likeCount)],
    ] },
    { heading: "Links and data", values: [
      ["URLs", item.urls?.length ? item.urls.map(url => linkedUrl(url, resolved)).join("\n") : undefined, true],
      ["Remote IDs", remoteIdLabels(item.remoteIds, resolved, "performer"), true],
      ["Custom fields", item.customFields && Object.keys(item.customFields).length ? JSON.stringify(item.customFields) : undefined],
    ] },
    { heading: "Details", values: [["Text", item.details ?? undefined]] },
  ], resolved, true);
}

export function renderVideo(item: Video, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const file = item.files[0];
  const heading = entityLink(item.title || file?.basename || `Video ${item.id}`, "video", item.id, resolved);
  const metadata = [
    `#${item.id}`,
    item.date,
    typeof file?.duration === "number" && Number.isFinite(file.duration) ? duration(file.duration) : undefined,
    resolution(file) === "—" ? undefined : resolution(file),
    item.studioName,
  ].filter((value): value is string => !!value).join(" · ");
  return renderSectionedDetail(heading, metadata, [
    { heading: "Overview", values: [
      ["Code", item.code ?? undefined], ["Director", item.director ?? undefined], ["Captions", item.captions ?? undefined],
      ["Organized", item.organized === undefined ? undefined : item.organized ? "yes" : "no"], ["VR", item.isVr === undefined ? undefined : item.isVr ? "yes" : "no"],
      ["Created", item.createdAt], ["Updated", item.updatedAt],
    ] },
    { heading: "Library", values: [
      ["Performers", item.performers.length ? item.performers.map(performer => entityLink(performer.name, "performer", performer.id, resolved)).join(", ") : "—", true],
      ["Tags", tagLabels(item.tags, resolved), true],
      ["Groups", item.groups?.length ? item.groups.map(group => typeof group.id === "number" ? entityLink(groupLabel(group), "group", group.id, resolved) : groupLabel(group)).join("\n") : "—", true],
      ["Galleries", item.galleries?.length ? item.galleries.map(gallery => typeof gallery.id === "number" ? entityLink(namedLabel(gallery, "Gallery"), "gallery", gallery.id, resolved) : namedLabel(gallery, "Gallery")).join("\n") : "—", true],
      ["Parent video", item.parentVideoId == null ? undefined : `${entityLink(item.parentVideoTitle ?? "Untitled", "video", item.parentVideoId, resolved)} (#${item.parentVideoId})`, true],
      ["Clip range", item.clipStartSec == null && item.clipEndSec == null ? undefined : `${item.clipStartSec ?? 0}–${item.clipEndSec ?? "end"} sec`],
      ["Child videos", item.childVideoCount === undefined ? undefined : String(item.childVideoCount)],
    ] },
    { heading: "Files and links", values: [
      ["Files", item.files.length ? item.files.map(mediaFile => mediaFile.path ?? mediaFile.basename ?? "Unknown file").join("\n") : "—"],
      ["URLs", item.urls?.length ? item.urls.map(url => linkedUrl(url, resolved)).join("\n") : undefined, true],
      ["Remote IDs", remoteIdLabels(item.remoteIds, resolved, "video"), true],
      ["Custom fields", item.customFields && Object.keys(item.customFields).length ? JSON.stringify(item.customFields) : undefined],
    ] },
    { heading: "Details", values: [["Text", item.details ? markdownLinks(item.details, resolved) : undefined, true]] },
  ], resolved, true);
}

export function renderAudio(item: Audio, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const file = item.files[0];
  const heading = entityLink(item.title || file?.basename || `Audio ${item.id}`, "audio", item.id, resolved);
  const metadata = [
    `#${item.id}`, item.date, typeof file?.duration === "number" ? duration(file.duration) : undefined, file?.format, item.studioName,
  ].filter((value): value is string => !!value).join(" · ");
  return renderSectionedDetail(heading, metadata, [
    { heading: "Overview", values: [
      ["ID", String(item.id)], ["Code", item.code ?? undefined], ["Organized", item.organized === undefined ? undefined : item.organized ? "yes" : "no"],
      ["Has video files", item.hasVideoFiles === undefined ? undefined : item.hasVideoFiles ? "yes" : "no"], ["File count", item.fileCount === undefined ? undefined : String(item.fileCount)],
      ["Max duration", item.maxDuration === undefined ? undefined : duration(item.maxDuration)], ["Created", item.createdAt], ["Updated", item.updatedAt],
    ] },
    { heading: "Library", values: [
      ["Performers", item.performers.length ? item.performers.map(performer => entityLink(performer.name, "performer", performer.id, resolved)).join(", ") : "—", true],
      ["Tags", tagLabels(item.tags, resolved), true],
      ["Groups", item.groups?.length ? item.groups.map(group => typeof group.id === "number" ? entityLink(groupLabel(group), "group", group.id, resolved) : groupLabel(group)).join("\n") : "—", true], ["Tracks", item.tracks.length ? item.tracks.map((track, index) => namedLabel(track, `Track ${index + 1}`)).join("\n") : "—"],
    ] },
    { heading: "Files and links", values: [
      ["Files", item.files.length ? item.files.map(mediaFile => mediaFile.path ?? mediaFile.basename ?? "Unknown file").join("\n") : "—"],
      ["URLs", item.urls?.length ? item.urls.map(url => linkedUrl(url, resolved)).join("\n") : undefined, true],
      ["Custom fields", item.customFields && Object.keys(item.customFields).length ? JSON.stringify(item.customFields) : undefined],
    ] },
    { heading: "Details", values: [["Text", item.details ? markdownLinks(item.details, resolved) : undefined, true]] },
  ], resolved, true);
}

export function renderCatalogDetail(item: ImageRecord | GalleryRecord | Tag | StudioRecord | GroupRecord | TextRecord | SegmentRecord, kind: CatalogEntityKind, context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const record = item as Record<string, unknown>;
  const displayKind = `${kind.charAt(0).toUpperCase()}${kind.slice(1)}`;
  const label = clean(typeof record.name === "string" ? record.name : typeof record.title === "string" && record.title ? record.title : `${displayKind} ${item.id}`);
  const heading = entityLink(label, kind, item.id, resolved);
  const scalar = (key: string) => typeof record[key] === "string" || typeof record[key] === "number" ? String(record[key]) : undefined;
  const entities = (key: string, entityKind: EntityKind) => Array.isArray(record[key]) ? (record[key] as unknown[]).filter(isObjectRecord).map(value => {
    const entityLabel = namedLabel(value, `${entityKind.charAt(0).toUpperCase()}${entityKind.slice(1)}`);
    return typeof value.id === "number" ? entityLink(entityLabel, entityKind, value.id, resolved) : entityLabel;
  }).join(", ") || "—" : undefined;
  const tags = Array.isArray(record.tags)
    ? tagLabels((record.tags as unknown[]).filter((value): value is TagReference => isObjectRecord(value) && typeof value.id === "number" && typeof value.name === "string"), resolved)
    : undefined;
  const files = Array.isArray(record.files) ? (record.files as unknown[]).filter(isObjectRecord).map(value => scalarFrom(value, "path") ?? scalarFrom(value, "basename") ?? "Unknown file").join("\n") || "—" : undefined;
  const strings = (key: string) => Array.isArray(record[key]) ? (record[key] as unknown[]).filter(value => typeof value === "string").join(", ") || "—" : undefined;
  const metadata = [`#${item.id}`, scalar("date"), scalar("kind"), scalar("studioName")].filter((value): value is string => !!value).join(" · ");
  return renderSectionedDetail(heading, metadata, [
    { heading: "Overview", values: [
      ["ID", String(item.id)], ["Aliases", strings("aliases") ?? scalar("aliases")], ["Code", scalar("code")], ["Tag group", scalar("tagGroupName")], ["Color", scalar("color") ?? scalar("tagGroupColor")],
      ["Host type", scalar("hostType")], ["Host", scalar("hostTitle") ?? scalar("hostId")], ["Start", scalar("startSec")], ["End", scalar("endSec")],
      ["Source", scalar("sourceKey")], ["Confidence", scalar("confidence")], ["Tag", scalar("tagName")], ["Performer", scalar("performerName")],
      ["Photographer", scalar("photographer")], ["Director", scalar("director")], ["Organized", typeof record.organized === "boolean" ? record.organized ? "yes" : "no" : undefined],
      ["Created", scalar("createdAt")], ["Updated", scalar("updatedAt")],
    ] },
    { heading: "Library", values: [
      ["Performers", entities("performers", "performer"), true], ["Tags", tags, true], ["Groups", entities("groups", "group"), true], ["Galleries", entities("galleries", "gallery"), true],
      ["Items", scalar("itemCount")], ["Videos", scalar("videoCount")], ["Images", scalar("imageCount")], ["Galleries count", scalar("galleryCount")],
      ["Audios", scalar("audioCount")], ["Texts", scalar("textCount")], ["Words", scalar("maxWordCount")], ["Pages", scalar("maxPageCount")],
    ] },
    { heading: "Files and links", values: [
      ["Image URL", kind === "image" && resolved.server ? linkedUrl(`api/stream/image/${item.id}`, resolved) : undefined, true],
      ["Files", files],
      ["URLs", Array.isArray(record.urls) ? (record.urls as unknown[]).filter((value): value is string => typeof value === "string").map(url => linkedUrl(url, resolved)).join("\n") || "—" : undefined, true],
    ] },
    { heading: "Details", values: [["Text", scalar("details") ?? scalar("description")]] },
  ], resolved, true);
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function scalarFrom(record: Record<string, unknown>, key: string): string | undefined {
  return typeof record[key] === "string" || typeof record[key] === "number" ? String(record[key]) : undefined;
}

export function renderGroups(items: GroupRecord[], context: RenderContext = {}): string {
  return renderSimpleCatalog("Groups", ["group", "groups"], items, item => item.name || `Group ${item.id}`, item => String(item.itemCount ?? 0), "Items", "group", context);
}

export function renderGroupItems(items: GroupItem[], context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const title = (item: GroupItem): string => {
    const label = item.title || item.videoTitle || item.imageTitle || item.childGroupName || `${item.kind} ${item.hostId}`;
    if (item.startSec == null && item.endSec == null) return label;
    return `${label} · ${item.startSec == null ? "start" : duration(item.startSec)}–${item.endSec == null ? "end" : duration(item.endSec)}`;
  };
  const positioned = items.map(item => ({ item, position: item.orderIndex + 1 }));
  const columns: Column<(typeof positioned)[number]>[] = [
    { label: "Position", width: 8, value: entry => String(entry.position) },
    { label: "Title", width: 14, value: entry => title(entry.item) },
  ];
  if (resolved.terminalWidth >= 52) columns.push({ label: "Kind", width: 10, value: entry => entry.item.kind });
  if (resolved.terminalWidth >= 72) columns.push({ label: "Host", width: 12, value: entry => `${entry.item.hostType} #${entry.item.hostId}` });
  columns.push({ label: "Group item ID", width: 13, value: entry => String(entry.item.id) });
  return renderCompactList("Group items", ["item", "items"], positioned, positioned.length, columns, resolved, "This group has no items.");
}

export function renderTexts(items: TextRecord[], context: RenderContext = {}): string {
  return renderSimpleCatalog("Texts", ["text", "texts"], items, item => item.title || item.files[0]?.basename || `Text ${item.id}`, item => String(item.maxWordCount ?? 0), "Words", "text", context);
}

export function renderSegments(items: SegmentRecord[], context: RenderContext = {}): string {
  return renderSimpleCatalog("Segments", ["segment", "segments"], items, item => item.title || item.tagName || item.kind || `Segment ${item.id}`, item => `${item.startSec}–${item.endSec ?? item.startSec}`, "Seconds", "segment", context);
}

function renderSimpleCatalog<T extends { id: number }>(heading: string, nouns: [string, string], items: T[], label: (item: T) => string, extra: (item: T) => string, extraLabel: string, kind: EntityKind, context: RenderContext): string {
  const resolved = resolveRenderContext(context);
  const columns: Column<T>[] = [linkedEntityColumn("NAME", 12, label, kind, resolved)];
  if (resolved.terminalWidth >= 38) columns.push({ label: extraLabel.toUpperCase(), width: 12, value: extra });
  return renderCompactList(heading, nouns, items, resolved.totalCount ?? items.length, withFallbackId(columns, resolved), resolved);
}

function renderStatusCard(
  title: string,
  values: Array<[string, string]>,
  tone: "success" | "neutral",
  context: ResolvedRenderContext,
): string {
  const paint = uiPalette(context.color);
  const width = context.terminalWidth;
  const labelWidth = Math.max(...values.map(([label]) => label.length));
  const valueWidth = Math.max(8, width - labelWidth - 4);
  const marker = tone === "success" ? paint.success("✓") : paint.accent("•");
  const lines = wrapDetail(clean(title), width - 2).map((line, index) => `${index === 0 ? `${marker} ` : "  "}${paint.bold(line)}`);
  lines.push(...values.flatMap(([label, value]) => wrapDetail(value, valueWidth).map((part, index) =>
    `${index === 0 ? `  ${paint.dim(label.padEnd(labelWidth))}  ` : " ".repeat(labelWidth + 4)}${part}`
  )));
  return lines.join("\n");
}

function renderSectionedDetail(
  heading: string,
  metadata: string,
  sections: Array<{ heading: string; values: Array<[string, string | undefined, boolean?]> }>,
  context: ResolvedRenderContext,
  trustedHeading = false,
): string {
  const paint = uiPalette(context.color);
  const width = context.terminalWidth;
  const lines = wrapDetail(heading, width, trustedHeading).map(line => paint.bold(line));
  if (metadata) lines.push(...wrapDetail(metadata, width).map(line => paint.dim(line)));
  for (const section of sections) {
    const visible = section.values.filter((entry): entry is [string, string, boolean?] => entry[1] !== undefined);
    if (visible.length === 0) continue;
    const labelWidth = Math.max(...visible.map(([label]) => label.length));
    const valueWidth = Math.max(8, width - labelWidth - 4);
    lines.push("", paint.accent(clean(section.heading)));
    lines.push(...visible.flatMap(([label, value, trustedLinks]) => wrapDetail(value, valueWidth, trustedLinks).map((part, index) =>
      `${index === 0 ? `  ${paint.dim(label.padEnd(labelWidth))}  ` : " ".repeat(labelWidth + 4)}${part}`
    )));
  }
  return lines.join("\n");
}

function wrapDetail(value: string, width: number, trustedTerminalSequences = false): string[] {
  return value.replace(/\r\n?/g, "\n").split("\n").flatMap(source => {
    const sequences: string[] = [];
    const protect = (sequence: string) => `\uE000${sequences.push(sequence) - 1}\uE001`;
    const protectedSource = trustedTerminalSequences
      ? source.replace(OSC_8_LINK, protect).replace(SAFE_COLOR, protect)
      : source;
    const normalized = clean(protectedSource).replace(/\uE000(\d+)\uE001/g, (_match, index: string) => sequences[Number(index)] ?? "");
    if (!normalized) return [""];
    const lines: string[] = [];
    const tokens = normalized.match(new RegExp(`${OSC_8_LINK.source}|\\s+|[^\\s]+`, "g")) ?? [];
    let line = "";
    let pendingSpace = false;
    for (const token of tokens) {
      const space = /^\s+$/.test(token);
      if (space) {
        pendingSpace = !!line;
        continue;
      }
      const separator = pendingSpace ? " " : "";
      pendingSpace = false;
      if (visibleWidth(line + separator + token) <= width) {
        line += separator + token;
        continue;
      }
      if (line.trimEnd()) lines.push(line.trimEnd());
      line = "";
      if (visibleWidth(token) <= width) {
        line = token;
        continue;
      }
      const plain = visibleText(token);
      for (const { segment } of graphemeSegmenter.segment(plain)) {
        if (Bun.stringWidth(line + segment) > width && line) {
          lines.push(line);
          line = "";
        }
        line += segment;
      }
    }
    if (line || lines.length === 0) lines.push(line.trimEnd());
    return lines;
  });
}

const OSC_8_LINK = /\u001b]8;;[^\u0007]*\u0007[^\u001b]*\u001b]8;;\u0007/g;
const SAFE_COLOR = /\u001b\[(?:38;2;\d{1,3};\d{1,3};\d{1,3}|39)m/g;

function visibleText(value: string): string {
  return value
    .replace(OSC_8_LINK, link => link.replace(/^\u001b]8;;[^\u0007]*\u0007/, "").replace(/\u001b]8;;\u0007$/, ""))
    .replace(SAFE_COLOR, "");
}

function visibleWidth(value: string): number {
  return Bun.stringWidth(visibleText(value));
}

function namedLabel(item: Record<string, unknown>, fallback: string): string {
  for (const key of ["name", "title"]) if (typeof item[key] === "string" && item[key]) return clean(item[key]);
  return fallback;
}

function groupLabel(group: Record<string, unknown>): string {
  const label = namedLabel(group, "Unnamed group");
  const index = typeof group.videoIndex === "number" ? group.videoIndex : typeof group.audioIndex === "number" ? group.audioIndex : undefined;
  return index === undefined ? label : `${label} (index ${index})`;
}

function entityLink(label: string, kind: EntityKind, id: number, context: ResolvedRenderContext): string {
  return terminalLink(label, absoluteUrl(`${COVE_ENTITY_ROUTES[kind]}/${id}`, context.server), entityLinksAvailable(context));
}

function linkedUrl(value: string, context: ResolvedRenderContext): string {
  const url = absoluteUrl(value, context.server);
  return terminalLink(urlWithoutCredentials(url), url, context.hyperlinks);
}

function metadataServerLabel(endpoint: string, servers: MetadataServerSummary[]): string {
  const normalized = endpoint.trim().replace(/\/$/, "").toLowerCase();
  const configured = servers.find(server => server.endpoint.trim().replace(/\/$/, "").toLowerCase() === normalized)?.name?.trim();
  if (configured) return clean(configured);
  try {
    return new URL(endpoint).hostname.replace(/^www\./i, "");
  } catch {
    return "metadata server";
  }
}

function metadataServerEntityUrl(endpoint: string, entityType: "scenes" | "performers" | "studios" | "tags", remoteId: string): string | undefined {
  try {
    const url = new URL(endpoint);
    if (url.protocol !== "http:" && url.protocol !== "https:") return undefined;
    if (url.username || url.password) return undefined;
    const graphqlPath = url.pathname.match(/^(.*\/)?graphql\/?$/i);
    if (!graphqlPath) return undefined;
    url.pathname = `${graphqlPath[1] ?? "/"}${entityType}/${encodeURIComponent(remoteId)}`;
    url.search = "";
    url.hash = "";
    return url.toString();
  } catch {
    return undefined;
  }
}

function remoteIdLabels(remoteIds: RemoteId[] | undefined, context: ResolvedRenderContext, entityType: keyof typeof METADATA_ENTITY_ROUTES): string {
  const labels = (Array.isArray(remoteIds) ? remoteIds : []).flatMap(remoteId => {
    if (!remoteId || typeof remoteId !== "object" || typeof remoteId.endpoint !== "string" || typeof remoteId.remoteId !== "string") return [];
    const label = `${metadataServerLabel(remoteId.endpoint, context.metadataServers)} · ${clean(remoteId.remoteId)}`;
    const url = metadataServerEntityUrl(remoteId.endpoint, METADATA_ENTITY_ROUTES[entityType], remoteId.remoteId);
    return [url ? terminalLink(label, url, context.hyperlinks) : label];
  });
  return labels.length ? labels.join("\n") : "—";
}

function absoluteUrl(value: string, server?: string): string {
  try {
    const base = server && !server.endsWith("/") ? `${server}/` : server;
    return new URL(value, base).href;
  } catch {
    return clean(value);
  }
}

function safeHttpTarget(value: string): string | undefined {
  if (/[\u0000-\u001f\u007f-\u009f]/.test(value)) return undefined;
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") && !url.username && !url.password ? value : undefined;
  } catch {
    return undefined;
  }
}

function urlWithoutCredentials(value: string): string {
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") return clean(value);
    url.username = "";
    url.password = "";
    return clean(url.href);
  } catch {
    return clean(value);
  }
}

function terminalLink(label: string, target: string, hyperlinks: boolean): string {
  const safeLabel = clean(label);
  const safeTarget = safeHttpTarget(target);
  return hyperlinks && safeTarget ? `\u001b]8;;${safeTarget}\u0007${safeLabel}\u001b]8;;\u0007` : safeLabel;
}

function markdownLinks(value: string, context: ResolvedRenderContext): string {
  const sanitized = stripTerminalSequences(value).replace(/[\u0000-\u0009\u000b\u000c\u000e-\u001f\u007f-\u009f]/g, "");
  return sanitized.replace(/\[([^\]\n]+)]\(([^)\s]+)\)/g, (_match, label: string, target: string) => {
    const url = absoluteUrl(target, context.server);
    const safeLabel = clean(label);
    return context.hyperlinks && safeHttpTarget(url) ? terminalLink(safeLabel, url, true) : `${safeLabel} (${urlWithoutCredentials(url)})`;
  });
}

export function renderStudios(items: StudioRecord[], context: RenderContext = {}): string {
  const resolved = resolveRenderContext(context);
  const paint = uiPalette(resolved.color);
  const totalCount = resolved.totalCount ?? items.length;
  const { title, footer } = listChrome("Studios", ["studio", "studios"], items.length, totalCount, resolved);
  if (items.length === 0) {
    const empty = wrapDetail("No studios found.", resolved.terminalWidth);
    const hint = wrapDetail("Try changing the filters.", resolved.terminalWidth).map(line => paint.dim(line));
    return `${title}\n\n${[...empty, ...hint].join("\n")}${footer}`;
  }
  const rows = items.flatMap(item => [studioPrimaryLine(item, resolved, paint), studioSecondaryLine(item, resolved)]);
  return `${title}\n\n${rows.join("\n")}${footer}`;
}

export function renderSavedFilters(filters: SavedFilter[], context: RenderContext = {}, title = "Saved filters"): string {
  const parsed = filters.map(filter => ({ filter, find: displayObject(filter.findFilter), object: displayObject(filter.objectFilter) }));
  type DisplayFilter = (typeof parsed)[number];
  const resolved = resolveRenderContext(context);
  const columns: Column<DisplayFilter>[] = [
    { label: "NAME", width: 10, value: item => item.filter.name },
  ];
  if (resolved.terminalWidth >= 54) columns.push({ label: "SEARCH", width: 18, value: item => item.find.invalid ? "invalid JSON" : typeof item.find.value.q === "string" && item.find.value.q ? item.find.value.q : "—" });
  if (resolved.terminalWidth >= 78) columns.push({ label: "SORT", width: 18, value: item => item.find.invalid ? "invalid JSON" : typeof item.find.value.sort === "string" ? `${item.find.value.sort} ${item.find.value.direction === "asc" ? "asc" : "desc"}` : `${savedFilterDefaultSort(item.filter.mode)} desc` });
  columns.push({ label: "CRITERIA", width: 10, value: item => item.object.invalid ? "invalid JSON" : String(Object.keys(item.object.value).length) });
  columns.push({ label: "ID", width: 6, value: item => String(item.filter.id) });
  return renderCompactList(title, ["filter", "filters"], parsed, filters.length, columns, resolved, "Create a saved filter in Cove first.");
}

function displayObject(raw: string | null | undefined): { value: Record<string, unknown>; invalid: boolean } {
  if (!raw) return { value: {}, invalid: false };
  try {
    const value: unknown = JSON.parse(raw);
    return value && typeof value === "object" && !Array.isArray(value)
      ? { value: value as Record<string, unknown>, invalid: false }
      : { value: {}, invalid: true };
  } catch {
    return { value: {}, invalid: true };
  }
}
