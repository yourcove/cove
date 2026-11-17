import { useCallback, useState } from "react";

/**
 * A boolean the viewer can set, remembered in this browser. Until they set it, the caller's default applies and keeps
 * applying as it changes, for example when a player that stays mounted moves from a tag segment to a face segment,
 * or once a video's faces have loaded.
 */
export function usePersistedFlag(
  key: string,
  defaultValue: boolean,
): [boolean, (next: boolean | ((prev: boolean) => boolean)) => void] {
  const [stored, setStored] = useState<boolean | undefined>(() => readPersistedFlag(key));
  const value = stored ?? defaultValue;

  const setPersistedValue = useCallback(
    (next: boolean | ((prev: boolean) => boolean)) => {
      setStored((previous) => {
        const resolved =
          typeof next === "function" ? (next as (prev: boolean) => boolean)(previous ?? defaultValue) : next;
        try {
          window.localStorage.setItem(key, resolved ? "true" : "false");
        } catch {
          // Ignore storage access failures.
        }
        return resolved;
      });
    },
    [defaultValue, key],
  );

  return [value, setPersistedValue];
}

// The viewer's own choice, or undefined while they have never made one.
function readPersistedFlag(key: string): boolean | undefined {
  if (typeof window === "undefined") return undefined;
  try {
    const raw = window.localStorage.getItem(key);
    if (raw === "true") return true;
    if (raw === "false") return false;
  } catch {
    // Ignore storage access failures.
  }
  return undefined;
}
