import { normalizeShortcutSequence } from "./keybindings";

export const KEYBOARD_PRESET_SCHEMA_VERSION = 1 as const;
export const MAX_SHORTCUT_STROKES = 3;
export const MAX_SHORTCUT_ALTERNATIVES = 8;

export type KeyboardShortcutSurface = "global" | "page" | "list" | "detail" | "player" | "viewer" | "overlay" | "local";

export interface KeyboardActionScope {
  surface: KeyboardShortcutSurface;
  page?: string;
  entityType?: string;
  tab?: string;
}

export interface KeyboardActionDefinition {
  id: string;
  group: string;
  label: string;
  description?: string;
  defaultBindings: string[];
  scopes?: KeyboardActionScope[];
  source?: "cove" | "extension";
  extensionId?: string;
  repeatable?: boolean;
  allowInEditable?: boolean;
  requiredPermission?: string;
}

export interface KeyboardShortcutPreset {
  schemaVersion: typeof KEYBOARD_PRESET_SCHEMA_VERSION;
  id: string;
  name: string;
  description?: string;
  author?: string;
  version?: string;
  basePresetId?: string;
  unmappedActions: "action-defaults" | "unbound";
  bindings: Record<string, string[]>;
  requirements?: {
    extensions?: Array<{ id: string; minimumVersion?: string }>;
  };
  provenance?: {
    source: "cove" | "extension" | "import" | "personal" | "instance";
    providerId?: string;
    originalPresetId?: string;
  };
}

export interface ResolvedKeyboardPreset {
  preset: KeyboardShortcutPreset;
  bindings: Record<string, string[]>;
  lineage: string[];
}

export interface KeyboardPresetValidationResult {
  valid: boolean;
  errors: string[];
}

export interface KeyboardDispatchCandidate<T> {
  actionId: string;
  sequence: string;
  priority: number;
  value: T;
}

export type KeyboardDispatchResolution<T> =
  | { kind: "none" }
  | { kind: "prefix" }
  | { kind: "action"; candidate: KeyboardDispatchCandidate<T> }
  | { kind: "conflict"; actionIds: string[] };

export function getKeyboardSequenceContinuations<T>(candidates: KeyboardDispatchCandidate<T>[], sequence: string) {
  const strokeCount = sequence.split(" ").filter(Boolean).length;
  const matching = candidates.filter((candidate) => candidate.sequence.startsWith(`${sequence} `));
  if (matching.length === 0) return [];
  const highestPriority = Math.max(...matching.map((candidate) => candidate.priority));
  return [
    ...new Set(
      matching
        .filter((candidate) => candidate.priority === highestPriority)
        .map((candidate) => candidate.sequence.split(" ")[strokeCount])
        .filter((stroke): stroke is string => !!stroke),
    ),
  ];
}

/** Resolve one buffered sequence. More-specific surfaces win; peer and prefix collisions block. */
export function resolveKeyboardDispatch<T>(
  candidates: KeyboardDispatchCandidate<T>[],
  sequence: string,
): KeyboardDispatchResolution<T> {
  const relevant = candidates.filter(
    (candidate) => candidate.sequence === sequence || candidate.sequence.startsWith(`${sequence} `),
  );
  if (relevant.length === 0) return { kind: "none" };
  const highestPriority = Math.max(...relevant.map((candidate) => candidate.priority));
  const highest = relevant.filter((candidate) => candidate.priority === highestPriority);
  const exact = highest.filter((candidate) => candidate.sequence === sequence);
  const longer = highest.filter((candidate) => candidate.sequence.startsWith(`${sequence} `));
  // Several longer sequences may legitimately share a prefix (for example every "g …"
  // navigation chord). They conflict only when an exact action makes the prefix ambiguous.
  const exactActionIds = new Set(exact.map((candidate) => candidate.actionId));
  const competingActionIds = new Set([...exact, ...longer].map((candidate) => candidate.actionId));
  if (exact.length > 0 && competingActionIds.size > 1) {
    return { kind: "conflict", actionIds: [...competingActionIds] };
  }
  if (exactActionIds.size > 1) return { kind: "conflict", actionIds: [...exactActionIds] };
  if (exact.length > 0) return { kind: "action", candidate: exact[exact.length - 1] };
  return { kind: "prefix" };
}

function normalizePresetBinding(value: string) {
  return normalizeShortcutSequence(value.replace(/\bmod\b/gi, "Ctrl"));
}

