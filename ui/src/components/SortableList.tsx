import {
  useCallback,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type PointerEvent,
  type ReactNode,
} from "react";

export interface DragHandleProps {
  tabIndex: number;
  role: "button";
  "aria-label": string;
  "aria-pressed": boolean;
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void;
  onPointerDown: (event: PointerEvent<HTMLElement>) => void;
  onPointerMove: (event: PointerEvent<HTMLElement>) => void;
  onPointerUp: (event: PointerEvent<HTMLElement>) => void;
  onPointerCancel: (event: PointerEvent<HTMLElement>) => void;
  style: CSSProperties;
}

interface SortableListRenderState {
  index: number;
  isDragging: boolean;
  isOver: boolean;
  keyboardDragging: boolean;
  dragHandleProps: DragHandleProps;
}

interface SortableListProps<T> {
  items: T[];
  getKey: (item: T) => string | number;
  onReorder: (nextItems: T[]) => void;
  renderItem: (item: T, state: SortableListRenderState) => ReactNode;
  disabled?: boolean;
  className?: string;
  style?: CSSProperties;
}

function moveItem<T>(items: T[], fromIndex: number, toIndex: number) {
  if (fromIndex === toIndex || fromIndex < 0 || toIndex < 0 || fromIndex >= items.length || toIndex >= items.length) {
    return items;
  }

  const nextItems = [...items];
  const [movedItem] = nextItems.splice(fromIndex, 1);
  nextItems.splice(toIndex, 0, movedItem);
  return nextItems;
}

export function SortableList<T>({
  items,
  getKey,
  onReorder,
  renderItem,
  disabled = false,
  className = "space-y-2",
  style,
}: SortableListProps<T>) {
  const [dragKey, setDragKey] = useState<string | number | null>(null);
  const [overKey, setOverKey] = useState<string | number | null>(null);
  const [keyboardDragKey, setKeyboardDragKey] = useState<string | number | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const pointerDrag = useRef<{
    key: string | number;
    pointerId: number;
    startX: number;
    startY: number;
    active: boolean;
    overIndex: number;
  } | null>(null);

  const reorderByIndex = useCallback(
    (fromIndex: number, toIndex: number) => {
      const nextItems = moveItem(items, fromIndex, toIndex);
      if (nextItems !== items) {
        onReorder(nextItems);
      }
    },
    [items, onReorder],
  );

  const commitDrag = useCallback(
    (fromKey = dragKey, toKey = overKey) => {
      if (disabled || fromKey == null || toKey == null || fromKey === toKey) {
        return;
      }

      const fromIndex = items.findIndex((item) => getKey(item) === fromKey);
      const toIndex = items.findIndex((item) => getKey(item) === toKey);
      reorderByIndex(fromIndex, toIndex);
    },
    [disabled, dragKey, getKey, items, overKey, reorderByIndex],
  );

  const resetDragState = useCallback(() => {
    setDragKey(null);
    setOverKey(null);
    pointerDrag.current = null;
  }, []);

  return (
    <div ref={listRef} className={className} style={style} role="list">
      {items.map((item, index) => {
        const itemKey = getKey(item);
        const isDragging = dragKey === itemKey;
        const isOver = overKey === itemKey;
        const keyboardDragging = keyboardDragKey === itemKey;
        const dragHandleProps: DragHandleProps = {
          tabIndex: disabled ? -1 : 0,
          role: "button",
          "aria-label": keyboardDragging ? "Drop item" : "Pick up item to reorder",
          "aria-pressed": keyboardDragging,
          style: {
            display: "inline-flex",
            alignItems: "center",
            justifyContent: "center",
            touchAction: "none",
            minWidth: 44,
            minHeight: 44,
          },
          onKeyDown: (event) => {
            if (disabled) {
              return;
            }

            if (event.key === "Escape") {
              if (keyboardDragging) {
                event.preventDefault();
                setKeyboardDragKey(null);
              }
              return;
            }

            if (event.key === " " || event.key === "Enter") {
              event.preventDefault();
              setKeyboardDragKey((current) => (current === itemKey ? null : itemKey));
              return;
            }

            if (event.key === "ArrowUp" && (event.altKey || keyboardDragging)) {
              event.preventDefault();
              reorderByIndex(index, index - 1);
              return;
            }

            if (event.key === "ArrowDown" && (event.altKey || keyboardDragging)) {
              event.preventDefault();
              reorderByIndex(index, index + 1);
            }
          },
          onPointerDown: (event) => {
            if (disabled || !event.isPrimary || event.button !== 0) return;
            event.preventDefault();
            event.currentTarget.setPointerCapture?.(event.pointerId);
            pointerDrag.current = {
              key: itemKey,
              pointerId: event.pointerId,
              startX: event.clientX,
              startY: event.clientY,
              active: false,
              overIndex: index,
            };
          },
          onPointerMove: (event) => {
            const pointer = pointerDrag.current;
            if (!pointer || pointer.pointerId !== event.pointerId) return;

            if (!pointer.active && Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY) < 5)
              return;
            event.preventDefault();
            if (!pointer.active) {
              pointer.active = true;
              setDragKey(pointer.key);
              setOverKey(pointer.key);
            }

            const target = document
              .elementFromPoint(event.clientX, event.clientY)
              ?.closest<HTMLElement>("[data-sortable-index]");
            const targetIndex = Number(target?.dataset.sortableIndex);
            if (
              target?.parentElement === listRef.current &&
              Number.isInteger(targetIndex) &&
              targetIndex >= 0 &&
              targetIndex < items.length &&
              targetIndex !== pointer.overIndex
            ) {
              pointer.overIndex = targetIndex;
              setOverKey(getKey(items[targetIndex]));
            }

            const edge = 48;
            const scrollAmount = event.clientY < edge ? -12 : event.clientY > window.innerHeight - edge ? 12 : 0;
            if (scrollAmount) window.scrollBy({ top: scrollAmount, behavior: "auto" });
          },
          onPointerUp: (event) => {
            const pointer = pointerDrag.current;
            if (!pointer || pointer.pointerId !== event.pointerId) return;
            event.currentTarget.releasePointerCapture?.(event.pointerId);
            if (pointer.active) commitDrag(pointer.key, getKey(items[pointer.overIndex]));
            resetDragState();
          },
          onPointerCancel: (event) => {
            if (pointerDrag.current?.pointerId === event.pointerId) resetDragState();
          },
        };

        return (
          <div
            key={String(itemKey)}
            data-sortable-index={index}
            role="listitem"
            aria-grabbed={isDragging || keyboardDragging}
          >
            {renderItem(item, { index, isDragging, isOver, keyboardDragging, dragHandleProps })}
          </div>
        );
      })}
    </div>
  );
}
