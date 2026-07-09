import { useCallback, useEffect, useRef, useState } from "react";

interface UseMultiSelectOptions<T extends { id: string | number }> {
  preserveOnItemsChange?: boolean;
  /** @deprecated Use preserveOnItemsChange instead. */
  preserveOnAppend?: boolean;
  resetKey?: string;
  isSelectable?: (item: T) => boolean;
  isSelectableId?: (id: T["id"]) => boolean;
}

export interface MultiSelectToggleOptions<TId extends string | number = number> {
  range?: boolean;
  orderedIds?: readonly TId[];
}

export type MultiSelectToggleHandler<TId extends string | number = number> = (id: TId, options?: MultiSelectToggleOptions<TId>) => void;
export type BoundMultiSelectToggleHandler<TId extends string | number = number> = (options?: MultiSelectToggleOptions<TId>) => void;

export function toggleOptionsFromEvent<TId extends string | number = never>(event: { shiftKey: boolean }): MultiSelectToggleOptions<TId>;
export function toggleOptionsFromEvent<TId extends string | number>(event: { shiftKey: boolean }, orderedIds: readonly TId[]): MultiSelectToggleOptions<TId>;
export function toggleOptionsFromEvent<TId extends string | number>(
  event: { shiftKey: boolean },
  orderedIds?: readonly TId[],
): MultiSelectToggleOptions<TId> {
  if (orderedIds === undefined) {
    return { range: event.shiftKey };
  }

  return { range: event.shiftKey, orderedIds };
}

export function withOrderedToggle<TId extends string | number>(
  onToggle: MultiSelectToggleHandler<TId>,
  orderedIds: readonly TId[],
): MultiSelectToggleHandler<TId> {
  return (id, options) => onToggle(id, { ...options, orderedIds });
}

const allSelectable = () => true;

export function useMultiSelect<T extends { id: string | number }>(items: T[], options: UseMultiSelectOptions<T> = {}) {
  const [selectedIds, setSelectedIds] = useState<Set<T["id"]>>(new Set());
  const lastToggledId = useRef<T["id"] | null>(null);
  const isSelectable = options.isSelectable ?? allSelectable;
  const isSelectableId = options.isSelectableId ?? allSelectable;
  const canSelectUnloadedIds = options.isSelectableId !== undefined;
  const preserveOnItemsChange = options.preserveOnItemsChange ?? options.preserveOnAppend ?? false;

  // Infinite lists can unload pages above or below the viewport, so those selections are preserved until the query changes.
  const itemIdsKey = items.map((item) => String(item.id)).join(",");
  const prevKey = useRef(itemIdsKey);
  const resetKey = options.resetKey ?? "";
  const prevResetKey = useRef(resetKey);

  useEffect(() => {
    if (prevResetKey.current !== resetKey) {
      prevResetKey.current = resetKey;
      prevKey.current = itemIdsKey;
      lastToggledId.current = null;
      setSelectedIds(new Set<T["id"]>());
      return;
    }

    if (prevKey.current !== itemIdsKey) {
      prevKey.current = itemIdsKey;

      if (preserveOnItemsChange) {
        return;
      }

      lastToggledId.current = null;
      setSelectedIds(new Set<T["id"]>());
    }
  }, [itemIdsKey, preserveOnItemsChange, resetKey]);

  const isSelectableItem = useCallback((item: T) => isSelectableId(item.id) && isSelectable(item), [isSelectable, isSelectableId]);
  const isSelectableSelectionId = useCallback((id: T["id"], itemById: ReadonlyMap<T["id"], T>) => {
    const item = itemById.get(id);
    if (item) {
      return isSelectableItem(item);
    }

    return isSelectableId(id);
  }, [isSelectableId, isSelectableItem]);
  const isSelectableToggleId = useCallback((id: T["id"], itemById: ReadonlyMap<T["id"], T>) => {
    const item = itemById.get(id);
    if (item) {
      return isSelectableItem(item);
    }

    return canSelectUnloadedIds && isSelectableId(id);
  }, [canSelectUnloadedIds, isSelectableId, isSelectableItem]);

  const toggle = useCallback((id: T["id"], toggleOptions: MultiSelectToggleOptions<T["id"]> = {}) => {
    const itemById = new Map(items.map((item) => [item.id, item]));
    if (!isSelectableToggleId(id, itemById)) {
      return;
    }

    const anchorId = lastToggledId.current;
    const rangeIds = toggleOptions.orderedIds ?? items.map((item) => item.id);
    const anchorIndex = anchorId == null ? -1 : rangeIds.findIndex((itemId) => itemId === anchorId);
    const targetIndex = rangeIds.findIndex((itemId) => itemId === id);
    setSelectedIds((prev) => {
      if (toggleOptions.range && anchorIndex >= 0 && targetIndex >= 0) {
        const next = new Set(prev);
        const start = Math.min(anchorIndex, targetIndex);
        const end = Math.max(anchorIndex, targetIndex);
        for (const itemId of rangeIds.slice(start, end + 1)) {
          if (!isSelectableToggleId(itemId, itemById)) {
            continue;
          }
          next.add(itemId);
        }
        return next;
      }

      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
    lastToggledId.current = id;
  }, [isSelectableToggleId, items]);

  const selectAll = useCallback(() => {
    lastToggledId.current = null;
    setSelectedIds(new Set(items.filter(isSelectableItem).map((i) => i.id)));
  }, [isSelectableItem, items]);

  const selectIds = useCallback((ids: Array<T["id"]>) => {
    lastToggledId.current = null;
    const itemById = new Map(items.map((item) => [item.id, item]));
    setSelectedIds(new Set(ids.filter((id) => isSelectableSelectionId(id, itemById))));
  }, [isSelectableSelectionId, items]);

  const selectNone = useCallback(() => {
    lastToggledId.current = null;
    setSelectedIds(new Set<T["id"]>());
  }, []);

  const invertSelection = useCallback(() => {
    lastToggledId.current = null;
    setSelectedIds((prev) => {
      const next = new Set<T["id"]>();
      for (const item of items) {
        if (isSelectableItem(item) && !prev.has(item.id)) {
          next.add(item.id);
        }
      }
      return next;
    });
  }, [isSelectableItem, items]);

  return { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection };
}
