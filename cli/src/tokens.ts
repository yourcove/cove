import { CliError } from "./errors";
import type { CoveClient } from "./client";
import type { MeResponse } from "./types";

/**
 * Read-only scope preset. Includes the read permissions of
 * Permissions.ViewerDefaults from src/Cove.Core/Auth/Permissions.cs, plus file
 * metadata access, which is useful to API clients without exposing file
 * mutation operations. The Viewer role's own saved-filter write and delete
 * keys stay out, so a token made from this preset changes no library data or
 * saved filters.
 */
const VIEWER_SCOPE = [
  "videos.read", "audios.read", "texts.read", "performers.read", "tags.read", "studios.read",
  "taggroups.read", "galleries.read", "images.read", "groups.read", "segments.read",
  "faces.read", "embeddings.read", "airuns.read", "files.read",
  "savedfilters.read", "jobs.read", "extensions.read", "system.read", "stream.read",
];

export const SCOPE_PRESETS: Record<string, string[]> = { viewer: VIEWER_SCOPE };
export const SCOPE_PRESET_NAMES = Object.keys(SCOPE_PRESETS).sort();

/**
 * Read permissions a scoped read grant can satisfy. Mirrors
 * CovePrincipal.TryGetReadGrantEntityKind in src/Cove.Core/Auth/ICurrentPrincipalAccessor.cs.
 */
const READ_GRANT_ENTITY_KINDS: Record<string, string> = {
  "videos.read": "video",
  "audios.read": "audio",
  "texts.read": "text",
  "performers.read": "performer",
  "faces.read": "face",
  "tags.read": "tag",
  "studios.read": "studio",
  "galleries.read": "gallery",
  "images.read": "image",
  "groups.read": "group",
  "segments.read": "segment",
  "files.read": "file",
};

const PERMISSION_KEY = /^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$/;

export interface ScopeSelection {
  /** Permission keys to send as the token scope, or undefined for the owner's full permissions. */
  keys?: string[];
  /** Keys the caller named directly, rather than reaching through a preset. */
  explicit: string[];
  presets: string[];
}

/**
 * Expands `--scope` values into permission keys. Values are comma-separated or
 * repeated, and each is either a preset name or a literal permission key.
 */
export function parseScope(values: string[]): ScopeSelection {
  const entries = values.flatMap(value => value.split(",")).map(value => value.trim()).filter(value => value !== "");
  if (!values.length) return { explicit: [], presets: [] };
  if (!entries.length) throw new CliError("INVALID_ARGUMENT", "--scope must name at least one permission or preset.");

  const keys: string[] = [];
  const explicit: string[] = [];
  const presets: string[] = [];
  for (const entry of entries) {
    // hasOwn, not a truthiness check: "toString" and "constructor" reach Object.prototype.
    if (Object.hasOwn(SCOPE_PRESETS, entry)) {
      if (!presets.includes(entry)) presets.push(entry);
      keys.push(...SCOPE_PRESETS[entry]!);
      continue;
    }
    if (entry === "*") {
      throw new CliError("INVALID_ARGUMENT", "A token scope lists the permissions to keep. Omit --scope to use the full permissions of the owner.");
    }
    if (entry.endsWith(".*") || entry.startsWith("*.")) {
      throw new CliError("INVALID_ARGUMENT", `Cove rejects wildcard token scopes such as “${entry}”. List explicit permission keys instead.`);
    }
    if (!PERMISSION_KEY.test(entry)) {
      const hint = PERMISSION_KEY.test(entry.toLowerCase())
        ? " Permission keys are lowercase."
        : ` Expected a permission key such as videos.read, or a scope preset (${SCOPE_PRESET_NAMES.join(", ")}).`;
      throw new CliError("INVALID_ARGUMENT", `“${entry}” is not a permission key or a scope preset.${hint}`);
    }
    keys.push(entry);
    if (!explicit.includes(entry)) explicit.push(entry);
  }
  return { keys: [...new Set(keys)], explicit, presets };
}

/**
 * Mirrors CovePrincipal.Has: the literal key, the superuser wildcard, and the
 * `<resource>.*` and `*.<verb>` wildcards.
 */
export function grantsPermission(held: readonly string[], permission: string): boolean {
  if (held.includes("*") || held.includes(permission)) return true;
  const dot = permission.indexOf(".");
  if (dot < 0) return false;
  return held.includes(`${permission.slice(0, dot)}.*`) || held.includes(`*.${permission.slice(dot + 1)}`);
}

export interface HeldPermissions {
  permissions: string[];
  readGrantedEntityKinds: string[];
}

/**
 * Reads the current identity's effective permissions, or undefined when the
 * server cannot report them (anonymous access, or an identity endpoint that
 * rejects this credential). Callers treat undefined as "cannot pre-check".
 */
export async function heldPermissions(client: CoveClient): Promise<HeldPermissions | undefined> {
  let me: MeResponse;
  try {
    me = await client.get<MeResponse>("auth/me");
  } catch (error) {
    if (error instanceof CliError && error.status === 401) return undefined;
    throw error;
  }
  if (!Array.isArray(me.permissions)) return undefined;
  return {
    permissions: me.permissions.filter(value => typeof value === "string"),
    readGrantedEntityKinds: Array.isArray(me.readGrantedEntityKinds)
      ? me.readGrantedEntityKinds.filter(value => typeof value === "string")
      : [],
  };
}

/**
 * Scope keys the current identity does not appear to hold. Cove refuses to
 * issue a token whose scope exceeds its owner, so these are the keys that make
 * a create request fail — the server stays authoritative.
 */
export function unheldScope(held: HeldPermissions, keys: readonly string[]): string[] {
  return keys.filter(key => {
    if (grantsPermission(held.permissions, key)) return false;
    const entityKind = READ_GRANT_ENTITY_KINDS[key];
    return !(entityKind && held.readGrantedEntityKinds.some(kind => kind.toLowerCase() === entityKind));
  });
}
