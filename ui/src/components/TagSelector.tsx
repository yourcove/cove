import { useState, type CSSProperties, type ReactNode } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import type { Tag, TagProvenance } from "../api/types";
import { TagProvenanceHover } from "./TagProvenanceHover";
import { rankByLabel } from "../utils/searchRanking";

export type SelectableTag = Pick<Tag, "id" | "name" | "color" | "tagGroupId" | "tagGroupName" | "tagGroupColor">;

export interface TagSelectorGroup<TTag extends SelectableTag = SelectableTag> {
  key: string;
  label: string;
  color?: string | null;
  tags: TTag[];
}

export function groupTagsForSelector<TTag extends SelectableTag>(tags: TTag[]): TagSelectorGroup<TTag>[] {
  const groups = new Map<string, TagSelectorGroup<TTag>>();

  for (const tag of tags) {
    const key = tag.tagGroupId != null ? `group:${tag.tagGroupId}` : "ungrouped";
    const label = tag.tagGroupName?.trim() || "Ungrouped";
    const group = groups.get(key) ?? { key, label, color: tag.tagGroupColor, tags: [] };
    group.tags.push(tag);
    groups.set(key, group);
  }

  return Array.from(groups.values())
    .map((group) => ({ ...group, tags: [...group.tags].sort((left, right) => left.name.localeCompare(right.name)) }))
    .sort((left, right) => {
      if (left.key === "ungrouped") return 1;
      if (right.key === "ungrouped") return -1;
      return left.label.localeCompare(right.label);
    });
}

export function filterTagsForSelector<TTag extends SelectableTag>(tags: TTag[], search: string, excludedIds?: Iterable<number>) {
  const excluded = excludedIds ? new Set(excludedIds) : undefined;
  const q = search.trim().toLowerCase();

  const matched = tags.filter((tag) => {
    if (excluded?.has(tag.id)) {
      return false;
    }

    if (!q) {
      return true;
    }

    return tag.name.toLowerCase().includes(q) || (tag.tagGroupName?.toLowerCase().includes(q) ?? false);
  });

  return q ? rankByLabel(matched, search, (tag) => tag.name) : matched;
}

export function SelectedTagChips({ tags, onRemove, emptyText, className, provenanceById }: { tags: SelectableTag[]; onRemove?: (tag: SelectableTag) => void; emptyText?: string; className?: string; provenanceById?: Record<number, TagProvenance[] | undefined> }) {
  if (tags.length === 0) {
    return emptyText ? <div className="text-xs text-muted">{emptyText}</div> : null;
  }

  return (
    <div className={className ?? "flex flex-wrap gap-1.5"}>
      {tags.map((tag) => {
        const style = getTagChipStyle(tag);
        const chip = (
          <span
            key={tag.id}
            style={style}
            className="inline-flex max-w-full items-center gap-1.5 rounded border border-border bg-card px-2 py-0.5 text-xs font-medium text-secondary"
          >
            <span className="truncate text-foreground">{tag.name}</span>
            {tag.tagGroupName ? <span className="truncate text-[10px] text-muted">{tag.tagGroupName}</span> : null}
            {onRemove ? (
              <button type="button" onClick={() => onRemove(tag)} className="text-muted hover:text-white" aria-label={`Remove ${tag.name}`}>
                x
              </button>
            ) : null}
          </span>
        );
        const provenance = provenanceById?.[tag.id];
        return provenance?.length ? <TagProvenanceHover key={tag.id} provenance={provenance}>{chip}</TagProvenanceHover> : chip;
      })}
    </div>
  );
}

