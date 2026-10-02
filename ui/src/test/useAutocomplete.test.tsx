import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { type AutocompleteItem, useAutocomplete } from "../hooks/useAutocomplete";

type Props = {
  items: AutocompleteItem<string>[];
  inputValue: string;
  disabled?: boolean;
  busy?: boolean;
  preserveActiveKeyOnInputChange?: boolean;
};

const items: AutocompleteItem<string>[] = [
  { key: "a", value: "Alpha" },
  { key: "b", value: "Beta" },
];

function renderAutocomplete(initialProps: Props) {
  return renderHook((props: Props) => useAutocomplete({ ...props, onInputValueChange: vi.fn(), onSelect: vi.fn() }), {
    initialProps,
  });
}

function pressKey(result: { current: ReturnType<typeof useAutocomplete<string>> }, key: string) {
  act(() =>
    result.current.inputProps.onKeyDown({
      key,
      preventDefault: () => undefined,
      stopPropagation: () => undefined,
    } as never),
  );
}

describe("useAutocomplete", () => {
  it("opens for a non-empty external input value and clears the active option", () => {
    const { result, rerender } = renderAutocomplete({ items, inputValue: "" });
    expect(result.current.isOpen).toBe(false);

    rerender({ items, inputValue: "al" });
    expect(result.current.isOpen).toBe(true);

    pressKey(result, "ArrowDown");
    expect(result.current.activeKey).toBe("a");

    rerender({ items, inputValue: "alp" });
    expect(result.current.activeKey).toBeNull();
    expect(result.current.isOpen).toBe(true);

    rerender({ items, inputValue: " " });
    expect(result.current.isOpen).toBe(false);
  });

  it("keeps the active option across input changes when asked to", () => {
    const { result, rerender } = renderAutocomplete({
      items,
      inputValue: "a",
      preserveActiveKeyOnInputChange: true,
    });

    pressKey(result, "ArrowDown");
    rerender({ items, inputValue: "ab", preserveActiveKeyOnInputChange: true });
    expect(result.current.activeKey).toBe("a");
  });

  it("closes and clears the active option when disabled", () => {
    const { result, rerender } = renderAutocomplete({ items, inputValue: "a" });
    pressKey(result, "ArrowDown");
    expect(result.current.isOpen).toBe(true);

    rerender({ items, inputValue: "a", disabled: true });
    expect(result.current.isOpen).toBe(false);
    expect(result.current.activeKey).toBeNull();
  });

  it("drops an active option that disappears or is disabled once loading finishes", () => {
    const { result, rerender } = renderAutocomplete({ items, inputValue: "a" });
    pressKey(result, "ArrowDown");
    expect(result.current.activeKey).toBe("a");

    const disabledFirst = [{ ...items[0], disabled: true }, items[1]];
    rerender({ items: disabledFirst, inputValue: "a", busy: true });
    expect(result.current.activeKey).toBe("a");

    rerender({ items: disabledFirst, inputValue: "a", busy: false });
    expect(result.current.activeKey).toBeNull();

    pressKey(result, "ArrowDown");
    expect(result.current.activeKey).toBe("b");

    rerender({ items: [items[0]], inputValue: "a" });
    expect(result.current.activeKey).toBeNull();
  });
});