export function normalizePresetBindings(bindings: string[] | null | undefined) {
  const normalized = (bindings ?? []).map(normalizePresetBinding).filter(Boolean);
  return [...new Set(normalized)].slice(0, MAX_SHORTCUT_ALTERNATIVES);
}

export function validateKeyboardPreset(value: KeyboardShortcutPreset): KeyboardPresetValidationResult {
  const errors: string[] = [];
  if (value.schemaVersion !== KEYBOARD_PRESET_SCHEMA_VERSION)
    errors.push("Unsupported keyboard preset schema version.");
  if (!value.id?.trim()) errors.push("Preset id is required.");
  if (!value.name?.trim()) errors.push("Preset name is required.");
  if (value.unmappedActions !== "action-defaults" && value.unmappedActions !== "unbound") {
    errors.push("Preset unmappedActions must be action-defaults or unbound.");
  }

  for (const [actionId, alternatives] of Object.entries(value.bindings ?? {})) {
    if (!actionId.trim()) errors.push("Preset action ids cannot be empty.");
    if (!Array.isArray(alternatives)) {
      errors.push(`Bindings for '${actionId}' must be an array.`);
      continue;
    }
    if (alternatives.length > MAX_SHORTCUT_ALTERNATIVES) {
      errors.push(`Bindings for '${actionId}' may contain at most ${MAX_SHORTCUT_ALTERNATIVES} alternatives.`);
    }
    for (const alternative of alternatives) {
      const normalized = normalizePresetBinding(alternative);
      if (!normalized) errors.push(`Bindings for '${actionId}' contain an empty shortcut.`);
      if (normalized.split(" ").length > MAX_SHORTCUT_STROKES) {
        errors.push(`Bindings for '${actionId}' may contain at most three strokes.`);
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

export function resolveKeyboardPreset(
  preset: KeyboardShortcutPreset,
  availablePresets: KeyboardShortcutPreset[],
  actions: KeyboardActionDefinition[],
): ResolvedKeyboardPreset {
  const presetById = new Map(availablePresets.map((entry) => [entry.id, entry]));
  presetById.set(preset.id, preset);
  const resolving = new Set<string>();
  const resolved = new Map<string, ResolvedKeyboardPreset>();

  const visit = (current: KeyboardShortcutPreset): ResolvedKeyboardPreset => {
    const cached = resolved.get(current.id);
    if (cached) return cached;
    if (resolving.has(current.id)) throw new Error(`Keyboard preset inheritance cycle at '${current.id}'.`);
    const validation = validateKeyboardPreset(current);
    if (!validation.valid) throw new Error(validation.errors.join(" "));

    resolving.add(current.id);
    const inherited = current.basePresetId
      ? (() => {
          const parent = presetById.get(current.basePresetId!);
          if (!parent)
            throw new Error(`Keyboard preset '${current.id}' requires missing base '${current.basePresetId}'.`);
          return visit(parent);
        })()
      : null;

    const bindings: Record<string, string[]> = inherited
      ? Object.fromEntries(Object.entries(inherited.bindings).map(([id, keys]) => [id, [...keys]]))
      : current.unmappedActions === "action-defaults"
        ? Object.fromEntries(actions.map((action) => [action.id, normalizePresetBindings(action.defaultBindings)]))
        : Object.fromEntries(actions.map((action) => [action.id, []]));

    for (const [actionId, alternatives] of Object.entries(current.bindings)) {
      bindings[actionId] = normalizePresetBindings(alternatives);
    }

    resolving.delete(current.id);
    const result = {
      preset: current,
      bindings,
      lineage: [...(inherited?.lineage ?? []), current.id],
    };
    resolved.set(current.id, result);
    return result;
  };

  return visit(preset);
}

export function flattenKeyboardPreset(
  preset: KeyboardShortcutPreset,
  availablePresets: KeyboardShortcutPreset[],
  actions: KeyboardActionDefinition[],
  identity: { id: string; name: string },
): KeyboardShortcutPreset {
  const resolved = resolveKeyboardPreset(preset, availablePresets, actions);
  return {
    schemaVersion: KEYBOARD_PRESET_SCHEMA_VERSION,
    id: identity.id,
    name: identity.name,
    description: preset.description,
    author: preset.author,
    version: preset.version,
    unmappedActions: "unbound",
    bindings: resolved.bindings,
    requirements: preset.requirements,
    provenance: {
      source: "personal",
      providerId: preset.provenance?.providerId,
      originalPresetId: preset.id,
    },
  };
}
