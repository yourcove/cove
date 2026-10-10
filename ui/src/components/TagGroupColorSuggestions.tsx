import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowRight, Shuffle } from "lucide-react";
import type { TagGroup } from "../api/types";
import { findGroupsNeedingColor, normalizeGroupColor, suggestDistinctColors } from "../utils/tagGroupColors";
import { TagBadge } from "./shared";

export interface TagGroupColorChange {
  id: number;
  name: string;
  previous: string | null;
  next: string;
}

/**
 * Previews a distinct color for each selected tag group. Nothing is saved until the user applies,
 * and only checked groups change.
 */
export function TagGroupColorSuggestions({
  groups,
  applying,
  onApply,
  onCancel,
}: {
  groups: TagGroup[];
  applying: boolean;
  onApply: (changes: TagGroupColorChange[]) => void;
  onCancel: () => void;
}) {
  const [selectedIds, setSelectedIds] = useState(() => findGroupsNeedingColor(groups));
  const [variant, setVariant] = useState(0);
  const headingRef = useRef<HTMLHeadingElement>(null);
  // The button that opened the panel is disabled while it is open, so take focus here.
  useEffect(() => headingRef.current?.focus(), []);
  // A suggestion that equals the group's current color is not a change, so it is neither shown nor saved.
  const suggestions = useMemo(() => {
    const proposals = suggestDistinctColors(groups, selectedIds, variant);
    for (const group of groups) {
      if (proposals.get(group.id) === normalizeGroupColor(group.color)) proposals.delete(group.id);
    }
    return proposals;
  }, [groups, selectedIds, variant]);

  const toggle = (id: number) =>
    setSelectedIds((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const changes = groups.flatMap((group) => {
    const next = suggestions.get(group.id);
    return next ? [{ id: group.id, name: group.name, previous: group.color ?? null, next }] : [];
  });

  return (
    <div className="space-y-3 rounded-lg border border-border bg-surface p-3">
      <div>
        <h4 ref={headingRef} tabIndex={-1} className="text-sm font-semibold text-foreground outline-none">
          Suggest distinct colors
        </h4>
        <p className="mt-1 text-sm text-secondary">
          Groups with no color, the old default green, or a color another group uses are checked. Check any other group
          you want to recolor. Nothing is saved until you apply.
        </p>
      </div>

      <ul className="max-h-[50vh] space-y-1 overflow-y-auto">
        {groups.map((group) => {
          const checked = selectedIds.has(group.id);
          const next = suggestions.get(group.id);
          const inputId = `tag-group-color-suggest-${group.id}`;
          return (
            <li key={group.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded px-1 py-1 hover:bg-card">
              <input
                id={inputId}
                type="checkbox"
                checked={checked}
                disabled={applying}
                onChange={() => toggle(group.id)}
                className="h-4 w-4 accent-accent"
              />
              <label htmlFor={inputId} className="min-w-0 flex-1 truncate text-sm text-foreground">
                {group.name}
              </label>
              <div className="flex items-center gap-2" data-testid={`tag-group-color-preview-${group.id}`}>
                <span className="sr-only">Current color {group.color ?? "none"}</span>
                <TagBadge name={group.name} color={group.color} groupColor={group.color} groupName={group.name} />
                {next ? (
                  <>
                    <ArrowRight className="h-3.5 w-3.5 text-muted" role="img" aria-label="becomes" />
                    <span className="sr-only">new color {next}</span>
                    <TagBadge name={group.name} color={next} groupColor={next} groupName={group.name} />
                  </>
                ) : (
                  <span className="text-xs text-muted">Unchanged</span>
                )}
              </div>
            </li>
          );
        })}
      </ul>

      <div className="flex flex-wrap items-center justify-end gap-2">
        <button
          type="button"
          onClick={() => setVariant((current) => current + 1)}
          disabled={applying || selectedIds.size < 2}
          className="mr-auto inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60"
        >
          <Shuffle className="h-4 w-4" /> Shuffle
        </button>
        <button
          type="button"
          onClick={onCancel}
          disabled={applying}
          className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60"
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={() => onApply(changes)}
          disabled={applying || changes.length === 0}
          className="rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
        >
          {applying ? "Applying..." : `Apply to ${changes.length} group${changes.length === 1 ? "" : "s"}`}
        </button>
      </div>
    </div>
  );
}
