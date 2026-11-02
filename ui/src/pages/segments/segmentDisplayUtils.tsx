import { type ReactNode } from "react";
import type { ResolvedSpan, SegmentSpanOperator, SegmentSpanReference } from "../../api/types";
import type { SegmentTileItem } from "../../components/EntityCards";
import { SegmentPreviewMedia } from "../../components/SegmentPreviewMedia";
import { formatDate } from "../../components/shared";
import { faceStoredName } from "../../utils/faceDisplay";
import { formatSegmentTime } from "../../utils/segmentTime";
import { formatSourceKeyLabel } from "../../utils/sourceKeyLabel";
import { formatOperatorLabel } from "./derivedQueryCriterion";
import type { DerivedSpanItem, RawSegmentItem } from "./types";
import type { ListedFaceName } from "./useListedFaceNames";

export function Pill({ children }: { children: ReactNode }) {
  return <span className="inline-flex items-center rounded-full bg-surface px-2 py-1 text-secondary">{children}</span>;
}

export function SegmentVideoPreview({
  hostId,
  updatedAt,
  startSec,
  endSec,
  title,
  imgClassName,
  segmentId,
}: {
  hostId: number;
  updatedAt?: string;
  startSec?: number;
  endSec?: number;
  title: string;
  imgClassName: string;
  segmentId?: number;
}) {
  return (
    <SegmentPreviewMedia
      hostId={hostId}
      segmentId={segmentId}
      updatedAt={updatedAt}
      startSec={startSec}
      endSec={endSec}
      title={title}
      className={imgClassName}
    />
  );
}

export function buildSpanTitle(span: ResolvedSpan, videoTitle?: string, reference?: SegmentSpanReference) {
  return (
    formatSpanReferenceName(reference) ||
    span.tagName ||
    span.kind ||
    span.sourceKey ||
    videoTitle ||
    `Span ${span.spanKey}`
  );
}

// A span's tile shows its tag only for tag spans: other spans carry their segments' stored title in tagName,
// which the reference replaces. A derived-query span is named after its query, so its operator kind and
// internal "derived" source say nothing more.
export function buildSpanTileSegment(item: DerivedSpanItem, title: string): SegmentTileItem {
  const derived = item.kind === "derivedQuery";
  return {
    id: item.coverSegmentId ?? item.span.segmentIds[0] ?? item.id,
    hostType: "video",
    hostId: item.videoId,
    startSec: item.span.startSec,
    endSec: item.span.endSec,
    tagName: item.span.tagId != null ? item.span.tagName : undefined,
    kind: derived ? undefined : item.span.kind,
    sourceKey: derived ? undefined : item.span.sourceKey,
    title,
    updatedAt: item.coverVersion ?? item.videoUpdatedAt,
    hostTitle: item.videoTitle,
    refLabel: item.reference?.faceSourceKey || item.reference?.faceLabel,
    confidence: item.confidence,
  };
}

// A face span reads as its performer or its own name; a face still carrying only its placeholder label is
// "Unnamed face", with the placeholder shown as its detail instead.
export function formatSpanReferenceName(reference?: SegmentSpanReference) {
  if (!reference) return undefined;
  if (reference.faceId == null) return reference.performerName;
  return referenceFaceName(reference, reference.faceId) ?? "Unnamed face";
}

// The face a span names when that face has no name of its own, to look up its suggested performer.
export function spanUnnamedFaceId(reference?: SegmentSpanReference) {
  if (reference?.faceId == null) return undefined;
  return referenceFaceName(reference, reference.faceId) ? undefined : reference.faceId;
}

function referenceFaceName(reference: SegmentSpanReference, faceId: number) {
  return faceStoredName({
    id: faceId,
    label: reference.faceLabel,
    primarySourceKey: reference.faceSourceKey,
    performerId: reference.performerId,
    performerName: reference.performerName,
  });
}

// A span's title: a derived span's query in words, else its face, performer or tag, with a face the span names
// only by its placeholder read by its looked-up name, which may be its suggested performer.
export function buildSpanDisplayTitle(
  item: DerivedSpanItem,
  nameMaps?: DerivedOperandNameMaps,
  faceNames?: ReadonlyMap<number, ListedFaceName>,
) {
  const derivedName = formatDerivedQueryName(item.derivedQueryDescriptor, nameMaps);
  const unnamedFaceId = spanUnnamedFaceId(item.reference);
  const faceName = unnamedFaceId == null ? undefined : faceNames?.get(unnamedFaceId);
  return {
    derivedName,
    title: derivedName ?? faceName?.name ?? buildSpanTitle(item.span, item.videoTitle, item.reference),
    titleIsSuggestion: !derivedName && !!faceName?.suggested,
  };
}

export function buildRawSegmentTitle(segment: RawSegmentItem) {
  return (
    segment.title?.trim() ||
    segment.tagName ||
    segment.performerName ||
    segment.refLabel ||
    segment.kind ||
    formatSourceLabel(segment.sourceKey)
  );
}

export function formatSourceLabel(sourceKey?: string) {
  if (!sourceKey) {
    return "Unknown source";
  }

  if (sourceKey === "user") {
    return "User";
  }

  return sourceKey.startsWith("ext:") ? formatIdentifierWords(sourceKey.slice(4)) : sourceKey;
}

export function formatKindLabel(kind: string) {
  return formatIdentifierWords(kind) || kind;
}

