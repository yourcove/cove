import { useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import type { FieldProvenance } from "../api/types";
import { formatDateTime } from "../utils/dateFormat";

export function FieldProvenanceHover({
  fieldProvenance,
  fieldKey,
  children,
  className,
  block = false,
}: {
  fieldProvenance?: FieldProvenance[];
  fieldKey: string | string[];
  children: ReactNode;
  className?: string;
  block?: boolean;
}) {
  const wrapperRef = useRef<HTMLElement>(null);
  const [showProvenance, setShowProvenance] = useState(false);
  const [popupPosition, setPopupPosition] = useState<{ left: number; top: number }>({ left: 0, top: 0 });
  const entries = getFieldProvenanceEntries(fieldProvenance, fieldKey);
  const label = formatFieldProvenanceKey(Array.isArray(fieldKey) ? fieldKey[0] : fieldKey);
  const rootClassName = [
    block
      ? "group/provenance relative block min-w-0"
      : "group/provenance relative inline-flex max-w-full min-w-0 items-baseline gap-1.5",
    entries.length > 0 ? "cursor-help" : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");

  useLayoutEffect(() => {
    if (!showProvenance || entries.length === 0) {
      return;
    }

    const updatePosition = () => {
      const rect = wrapperRef.current?.getBoundingClientRect();
      if (!rect) {
        return;
      }

      const width = 320;
      const margin = 8;
      const left = Math.min(Math.max(margin, rect.right - width), window.innerWidth - width - margin);
      const preferredTop = rect.bottom + margin;
      const top = preferredTop < window.innerHeight - margin ? preferredTop : Math.max(margin, rect.top - margin);
      setPopupPosition({ left, top });
    };

    updatePosition();
    window.addEventListener("resize", updatePosition);
    window.addEventListener("scroll", updatePosition, true);
    return () => {
      window.removeEventListener("resize", updatePosition);
      window.removeEventListener("scroll", updatePosition, true);
    };
  }, [entries.length, showProvenance]);

  if (entries.length === 0) {
    return <>{children}</>;
  }

  const content = (
    <>
      {children}
      <span className="sr-only">
        <FieldProvenancePopupContent entries={entries} title={`${label} Sources`} />
      </span>
      {showProvenance && typeof document !== "undefined"
        ? createPortal(
            <span
              className="pointer-events-none fixed z-[200] max-h-[min(70vh,26rem)] w-80 overflow-y-auto rounded-xl border border-border bg-surface/95 p-3 text-left shadow-2xl backdrop-blur"
              style={{ left: popupPosition.left, top: popupPosition.top }}
            >
              <FieldProvenancePopupContent entries={entries} title={`${label} Sources`} />
            </span>,
            document.body,
          )
        : null}
    </>
  );

  if (block) {
    return (
      <div
        ref={wrapperRef as RefObject<HTMLDivElement>}
        className={rootClassName}
        onMouseEnter={() => setShowProvenance(true)}
        onMouseLeave={() => setShowProvenance(false)}
        onFocus={() => setShowProvenance(true)}
        onBlur={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
            setShowProvenance(false);
          }
        }}
        tabIndex={0}
      >
        {content}
      </div>
    );
  }

  return (
    <span
      ref={wrapperRef as RefObject<HTMLSpanElement>}
      className={rootClassName}
      onMouseEnter={() => setShowProvenance(true)}
      onMouseLeave={() => setShowProvenance(false)}
      onFocus={() => setShowProvenance(true)}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setShowProvenance(false);
        }
      }}
      tabIndex={0}
    >
      {content}
    </span>
  );
}

export function getFieldProvenanceEntries(fieldProvenance: FieldProvenance[] | undefined, fieldKey: string | string[]) {
  const keys = (Array.isArray(fieldKey) ? fieldKey : [fieldKey]).flatMap(getFieldKeyAliases);
  const normalizedKeys = new Set(keys.flatMap((key) => [normalizeFieldKey(key), compactFieldKey(key)]));

  return (fieldProvenance ?? [])
    .filter(
      (entry) =>
        normalizedKeys.has(normalizeFieldKey(entry.fieldKey)) || normalizedKeys.has(compactFieldKey(entry.fieldKey)),
    )
    .slice()
    .sort((left, right) => getSortableDate(right.createdAt) - getSortableDate(left.createdAt));
}

