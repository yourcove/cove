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

  describe("Enter with no option highlighted", () => {
    const matches = (value: string, input: string) => value.toLowerCase() === input.trim().toLowerCase();
    const render = (inputValue: string, matchesInput?: typeof matches, list = items) => {
      const onSelect = vi.fn();
      // Starts empty and then has the text typed, which opens the list as typing does.
      const hook = renderHook(
        ({ value }: { value: string }) =>
          useAutocomplete({ items: list, inputValue: value, onInputValueChange: vi.fn(), onSelect, matchesInput }),
        { initialProps: { value: "" } },
      );
      hook.rerender({ value: inputValue });
      return { ...hook, onSelect };
    };

    it("picks the option whose name is exactly what was typed", () => {
      const { result, onSelect } = render(" beta ", matches);
      pressKey(result, "Enter");
      expect(onSelect).toHaveBeenCalledWith("Beta");
    });

    it("does nothing for a partial match, a disabled match, two matches, or an owner that does not opt in", () => {
      const partial = render("bet", matches);
      pressKey(partial.result, "Enter");
      expect(partial.onSelect).not.toHaveBeenCalled();

      const disabled = render("beta", matches, [{ key: "b", value: "Beta", disabled: true }]);
      pressKey(disabled.result, "Enter");
      expect(disabled.onSelect).not.toHaveBeenCalled();

      const plain = render("beta");
      pressKey(plain.result, "Enter");
      expect(plain.onSelect).not.toHaveBeenCalled();

      // Two results with that name (performers told apart only by disambiguation): which is meant is unclear.
      const twice = render("beta", matches, [
        { key: "b1", value: "Beta" },
        { key: "b2", value: "Beta" },
      ]);
      pressKey(twice.result, "Enter");
      expect(twice.onSelect).not.toHaveBeenCalled();
    });
  });
});
