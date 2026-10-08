import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type FocusEvent,
  type HTMLAttributes,
  type KeyboardEvent,
  type RefCallback,
  type RefObject,
} from "react";

export interface AutocompleteItem<T> {
  key: string;
  value: T;
  disabled?: boolean;
}

interface UseAutocompleteOptions<T> {
  items: AutocompleteItem<T>[];
  inputValue: string;
  onInputValueChange: (value: string) => void;
  onSelect: (value: T) => boolean | void;
  disabled?: boolean;
  busy?: boolean;
  preserveActiveKeyOnInputChange?: boolean;
  /**
   * Whether an option is exactly what was typed. With it, Enter while no option is highlighted picks
   * that option when it is the only one, instead of doing nothing; without it, Enter only picks a
   * highlighted option.
   */
  matchesInput?: (value: T, inputValue: string) => boolean;
}

interface AutocompleteInputProps {
  role: "combobox";
  "aria-autocomplete": "list";
  "aria-expanded": boolean;
  "aria-controls": string | undefined;
  "aria-activedescendant": string | undefined;
  onChange: (event: ChangeEvent<HTMLInputElement>) => void;
  onFocus: (event: FocusEvent<HTMLInputElement>) => void;
  onKeyDown: (event: KeyboardEvent<HTMLInputElement>) => void;
}

// The one item there is, or none when there are several and which is meant is unclear.
const onlyItem = <T>(items: T[]) => (items.length === 1 ? items[0] : undefined);

export function useAutocomplete<T>({
  items,
  inputValue,
  onInputValueChange,
  onSelect,
  disabled = false,
  busy = false,
  preserveActiveKeyOnInputChange = false,
  matchesInput,
}: UseAutocompleteOptions<T>) {
  const generatedId = useId();
  const listboxId = `autocomplete-${generatedId}`;
  const inputRef = useRef<HTMLInputElement>(null);
  const listboxRef = useRef<HTMLDivElement>(null);
  const optionElements = useRef(new Map<string, HTMLElement>());
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [isOpen, setIsOpen] = useState(false);

  const selectableItems = useMemo(() => items.filter((item) => !item.disabled), [items]);
  const selectableKeys = useMemo(() => selectableItems.map((item) => item.key), [selectableItems]);
  const itemKeys = useMemo(() => items.map((item) => item.key), [items]);

  const getOptionId = useCallback(
    (key: string) => `${listboxId}-option-${encodeURIComponent(key).replaceAll("%", "_")}`,
    [listboxId],
  );

  const close = useCallback(() => {
    setIsOpen(false);
    setActiveKey(null);
  }, []);

  const selectItem = useCallback(
    (item: AutocompleteItem<T>) => {
      if (item.disabled) return;
      const shouldClose = onSelect(item.value);
      if (shouldClose !== false) {
        close();
      }
    },
    [close, onSelect],
  );

  const [prevDisabled, setPrevDisabled] = useState(disabled);
  if (prevDisabled !== disabled) {
    setPrevDisabled(disabled);
    if (disabled) {
      setIsOpen(false);
      setActiveKey(null);
    }
  }

  const [previousInputValue, setPreviousInputValue] = useState(inputValue);
  if (previousInputValue !== inputValue) {
    setPreviousInputValue(inputValue);
    if (!preserveActiveKeyOnInputChange) setActiveKey(null);
    setIsOpen(!disabled && inputValue.trim().length > 0);
  }

  // Drop an active option that is gone, or that is disabled once loading has finished.
  if (activeKey != null && (!itemKeys.includes(activeKey) || (!busy && !selectableKeys.includes(activeKey)))) {
    setActiveKey(null);
  }

  useEffect(() => {
    if (activeKey == null) return;
    optionElements.current.get(activeKey)?.scrollIntoView?.({ block: "nearest" });
  }, [activeKey]);

  useEffect(() => {
    if (!isOpen) return;
    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target as Node | null;
      if (target && (inputRef.current?.contains(target) || listboxRef.current?.contains(target))) {
        return;
      }
      close();
    };
    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [close, isOpen]);

  const moveActive = useCallback(
    (direction: 1 | -1) => {
      if (selectableKeys.length === 0) return;
      setIsOpen(true);
      setActiveKey((current) => {
        if (current == null) {
          return direction === 1 ? selectableKeys[0] : selectableKeys[selectableKeys.length - 1];
        }
        const currentIndex = selectableKeys.indexOf(current);
        if (currentIndex < 0) {
          return direction === 1 ? selectableKeys[0] : selectableKeys[selectableKeys.length - 1];
        }
        const nextIndex = Math.max(0, Math.min(selectableKeys.length - 1, currentIndex + direction));
        return selectableKeys[nextIndex];
      });
    },
    [selectableKeys],
  );

  const inputProps: AutocompleteInputProps = {
    role: "combobox",
    "aria-autocomplete": "list",
    "aria-expanded": isOpen,
    "aria-controls": isOpen ? listboxId : undefined,
    "aria-activedescendant": activeKey == null ? undefined : getOptionId(activeKey),
    onChange: (event) => {
      const nextValue = event.target.value;
      if (!preserveActiveKeyOnInputChange) setActiveKey(null);
      setIsOpen(!disabled && nextValue.trim().length > 0);
      onInputValueChange(nextValue);
    },
    onFocus: () => {
      if (!disabled && inputValue.trim().length > 0) {
        setIsOpen(true);
      }
    },
    onKeyDown: (event) => {
      if (disabled) return;
      switch (event.key) {
        case "ArrowDown":
          if (selectableKeys.length === 0) return;
          event.preventDefault();
          moveActive(1);
          break;
        case "ArrowUp":
          if (selectableKeys.length === 0) return;
          event.preventDefault();
          moveActive(-1);
          break;
        case "Enter": {
          if (!isOpen) return;
          const item =
            activeKey != null
              ? items.find((candidate) => candidate.key === activeKey)
              : matchesInput
                ? onlyItem(selectableItems.filter((candidate) => matchesInput(candidate.value, inputValue)))
                : undefined;
          if (!item || item.disabled) return;
          event.preventDefault();
          selectItem(item);
          break;
        }
        case "Escape":
          if (!isOpen && inputValue.length === 0) return;
          event.preventDefault();
          event.stopPropagation();
          onInputValueChange("");
          close();
          break;
        case "Tab":
          close();
          break;
      }
    },
  };

  const listboxProps: HTMLAttributes<HTMLDivElement> = {
    id: listboxId,
    role: "listbox",
    "aria-busy": busy || undefined,
  };

  const getOptionProps = <TElement extends HTMLElement>(
    item: AutocompleteItem<T>,
  ): HTMLAttributes<TElement> & { ref: RefCallback<TElement> } => ({
    id: getOptionId(item.key),
    role: "option",
    "aria-selected": activeKey === item.key,
    "aria-disabled": item.disabled || undefined,
    tabIndex: -1,
    ref: (element: TElement | null) => {
      if (element) {
        optionElements.current.set(item.key, element);
      } else {
        optionElements.current.delete(item.key);
      }
    },
    onMouseMove: () => {
      if (!item.disabled) setActiveKey(item.key);
    },
    onMouseDown: (event) => event.preventDefault(),
    onClick: () => selectItem(item),
  });

  return {
    activeKey,
    close,
    getOptionProps,
    inputProps,
    inputRef,
    isOpen,
    listboxProps,
    listboxRef: listboxRef as RefObject<HTMLDivElement | null>,
  };
}
