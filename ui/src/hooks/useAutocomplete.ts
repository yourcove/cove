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

export function useAutocomplete<T>({
  items,
  inputValue,
  onInputValueChange,
  onSelect,
  disabled = false,
  busy = false,
}: UseAutocompleteOptions<T>) {
  const generatedId = useId();
  const listboxId = `autocomplete-${generatedId}`;
  const inputRef = useRef<HTMLInputElement>(null);
  const listboxRef = useRef<HTMLDivElement>(null);
  const optionElements = useRef(new Map<string, HTMLElement>());
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  const previousInputValue = useRef(inputValue);

  const selectableItems = useMemo(
    () => items.filter((item) => !item.disabled),
    [items],
  );
  const selectableKeys = useMemo(
    () => selectableItems.map((item) => item.key),
    [selectableItems],
  );

  const getOptionId = useCallback(
    (key: string) => `${listboxId}-option-${encodeURIComponent(key).replaceAll("%", "_")}`,
    [listboxId],
  );

  const close = useCallback(() => {
    setIsOpen(false);
    setActiveKey(null);
  }, []);

  const selectItem = useCallback((item: AutocompleteItem<T>) => {
    if (item.disabled) return;
    const shouldClose = onSelect(item.value);
    if (shouldClose !== false) {
      close();
    }
  }, [close, onSelect]);

  useEffect(() => {
    if (disabled) {
      close();
    }
  }, [close, disabled]);

  useEffect(() => {
    if (previousInputValue.current === inputValue) return;
    previousInputValue.current = inputValue;
    setActiveKey(null);
    setIsOpen(!disabled && inputValue.trim().length > 0);
  }, [disabled, inputValue]);

  useEffect(() => {
    if (activeKey != null && !selectableKeys.includes(activeKey)) {
      setActiveKey(null);
    }
  }, [activeKey, selectableKeys]);

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

  const moveActive = useCallback((direction: 1 | -1) => {
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
  }, [selectableKeys]);

  const inputProps: AutocompleteInputProps = {
    role: "combobox",
    "aria-autocomplete": "list",
    "aria-expanded": isOpen,
    "aria-controls": isOpen ? listboxId : undefined,
    "aria-activedescendant": activeKey == null ? undefined : getOptionId(activeKey),
    onChange: (event) => {
      const nextValue = event.target.value;
      setActiveKey(null);
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
          if (!isOpen || activeKey == null) return;
          const item = items.find((candidate) => candidate.key === activeKey);
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

  const getOptionProps = <TElement extends HTMLElement>(item: AutocompleteItem<T>): HTMLAttributes<TElement> & { ref: RefCallback<TElement> } => ({
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