export function GroupedTagOptionList<TTag extends SelectableTag>({
  tags,
  onSelect,
  selectedIds,
  maxItems = 50,
  className,
  emptyText = "No tags found",
  renderTag,
  preserveOrder = false,
  groupToggleTabIndex,
  groupHeadersInteractive = true,
}: {
  tags: TTag[];
  onSelect?: (tag: TTag) => void;
  selectedIds?: Iterable<number>;
  maxItems?: number;
  className?: string;
  emptyText?: string;
  renderTag?: (tag: TTag) => ReactNode;
  preserveOrder?: boolean;
  groupToggleTabIndex?: number;
  groupHeadersInteractive?: boolean;
}) {
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(() => new Set());
  const selected = selectedIds ? new Set(selectedIds) : undefined;
  const visibleTags = tags.filter((tag) => !selected?.has(tag.id)).slice(0, maxItems);
  const groupedTags = groupTagsForSelector(visibleTags);

  if (preserveOrder) {
    return (
      <div className={className ?? "max-h-40 overflow-y-auto rounded border border-border bg-surface shadow-xl"}>
        {visibleTags.length === 0 ? <div className="px-2 py-2 text-center text-xs text-muted">{emptyText}</div> : null}
        {visibleTags.map((tag) => renderTag ? (
          <div key={tag.id}>{renderTag(tag)}</div>
        ) : (
          <button
            key={tag.id}
            type="button"
            onClick={() => onSelect?.(tag)}
            className="block w-full px-2 py-1.5 text-left text-xs text-foreground hover:bg-card"
          >
            {tag.name}
          </button>
        ))}
      </div>
    );
  }

  const toggleGroup = (groupKey: string) => {
    setCollapsedGroups((current) => {
      const next = new Set(current);
      if (next.has(groupKey)) {
        next.delete(groupKey);
      } else {
        next.add(groupKey);
      }
      return next;
    });
  };

  return (
    <div className={className ?? "max-h-40 overflow-y-auto rounded border border-border bg-surface shadow-xl"}>
      {groupedTags.length === 0 ? <div className="px-2 py-2 text-center text-xs text-muted">{emptyText}</div> : null}
      {groupedTags.map((group) => (
        <div key={group.key} className="border-b border-border/60 last:border-b-0">
          {groupHeadersInteractive ? (
            <button
              type="button"
              tabIndex={groupToggleTabIndex}
              onClick={() => toggleGroup(group.key)}
              aria-expanded={!collapsedGroups.has(group.key)}
              className="sticky top-0 z-10 flex w-full items-center gap-2 bg-surface/95 px-2 py-1 text-left text-[10px] font-semibold uppercase tracking-wide text-muted backdrop-blur hover:text-foreground"
            >
              {collapsedGroups.has(group.key) ? <ChevronRight className="h-3 w-3 flex-shrink-0" /> : <ChevronDown className="h-3 w-3 flex-shrink-0" />}
              <span className="h-2.5 w-2.5 rounded-full border border-border" style={{ backgroundColor: group.color ?? "transparent" }} />
              <span className="truncate">{group.label}</span>
              <span className="ml-auto text-[10px] font-normal">{group.tags.length}</span>
            </button>
          ) : (
            <div className="sticky top-0 z-10 flex w-full items-center gap-2 bg-surface/95 px-2 py-1 text-left text-[10px] font-semibold uppercase tracking-wide text-muted backdrop-blur">
              <span className="h-2.5 w-2.5 rounded-full border border-border" style={{ backgroundColor: group.color ?? "transparent" }} />
              <span className="truncate">{group.label}</span>
              <span className="ml-auto text-[10px] font-normal">{group.tags.length}</span>
            </div>
          )}
          {(!groupHeadersInteractive || !collapsedGroups.has(group.key)) && group.tags.map((tag) => renderTag ? (
            <div key={tag.id}>{renderTag(tag)}</div>
          ) : (
            <button
              key={tag.id}
              type="button"
              onClick={() => onSelect?.(tag)}
              className="block w-full px-2 py-1.5 text-left text-xs text-foreground hover:bg-card"
            >
              {tag.name}
            </button>
          ))}
        </div>
      ))}
    </div>
  );
}

function getTagChipStyle(tag: SelectableTag): CSSProperties | undefined {
  const color = normalizeTagColor(tag.color ?? tag.tagGroupColor);
  if (!color) {
    return undefined;
  }

  return {
    borderColor: hexToRgba(color, 0.5),
    backgroundColor: hexToRgba(color, 0.12),
    color: hexToRgba(color, 0.96),
  };
}

function normalizeTagColor(value?: string | null) {
  const trimmed = value?.trim();
  return trimmed && /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(trimmed) ? trimmed : null;
}

function hexToRgba(hex: string, alpha: number) {
  const value = hex.slice(1, 7);
  const r = Number.parseInt(value.slice(0, 2), 16);
  const g = Number.parseInt(value.slice(2, 4), 16);
  const b = Number.parseInt(value.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}
