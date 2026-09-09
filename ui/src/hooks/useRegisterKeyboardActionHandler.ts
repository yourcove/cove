import { useMemo } from "react";
import {
  useKeyboardShortcuts,
  useRegisterKeyboardActions,
  type KeyboardActionInvocation,
  type KeyboardActionRegistration,
} from "../keyboard/KeyboardShortcutProvider";
import type { KeyboardShortcutSurface } from "../keyboard/registry";

export interface ExtensionKeyboardActionRegistration {
  id: string;
  action: (context: KeyboardActionInvocation) => void;
  enabled?: boolean;
  /** A surface declared for this action in the extension manifest. */
  surface?: KeyboardShortcutSurface;
}

function requireIdentifier(value: string, label: string) {
  const normalized = value.trim();
  if (!normalized) throw new Error(`${label} is required.`);
  return normalized;
}

export function scopeExtensionKeyboardRegistrations(
  extensionId: string,
  registrations: ExtensionKeyboardActionRegistration[],
): KeyboardActionRegistration[] {
  const owner = requireIdentifier(extensionId, "Extension id");
  const seen = new Set<string>();
  return registrations.map((registration) => {
    const localId = requireIdentifier(registration.id, "Extension keyboard action id");
    if (seen.has(localId)) throw new Error(`Duplicate extension keyboard action id '${localId}'.`);
    seen.add(localId);
    return {
      id: `extension:${owner}:${localId}`,
      action: registration.action,
      enabled: registration.enabled,
      surface: registration.surface,
    };
  });
}

export function selectExtensionKeyboardBindings(
  extensionId: string,
  effectiveBindings: Record<string, string[]>,
): Readonly<Record<string, readonly string[]>> {
  const prefix = `extension:${requireIdentifier(extensionId, "Extension id")}:`;
  return Object.fromEntries(
    Object.entries(effectiveBindings)
      .filter(([actionId]) => actionId.startsWith(prefix))
      .map(([actionId, bindings]) => [actionId.slice(prefix.length), [...bindings]]),
  );
}

/**
 * Connect a manifest-declared keyboard action to mounted component state.
 * Extensions must pass the authoritative namespaced id returned by Cove's manifest.
 */
export function useRegisterKeyboardActionHandler(
  actionId: string,
  handler: (context: KeyboardActionInvocation) => void,
  options: { enabled?: boolean; surface?: KeyboardShortcutSurface } = {},
) {
  const registrations = useMemo(
    () => [
      {
        id: actionId,
        enabled: options.enabled ?? true,
        surface: options.surface ?? ("local" as const),
        action: handler,
      },
    ],
    [actionId, handler, options.enabled, options.surface],
  );
  useRegisterKeyboardActions(registrations);
}

/**
 * Register mounted handlers for manifest-declared actions owned by one extension.
 * Cove applies the extension namespace and remains responsible for bindings, declared
 * surface validation, and dispatch. Registration lifetime supplies the local page/tab scope.
 * The namespace prevents accidental collisions; it is not a security boundary between scripts.
 */
export function useRegisterExtensionKeyboardActions(
  extensionId: string,
  registrations: ExtensionKeyboardActionRegistration[],
) {
  useRegisterKeyboardActions(scopeExtensionKeyboardRegistrations(extensionId, registrations));
}

/** Read one extension's resolved bindings, keyed by extension-local manifest action id. */
export function useExtensionKeyboardBindings(extensionId: string) {
  const { effectiveBindings } = useKeyboardShortcuts();
  return useMemo(
    () => selectExtensionKeyboardBindings(extensionId, effectiveBindings),
    [effectiveBindings, extensionId],
  );
}