function formatIdentifierWords(value: string) {
  return value
    .split(/[._-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

export function formatSpanItemKindLabel(item: DerivedSpanItem) {
  return item.kind === "derivedQuery"
    ? `Derived ${formatOperatorLabel(item.derivedQueryDescriptor?.operator ?? "intersection")}`
    : "Profile";
}

export interface DerivedOperandNameMaps {
  tagNamesById?: Map<number, string> | Record<number, string>;
  performerNamesById?: Map<number, string> | Record<number, string>;
  faceLabelsById?: Map<number, string> | Record<number, string>;
}

function lookupName(map: Map<number, string> | Record<number, string> | undefined, id: number): string | undefined {
  if (!map) return undefined;
  if (map instanceof Map) return map.get(id);
  return (map as Record<number, string>)[id];
}

interface DerivedQueryNameOperand {
  sourceKey?: string;
  kind?: string;
  tagIds?: number[];
  performerIds?: number[];
  faceIds?: number[];
  minConfidence?: number;
}

// Names a derived query in words, e.g. "Dancing or Singing", "Kissing with Alex", "Dancing without Alex". Every
// span of an intersection or difference matches the whole expression; a union span matches at least one side.
export function formatDerivedQueryName(
  query: { operator: SegmentSpanOperator; operands: DerivedQueryNameOperand[] } | undefined,
  names?: DerivedOperandNameMaps,
) {
  if (!query || query.operands.length === 0) {
    return undefined;
  }

  const [first, ...rest] = query.operands.map((operand) => formatDerivedOperandName(operand, names));
  const name =
    rest.length === 0
      ? first
      : query.operator === "union"
        ? [first, ...rest].join(" or ")
        : query.operator === "intersection"
          ? `${first} with ${joinWords(rest, "and")}`
          : `${first} without ${joinWords(rest, "or")}`;
  return name.charAt(0).toUpperCase() + name.slice(1);
}

// True once every tag, performer and face the query names has loaded, so a caller can wait rather than show ids.
export function hasDerivedQueryNames(operands: DerivedQueryNameOperand[], names?: DerivedOperandNameMaps) {
  return operands.every(
    (operand) =>
      (operand.tagIds ?? []).every((id) => lookupName(names?.tagNamesById, id) != null) &&
      (operand.performerIds ?? []).every((id) => lookupName(names?.performerNamesById, id) != null) &&
      (operand.faceIds ?? []).every((id) => lookupName(names?.faceLabelsById, id) != null),
  );
}

// The tags, performers or faces an operand names already say what kind of segment they are, so the kind is
// only spelled out for an operand without names.
function formatDerivedOperandName(operand: DerivedQueryNameOperand, names?: DerivedOperandNameMaps) {
  const named = [
    ...(operand.tagIds ?? []).map((id) => lookupName(names?.tagNamesById, id) ?? `Tag #${id}`),
    ...(operand.performerIds ?? []).map((id) => lookupName(names?.performerNamesById, id) ?? `Performer #${id}`),
    ...(operand.faceIds ?? []).map((id) => lookupName(names?.faceLabelsById, id) ?? `Face #${id}`),
  ];
  const qualifiers = [
    operand.sourceKey ? formatSourceKeyLabel(operand.sourceKey) : null,
    operand.minConfidence != null ? `${Math.round(operand.minConfidence * 100)}%+` : null,
  ].filter((qualifier): qualifier is string => !!qualifier);

  // An operand matches any of its tags, performers or faces.
  const subject =
    named.length > 1
      ? `(${named.join(" or ")})`
      : (named[0] ?? (operand.kind ? `any ${operand.kind.toLowerCase()}` : null) ?? "any segment");
  return qualifiers.length > 0 ? `${subject} (${qualifiers.join(", ")})` : subject;
}

function joinWords(words: string[], conjunction: "and" | "or") {
  return words.length <= 1 ? (words[0] ?? "") : `${words.slice(0, -1).join(", ")} ${conjunction} ${words.at(-1)}`;
}

export function formatSegmentRange(startSec: number, endSec?: number) {
  const start = formatSegmentTime(startSec);
  return endSec == null ? start : `${start} - ${formatSegmentTime(endSec)}`;
}

export function formatSegmentDuration(startSec: number, endSec?: number) {
  if (endSec == null) {
    return "Instant";
  }

  const duration = Math.max(0, endSec - startSec);
  return duration > 0 ? `${formatSegmentTime(duration)} long` : "Instant";
}

export function formatSegmentCardEyebrow(startSec: number, endSec?: number) {
  return formatSegmentRange(startSec, endSec);
}

// The page of the stored face a face span or raw face segment names, as the faces list opens it.
export function spanFaceRoute(item: DerivedSpanItem) {
  const faceId = item.reference?.faceId;
  return faceId == null ? undefined : ({ page: "face", id: faceId } as const);
}

export function rawSegmentFaceRoute(segment: RawSegmentItem) {
  const faceId = rawSegmentFaceId(segment);
  return faceId == null ? undefined : ({ page: "face", id: faceId } as const);
}

function rawSegmentFaceId(segment: RawSegmentItem) {
  return segment.kind?.toLowerCase() === "face" ? segment.refId : undefined;
}

// A raw face segment of a face linked to no performer and titled with nothing but that face's label, whose name
// is worth looking up; a title someone gave the segment stays.
export function rawSegmentPlaceholderFaceId(segment: RawSegmentItem) {
  if (segment.performerName || segment.tagName) return undefined;
  const title = segment.title?.trim().toLowerCase();
  if (title && title !== segment.refLabel?.trim().toLowerCase()) return undefined;
  return rawSegmentFaceId(segment);
}

export function rawSegmentFaceName(segment: RawSegmentItem, faceNames: ReadonlyMap<number, ListedFaceName>) {
  const faceId = rawSegmentPlaceholderFaceId(segment);
  return faceId == null ? undefined : faceNames.get(faceId);
}

// A title as screen readers announce it, saying when the name is only a suggested match.
export function formatShownTitle(title: string, suggested?: boolean) {
  return suggested ? `${title} (suggested)` : title;
}

export { formatDate, formatSegmentTime };
