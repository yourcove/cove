import type { MediaDetailKeyboardShortcut } from "./types";
import { useKeySequence } from "../../hooks/useKeySequence";

interface UseMediaDetailLayoutOptions {
  keyboardShortcuts?: MediaDetailKeyboardShortcut[];
}

export function useMediaDetailLayout({
  keyboardShortcuts = [],
}: UseMediaDetailLayoutOptions) {
  useKeySequence(keyboardShortcuts.map((shortcut) => ({
    id: shortcut.id,
    keys: shortcut.key,
    surface: "detail" as const,
    action: shortcut.handler,
  })));
}
