import { useCallback, useMemo, useState } from "react";
import { ChevronDown } from "lucide-react";
import { InteractiveRating } from "./Rating";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useEntityRatings } from "../hooks/useEntityRatings";
import type { AffinityHostType } from "../api/types";

interface Props {
  hostType: AffinityHostType;
  hostId: number;
  canRate: boolean;
  className?: string;
  showHeading?: boolean;
  variant?: "grid" | "inline";
  /** When true (and a heading is shown) the panel can be collapsed; preference is remembered per entity type. */
  collapsible?: boolean;
}

// Per-entity-type persisted collapse preference for the rating breakdown.
function useCollapsedFlag(key: string): [boolean, () => void] {
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    if (typeof window === "undefined") return false;
    try {
      return window.localStorage.getItem(key) === "true";
    } catch {
      return false;
    }
  });
  const toggle = useCallback(() => {
    setCollapsed((prev) => {
      const next = !prev;
      try { window.localStorage.setItem(key, next ? "true" : "false"); } catch { /* ignore */ }
      return next;
    });
  }, [key]);
  return [collapsed, toggle];
}

interface AspectDefinition {
  key: string;
  label: string;
}

const DEFAULT_ASPECTS: Partial<Record<AffinityHostType, AspectDefinition[]>> = {
  video: [
    { key: "audio", label: "Audio" },
    { key: "video_quality", label: "Video Quality" },
    { key: "content", label: "Content" },
    { key: "performers", label: "Performers" },
  ],
  image: [
    { key: "content", label: "Content" },
    { key: "performers", label: "Performers" },
    { key: "quality", label: "Quality" },
  ],
  audio: [
    { key: "audio", label: "Audio" },
    { key: "content", label: "Content" },
  ],
  performer: [
    { key: "face", label: "Face" },
    { key: "body", label: "Body" },
    { key: "voice", label: "Voice" },
  ],
};

export function AspectRatingsPanel({ hostType, hostId, canRate, className, showHeading = true, variant = "grid", collapsible = true }: Props) {
  const { ratings, isLoading } = useEntityRatings(hostType, hostId, { enabled: hostId > 0 });
  const { setRating } = useEntityEngagement(hostType, hostId, { enabled: false });
  const [collapsed, toggleCollapsed] = useCollapsedFlag(`cove.ratingBreakdownCollapsed.${hostType}`);
  const canCollapse = collapsible && showHeading;
  const isCollapsed = canCollapse && collapsed;

  const aspects = useMemo(() => {
    const defaults = DEFAULT_ASPECTS[hostType] ?? [];
    const defaultKeys = new Set(defaults.map((aspect) => aspect.key));
    const extras = Object.keys(ratings)
      .filter((key) => key !== "overall" && !defaultKeys.has(key))
      .sort((left, right) => left.localeCompare(right))
      .map((key) => ({ key, label: formatAspectLabel(key) }));
    return [...defaults, ...extras];
  }, [hostType, ratings]);

  if (aspects.length === 0) {
    return null;
  }

  return (
    <section className={className}>
      {showHeading ? (
        <div className="mb-2 flex items-center justify-between gap-2">
          {canCollapse ? (
            <button
              type="button"
              onClick={toggleCollapsed}
              aria-expanded={!isCollapsed}
              className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-muted transition-colors hover:text-secondary"
            >
              <ChevronDown size={14} className={`transition-transform ${isCollapsed ? "-rotate-90" : ""}`} />
              Rating Breakdown
            </button>
          ) : (
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted">Rating Breakdown</h3>
          )}
          {isLoading ? <span className="text-xs text-muted">Loading...</span> : null}
        </div>
      ) : null}
      {isCollapsed ? null : variant === "inline" ? (
        <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
          {aspects.map((aspect) => (
            <div key={aspect.key} className="inline-flex items-center gap-2">
              <span className="text-xs font-medium text-muted">{aspect.label}:</span>
              <InteractiveRating
                value={ratings[aspect.key]}
                onChange={(value) => setRating(value, aspect.key)}
                readOnly={!canRate}
              />
            </div>
          ))}
        </div>
      ) : (
        <div className="grid gap-x-4 gap-y-2 sm:grid-cols-2">
          {aspects.map((aspect) => (
            <div key={aspect.key} className="flex items-center justify-between gap-3 py-1">
              <div className="text-xs uppercase tracking-wide text-muted">{aspect.label}</div>
              <InteractiveRating
                value={ratings[aspect.key]}
                onChange={(value) => setRating(value, aspect.key)}
                readOnly={!canRate}
              />
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function formatAspectLabel(value: string) {
  return value
    .split(/[_-]/g)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}
