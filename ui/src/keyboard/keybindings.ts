export type KeybindingDefinition = {
  id: string;
  group: string;
  label: string;
  keys: string;
};

type KeyboardLikeEvent = Pick<KeyboardEvent, "key" | "ctrlKey" | "metaKey" | "altKey" | "shiftKey">;

function isSingleCharacterLetter(value: string) {
  return Array.from(value).length === 1 && value.toLowerCase() !== value.toUpperCase();
}

export const KEYBINDING_DEFAULTS: KeybindingDefinition[] = [
  { id: "global.home", group: "Global Navigation", label: "Home", keys: "g h" },
  { id: "global.videos", group: "Global Navigation", label: "Videos", keys: "g s" },
  { id: "global.audios", group: "Global Navigation", label: "Audios", keys: "g a" },
  { id: "global.texts", group: "Global Navigation", label: "Texts", keys: "g x" },
  { id: "global.segments", group: "Global Navigation", label: "Segments", keys: "g m" },
  { id: "global.faces", group: "Global Navigation", label: "Faces", keys: "g f" },
  { id: "global.images", group: "Global Navigation", label: "Images", keys: "g i" },
  { id: "global.groups", group: "Global Navigation", label: "Groups", keys: "g v" },
  { id: "global.galleries", group: "Global Navigation", label: "Galleries", keys: "g l" },
  { id: "global.performers", group: "Global Navigation", label: "Performers", keys: "g p" },
  { id: "global.studios", group: "Global Navigation", label: "Studios", keys: "g u" },
  { id: "global.tags", group: "Global Navigation", label: "Tags", keys: "g t" },
  { id: "global.settings", group: "Global Navigation", label: "Settings", keys: "g z" },
  { id: "global.stats", group: "Global Navigation", label: "Stats", keys: "g d" },
  { id: "list.search", group: "List Pages", label: "Focus search", keys: "/" },
  { id: "list.view.grid", group: "List Pages", label: "Grid view", keys: "v g" },
  { id: "list.view.list", group: "List Pages", label: "List view", keys: "v l" },
  { id: "list.view.wall", group: "List Pages", label: "Wall view", keys: "v w" },
  { id: "list.view.tagger", group: "List Pages", label: "Tagger view", keys: "v t" },
  { id: "list.view.graph", group: "List Pages", label: "Graph view", keys: "v h" },
  { id: "list.view.group", group: "List Pages", label: "Group view", keys: "v b" },
  { id: "list.select.all", group: "List Pages", label: "Select all", keys: "s a" },
  { id: "list.select.none", group: "List Pages", label: "Select none", keys: "s n" },
  { id: "list.select.invert", group: "List Pages", label: "Invert selection", keys: "s i" },
  { id: "list.page.previous", group: "List Pages", label: "Previous page", keys: "ArrowLeft" },
  { id: "list.page.next", group: "List Pages", label: "Next page", keys: "ArrowRight" },
  { id: "list.page.back10", group: "List Pages", label: "Back 10 pages", keys: "Shift+ArrowLeft" },
  { id: "list.page.forward10", group: "List Pages", label: "Forward 10 pages", keys: "Shift+ArrowRight" },
  { id: "list.page.first", group: "List Pages", label: "First page", keys: "Ctrl+Home" },
  { id: "list.page.last", group: "List Pages", label: "Last page", keys: "Ctrl+End" },
  { id: "list.filters", group: "List Pages", label: "Filters", keys: "f" },
  { id: "list.zoom.in", group: "List Pages", label: "Zoom in", keys: "+" },
  { id: "list.zoom.out", group: "List Pages", label: "Zoom out", keys: "-" },
];

export const KEYBINDING_GROUPS = Array.from(
  KEYBINDING_DEFAULTS.reduce((groups, definition) => {
    const existing = groups.get(definition.group) ?? [];
    existing.push(definition);
    groups.set(definition.group, existing);
    return groups;
  }, new Map<string, KeybindingDefinition[]>()).entries(),
).map(([group, definitions]) => ({ group, definitions }));

export function resolveKeybinding(overrides: Record<string, string> | undefined, id: string, fallback: string) {
  const override = normalizeShortcutSequence(overrides?.[id]);
  return override || normalizeShortcutSequence(fallback);
}

export function keybindingDefault(id: string) {
  return KEYBINDING_DEFAULTS.find((definition) => definition.id === id)?.keys ?? "";
}

export function normalizeShortcutKeyName(key: string | null | undefined) {
  if (key === " ") {
    return "Space";
  }

  const raw = (key ?? "").trim();
  if (!raw) {
    return null;
  }

  const lower = raw.toLowerCase();
  switch (lower) {
    case " ":
    case "space":
    case "spacebar":
      return "Space";
    case "esc":
    case "escape":
      return "Escape";
    case "left":
    case "arrowleft":
      return "ArrowLeft";
    case "right":
    case "arrowright":
      return "ArrowRight";
    case "up":
    case "arrowup":
      return "ArrowUp";
    case "down":
    case "arrowdown":
      return "ArrowDown";
    case "return":
    case "enter":
      return "Enter";
    case "del":
    case "delete":
      return "Delete";
    case "control":
    case "ctrl":
    case "shift":
    case "alt":
    case "meta":
    case "cmd":
    case "command":
      return null;
    default:
      return isSingleCharacterLetter(raw) ? raw.toLowerCase() : raw;
  }
}

export function normalizeShortcutEvent(event: KeyboardLikeEvent) {
  const key = normalizeShortcutKeyName(event.key);
  if (!key) {
    return null;
  }

  const parts: string[] = [];
  if (event.ctrlKey || event.metaKey) parts.push("Ctrl");
  if (event.altKey) parts.push("Alt");
  if (event.shiftKey && (key.length > 1 || isSingleCharacterLetter(key))) parts.push("Shift");
  parts.push(key);
  return parts.join("+");
}

export function normalizeShortcutSequence(value: string | null | undefined) {
  return (value ?? "")
    .split(/\s+/)
    .map(normalizeShortcutStroke)
    .filter((part): part is string => !!part)
    .join(" ");
}

function normalizeShortcutStroke(value: string) {
  const stroke = value.trim();
  if (!stroke) {
    return null;
  }

  if (stroke === "+") {
    return "+";
  }

  const rawParts = stroke.endsWith("+")
    ? [...stroke.slice(0, -1).split("+").filter(Boolean), "+"]
    : stroke.split("+").filter(Boolean);

  const modifiers = new Set<string>();
  let key: string | null = null;
  for (const part of rawParts) {
    const normalized = part.trim().toLowerCase();
    if (!normalized) {
      continue;
    }

    if (
      normalized === "ctrl" ||
      normalized === "control" ||
      normalized === "cmd" ||
      normalized === "command" ||
      normalized === "meta"
    ) {
      modifiers.add("Ctrl");
      continue;
    }
    if (normalized === "alt" || normalized === "option") {
      modifiers.add("Alt");
      continue;
    }
    if (normalized === "shift") {
      modifiers.add("Shift");
      continue;
    }

    key = normalizeShortcutKeyName(part);
  }

  if (!key) {
    return null;
  }

  const orderedModifiers = [
    "Ctrl",
    "Alt",
    ...((key.length > 1 || isSingleCharacterLetter(key)) && modifiers.has("Shift") ? ["Shift"] : []),
  ].filter((modifier) => modifiers.has(modifier));
  return [...orderedModifiers, key].join("+");
}
