import { useMemo, useState } from "react";
import { LIST_PER_PAGE_OPTIONS, toolbarSelectClass } from "./listToolbarStyles";

const CUSTOM_VALUE = "custom";

interface PageSizeSelectProps {
  /** Current page size. `0` together with `allowInfinite` means "Infinite". */
  perPage: number;
  /** Whether the "Infinite" option is offered. */
  allowInfinite: boolean;
  /** Whether the current page size is effectively infinite (perPage === 0 or forced). */
  infinitePageSize: boolean;
  /** When true the control is locked to infinite and disabled. */
  infinitePageSizeOnly?: boolean;
  /** Called with the chosen page size (`0` for infinite). */
  onChange: (perPage: number) => void;
}

/**
 * Page-size picker shared by the list and detail toolbars. Offers the preset
 * sizes plus optional "Infinite", and a "Custom…" entry that swaps in a number
 * input so the user can pick any size that isn't one of the presets.
 */
export function PageSizeSelect({ perPage, allowInfinite, infinitePageSize, infinitePageSizeOnly = false, onChange }: PageSizeSelectProps) {
  const [customOpen, setCustomOpen] = useState(false);
  const [customText, setCustomText] = useState("");

  const options = useMemo(() => {
    if (infinitePageSizeOnly) return [];
    // Surface a previously-applied custom size as its own option so it stays selected.
    if (infinitePageSize || LIST_PER_PAGE_OPTIONS.includes(perPage)) return LIST_PER_PAGE_OPTIONS;
    return [...LIST_PER_PAGE_OPTIONS, perPage].sort((left, right) => left - right);
  }, [infinitePageSize, infinitePageSizeOnly, perPage]);

  const closeCustom = () => {
    setCustomOpen(false);
    setCustomText("");
  };

  const applyCustom = () => {
    const parsed = Number(customText);
    if (Number.isInteger(parsed) && parsed > 0) {
      onChange(parsed);
    }
    closeCustom();
  };

  if (customOpen) {
    return (
      <input
        type="number"
        min={1}
        step={1}
        autoFocus
        value={customText}
        placeholder="Items"
        onChange={(e) => setCustomText(e.target.value)}
        onBlur={applyCustom}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            applyCustom();
          } else if (e.key === "Escape") {
            e.preventDefault();
            closeCustom();
          }
        }}
        className={`${toolbarSelectClass} w-[4.75rem]`}
        title="Custom items per page"
        aria-label="Custom items per page"
      />
    );
  }

  return (
    <select
      value={infinitePageSize ? "infinite" : String(perPage)}
      onChange={(e) => {
        const value = e.target.value;
        if (value === CUSTOM_VALUE) {
          setCustomText(infinitePageSize ? "" : String(perPage));
          setCustomOpen(true);
          return;
        }
        onChange(value === "infinite" ? 0 : Number(value));
      }}
      className={`${toolbarSelectClass} min-w-[4.75rem]`}
      title="Items per page"
      disabled={infinitePageSizeOnly}
    >
      {allowInfinite ? <option value="infinite">Infinite</option> : null}
      {options.map((n) => (
        <option key={n} value={n}>{n}</option>
      ))}
      {!infinitePageSizeOnly ? <option value={CUSTOM_VALUE}>Custom…</option> : null}
    </select>
  );
}
