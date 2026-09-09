import { type ReactNode } from "react";
import type { ResolvedSpan } from "../../api/types";
import { SegmentPreviewMedia } from "../../components/SegmentPreviewMedia";
import { formatDate } from "../../components/shared";
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

export function buildSpanTitle(span: ResolvedSpan, videoTitle?: string) {
  return span.tagName || span.kind || span.sourceKey || videoTitle || `Span ${span.spanKey}`;
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

export function formatSegmentTime(value: number) {
  const totalHundredths = Math.max(0, Math.round(value * 100));
  const hours = Math.floor(totalHundredths / 360000);
  const minutes = Math.floor((totalHundredths % 360000) / 6000);
  const seconds = Math.floor((totalHundredths % 6000) / 100);
  const hundredths = totalHundredths % 100;

  if (hundredths === 0) {
    if (hours > 0) {
      return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    }

    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  }

  const fractional = hundredths % 10 === 0 ? String(Math.floor(hundredths / 10)) : String(hundredths).padStart(2, "0");

  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${fractional}`;
  }

  return `${minutes}:${String(seconds).padStart(2, "0")}.${fractional}`;
}

export { formatDate };
