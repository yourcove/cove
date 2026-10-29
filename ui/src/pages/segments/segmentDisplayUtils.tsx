import { type ReactNode } from "react";
import type { ResolvedSpan, SegmentSpanReference } from "../../api/types";
import type { SegmentTileItem } from "../../components/EntityCards";
import { SegmentPreviewMedia } from "../../components/SegmentPreviewMedia";
import { formatDate } from "../../components/shared";
import { faceStoredName } from "../../utils/faceDisplay";
import { formatSegmentTime } from "../../utils/segmentTime";
import { formatOperatorLabel } from "./derivedQueryCriterion";
import type { DerivedSpanItem, RawSegmentItem } from "./types";

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
// which the reference replaces.
export function buildSpanTileSegment(item: DerivedSpanItem, title: string): SegmentTileItem {
  return {
    id: item.coverSegmentId ?? item.span.segmentIds[0] ?? item.id,
    hostType: "video",
    hostId: item.videoId,
    startSec: item.span.startSec,
    endSec: item.span.endSec,
    tagName: item.span.tagId != null ? item.span.tagName : undefined,
    kind: item.span.kind,
    sourceKey: item.span.sourceKey,
    title,
    updatedAt: item.coverVersion ?? item.videoUpdatedAt,
    hostTitle: item.videoTitle,
    refLabel: item.reference?.faceSourceKey || item.reference?.faceLabel,
  };
}

// A face span reads as its performer or its own name; a face still carrying only its placeholder label is
// "Unnamed face", with the placeholder shown as its detail instead.
export function formatSpanReferenceName(reference?: SegmentSpanReference) {
  if (!reference) return undefined;
  if (reference.faceId == null) return reference.performerName;
  return (
    faceStoredName({
      id: reference.faceId,
      label: reference.faceLabel,
      primarySourceKey: reference.faceSourceKey,
      performerId: reference.performerId,
      performerName: reference.performerName,
    }) ?? "Unnamed face"
  );
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

  return sourceKey.startsWith("ext:")
    ? sourceKey
        .slice(4)
        .split(/[._-]+/)
        .filter(Boolean)
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(" ")
    : sourceKey;
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

export function formatDerivedOperandSummary(item: DerivedSpanItem, names?: DerivedOperandNameMaps) {
  const descriptor = item.derivedQueryDescriptor;
  if (!descriptor || descriptor.operands.length === 0) {
    return undefined;
  }

  const operands = descriptor.operands.map((operand, index) => {
    const parts: string[] = [];
    if (operand.sourceKey) parts.push(formatSourceLabel(operand.sourceKey));
    if (operand.kind) parts.push(operand.kind);
    if (operand.tagIds?.length) {
      const named = operand.tagIds.map((id) => lookupName(names?.tagNamesById, id) ?? `#${id}`);
      parts.push(named.join(", "));
    }
    if (operand.performerIds?.length) {
      const named = operand.performerIds.map((id) => lookupName(names?.performerNamesById, id) ?? `#${id}`);
      parts.push(named.join(", "));
    }
    if (operand.faceIds?.length) {
      const named = operand.faceIds.map((id) => lookupName(names?.faceLabelsById, id) ?? `Face #${id}`);
      parts.push(named.join(", "));
    }
    if (operand.minConfidence != null) parts.push(`${Math.round(operand.minConfidence * 100)}%+ confidence`);
    return parts.length > 0 ? parts.join(" + ") : `Operand ${index + 1}`;
  });

  return `${formatOperatorLabel(descriptor.operator)} of ${operands.join(" / ")}`;
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

export { formatDate, formatSegmentTime };
