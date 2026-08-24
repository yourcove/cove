import { useMemo } from "react";
import { useRegisterKeyboardActions } from "../keyboard/KeyboardShortcutProvider";
import type { KeyboardShortcutSurface } from "../keyboard/registry";

/**
 * Connect a manifest-declared keyboard action to mounted component state.
 * Extensions must pass the authoritative namespaced id returned by Cove's manifest.
 */
export function useRegisterKeyboardActionHandler(
  actionId: string,
  handler: () => void,
  options: { enabled?: boolean; surface?: KeyboardShortcutSurface } = {},
) {
  const registrations = useMemo(() => [{
    id: actionId,
    keys: "",
    enabled: options.enabled ?? true,
    surface: options.surface ?? "local" as const,
    action: handler,
  }], [actionId, handler, options.enabled, options.surface]);
  useRegisterKeyboardActions(registrations);
}
