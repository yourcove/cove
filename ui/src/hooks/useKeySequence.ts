import { useEffect, useMemo, useRef, useCallback } from "react";
import { normalizeShortcutEvent, normalizeShortcutSequence } from "../keyboard/keybindings";
import { isOverlayOpen } from "../utils/overlayState";

type KeyBinding = {
  keys: string; // e.g. "g s", "d d", "r 5", "e", "Space"
  action: () => void;
  /** If true, this binding works even when an input/textarea is focused */
  global?: boolean;
};

/**
 * Multi-key sequence keyboard shortcut hook (like vim motions).
 * Supports single keys ("e"), two-key sequences ("g s"), and modifier combos ("Ctrl+Home").
 * 
 * Buffer resets after 800ms of no input.
 */
export function useKeySequence(bindings: KeyBinding[], enabled = true) {
  const bufferRef = useRef<string[]>([]);
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const normalizedBindings = useMemo(
    () => bindings
      .map((binding) => ({ ...binding, normalizedKeys: normalizeShortcutSequence(binding.keys) }))
      .filter((binding) => binding.normalizedKeys.length > 0),
    [bindings]
  );

  const clearBuffer = useCallback(() => {
    bufferRef.current = [];
    if (timerRef.current) clearTimeout(timerRef.current);
  }, []);

  useEffect(() => {
    if (!enabled) return;

    const handler = (e: KeyboardEvent) => {
      // While a full-screen overlay (e.g. the lightbox) owns the keyboard, don't also run page/app
      // shortcuts — otherwise a lightbox arrow key would advance the image AND page the list behind it.
      if (isOverlayOpen()) return;

      const tag = (e.target as HTMLElement)?.tagName;
      const inInput = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";

      const fullKey = normalizeShortcutEvent(e);
      if (!fullKey) return;

      // Push to buffer
      bufferRef.current.push(fullKey);
      if (timerRef.current) clearTimeout(timerRef.current);

      const bufferStr = bufferRef.current.join(" ");

      // Check for exact match
      const match = normalizedBindings.find((b) => {
        if (inInput && !b.global) return false;
        return b.normalizedKeys === bufferStr;
      });

      if (match) {
        e.preventDefault();
        e.stopPropagation();
        match.action();
        clearBuffer();
        return;
      }

      // Check if buffer could still be a prefix of any binding
      const couldMatch = normalizedBindings.some((b) => {
        if (inInput && !b.global) return false;
        return b.normalizedKeys.startsWith(`${bufferStr} `);
      });

      if (couldMatch) {
        timerRef.current = setTimeout(clearBuffer, 800);
      } else {
        clearBuffer();
      }
    };

    window.addEventListener("keydown", handler);
    return () => {
      window.removeEventListener("keydown", handler);
      if (timerRef.current) clearTimeout(timerRef.current);
    };
  }, [normalizedBindings, enabled, clearBuffer]);
}