function FieldProvenancePopupContent({ entries, title }: { entries: FieldProvenance[]; title: string }) {
  return (
    <>
      <span className="mb-2 block text-[11px] font-semibold uppercase tracking-[0.16em] text-muted">{title}</span>
      <span className="flex flex-col gap-2">
        {entries.map((entry, index) => (
          <span
            key={`${entry.fieldKey}-${entry.sourceKey}-${entry.sourceRunId ?? ""}-${entry.modelKey ?? ""}-${index}`}
            className="block rounded-lg border border-border/70 bg-card/70 px-2.5 py-2"
          >
            <span className="flex items-center justify-between gap-2 text-xs text-foreground">
              <span className="font-medium">{formatProvenanceSource(entry.sourceKey)}</span>
              {entry.confidence != null ? (
                <span className="text-emerald-300">{formatConfidence(entry.confidence)}</span>
              ) : null}
            </span>
            <span className="mt-1 block break-words text-[11px] text-secondary">
              Value {formatFieldProvenanceValue(entry.value)}
            </span>
            {entry.modelKey ? (
              <span className="mt-1 block break-all text-[11px] text-secondary">Model {entry.modelKey}</span>
            ) : null}
            {entry.sourceRunId ? (
              <span className="mt-1 block break-all text-[11px] text-muted">Run {entry.sourceRunId}</span>
            ) : null}
            <span className="mt-1 block text-[11px] text-muted">Recorded {formatProvenanceDate(entry.createdAt)}</span>
          </span>
        ))}
      </span>
    </>
  );
}

function getFieldKeyAliases(fieldKey: string) {
  const normalized = fieldKey.trim();
  if (!normalized) {
    return [];
  }

  return [normalized, normalized.replace(/([a-z0-9])([A-Z])/g, "$1_$2"), normalized.replace(/_/g, "")];
}

function normalizeFieldKey(value: string) {
  return value
    .trim()
    .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
    .replace(/[:.\s-]+/g, "_")
    .toLowerCase();
}

function compactFieldKey(value: string) {
  return normalizeFieldKey(value).replace(/_/g, "");
}

function formatFieldProvenanceKey(fieldKey: string) {
  const normalized = fieldKey.trim();
  if (!normalized) {
    return "Field";
  }

  return normalized
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[:._\s-]+/)
    .filter(Boolean)
    .map(capitalizeWord)
    .join(" ");
}

function formatFieldProvenanceValue(value: unknown) {
  if (value === undefined) {
    return "Unavailable";
  }

  if (value === null) {
    return "Cleared";
  }

  if (typeof value === "string") {
    const normalized = value.trim();
    return normalized ? truncateFieldPreview(normalized) : "Empty";
  }

  if (typeof value === "number") {
    return Number.isFinite(value) ? value.toLocaleString() : String(value);
  }

  if (typeof value === "boolean") {
    return value ? "True" : "False";
  }

  if (Array.isArray(value)) {
    if (value.length === 0) {
      return "[]";
    }

    return truncateFieldPreview(value.map(formatFieldProvenanceArrayItem).join(", "));
  }

  try {
    return truncateFieldPreview(JSON.stringify(value));
  } catch {
    return "Unavailable";
  }
}

function formatFieldProvenanceArrayItem(value: unknown) {
  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

function truncateFieldPreview(value: string, maxLength = 160) {
  const normalized = value.replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }

  return `${normalized.slice(0, Math.max(0, maxLength - 3))}...`;
}

function getSortableDate(value?: string) {
  const parsed = value ? Date.parse(value) : Number.NaN;
  return Number.isNaN(parsed) ? 0 : parsed;
}

function formatProvenanceSource(sourceKey: string) {
  const normalized = sourceKey.trim();
  if (!normalized) {
    return "Unknown";
  }

  if (normalized.toLowerCase() === "user") {
    return "Manual";
  }

  if (normalized.startsWith("ext:")) {
    return normalized.slice(4).split(".").map(capitalizeWord).join(".");
  }

  if (normalized.startsWith("scraper:")) {
    return `Scraper: ${formatProviderIdentifier(normalized.slice("scraper:".length))}`;
  }

  if (normalized.startsWith("metadata:")) {
    return `Metadata: ${formatProviderIdentifier(normalized.slice("metadata:".length))}`;
  }

  return normalized
    .split(/[:._-]+/)
    .map(capitalizeWord)
    .join(" ");
}

function formatProviderIdentifier(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return "Default";
  }

  try {
    const url = new URL(trimmed);
    return url.host || trimmed;
  } catch {
    return trimmed;
  }
}

function capitalizeWord(value: string) {
  if (!value) {
    return value;
  }

  return value[0].toUpperCase() + value.slice(1);
}

function formatConfidence(confidence: number) {
  return `${Math.round(confidence * 100)}%`;
}

function formatProvenanceDate(value?: string) {
  if (!value) {
    return "Unknown";
  }

  try {
    return formatDateTime(value);
  } catch {
    return value;
  }
}
