import { useMemo, useRef, useState } from "react";
import { ArrowLeft, ChevronDown } from "lucide-react";
import { FloatingActionMenu } from "./FloatingActionMenu";
import { useKeySequence } from "../hooks/useKeySequence";
import type { BackNavigationAlternative } from "../hooks/useBackNavigation";

interface BackNavigationButtonProps {
  label: string;
  onGoBack: () => void;
  /** The other place Back can lead; when present a menu next to the button offers both. */
  alternative?: BackNavigationAlternative;
  /** Classes for the wrapper that holds the button and its menu toggle. */
  className?: string;
  /** Classes for the Back button and the menu toggle. */
  buttonClassName?: string;
}

const MENU_ITEM_CLASS =
  "flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-card";

export function BackNavigationButton({
  label,
  onGoBack,
  alternative,
  className = "",
  buttonClassName = "",
}: BackNavigationButtonProps) {
  const [menuOpen, setMenuOpen] = useState(false);
  const menuAnchorRef = useRef<HTMLDivElement>(null);
  const goAlternative = alternative?.onSelect ?? onGoBack;

  const bindings = useMemo(
    () => [
      { id: "detail.back", keys: "b", surface: "detail" as const, action: onGoBack },
      { id: "detail.back.alternate", keys: "Shift+b", surface: "detail" as const, action: goAlternative },
    ],
    [goAlternative, onGoBack],
  );
  useKeySequence(bindings);

  const choose = (action: () => void) => {
    setMenuOpen(false);
    action();
  };

  return (
    <div ref={menuAnchorRef} className={["inline-flex items-center gap-1", className].filter(Boolean).join(" ")}>
      <button type="button" onClick={onGoBack} className={buttonClassName}>
        <ArrowLeft className="h-4 w-4" /> {label}
      </button>
      {alternative ? (
        <>
          <button
            type="button"
            aria-label="Choose where to go back to"
            aria-haspopup="menu"
            aria-expanded={menuOpen}
            title="Choose where to go back to"
            onClick={() => setMenuOpen((open) => !open)}
            className={buttonClassName}
          >
            <ChevronDown className="h-4 w-4" />
          </button>
          <FloatingActionMenu
            open={menuOpen}
            anchorRef={menuAnchorRef}
            onClose={() => setMenuOpen(false)}
            align="start"
            className="min-w-[200px] py-1"
          >
            <button type="button" role="menuitem" onClick={() => choose(onGoBack)} className={MENU_ITEM_CLASS}>
              {label}
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => choose(alternative.onSelect)}
              className={MENU_ITEM_CLASS}
            >
              {alternative.label}
            </button>
          </FloatingActionMenu>
        </>
      ) : null}
    </div>
  );
}
