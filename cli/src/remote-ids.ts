import type { CoveClient } from "./client";
import { CliError } from "./errors";
import type { PaginatedResponse } from "./types";

/** A remote (metadata server) ID; an absent endpoint matches the ID on any endpoint. */
export interface RemoteIdReference {
  endpoint?: string;
  remoteId: string;
}

export type RemoteIdResource = "videos" | "performers" | "studios" | "tags";

/**
 * Parses `endpoint=id` or a bare `id`. Cove stores endpoints as full GraphQL URLs, so an endpoint given
 * without a scheme is expanded the stash-box way: `stashdb.org` becomes `https://stashdb.org/graphql`.
 */
export function parseRemoteId(value: string): RemoteIdReference {
  const separator = value.indexOf("=");
  const endpointText = separator < 0 ? "" : value.slice(0, separator).trim();
  const remoteId = (separator < 0 ? value : value.slice(separator + 1)).trim();
  if (!remoteId) throw new CliError("INVALID_ARGUMENT", "--remote-id must use endpoint=id or id.");
  if (!endpointText) return { remoteId };
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(endpointText)) return { endpoint: endpointText, remoteId };
  return { endpoint: `https://${endpointText}${endpointText.includes("/") ? "" : "/graphql"}`, remoteId };
}

/** Object filter matching an entity that holds this remote ID, on the given endpoint when one is named. */
export function remoteIdObjectFilter(reference: RemoteIdReference | undefined): Record<string, unknown> {
  if (!reference) return {};
  return {
    // Always sent, blank for any endpoint, so an endpoint criterion from a saved filter cannot pair with this ID.
    remoteIdCriterion: { value: reference.endpoint ?? "", modifier: "equals" },
    remoteIdValueCriterion: { value: reference.remoteId, modifier: "equals" },
  };
}

function describe(reference: RemoteIdReference): string {
  return reference.endpoint ? `${reference.endpoint}=${reference.remoteId}` : reference.remoteId;
}

/** Finds the single entity holding a remote ID and returns its complete record. */
export async function resolveByRemoteId<T>(client: CoveClient, resource: RemoteIdResource, reference: RemoteIdReference, validate: (value: unknown) => value is T): Promise<T> {
  const singular = resource.replace(/s$/, "");
  const page = await client.post<PaginatedResponse<unknown>>(`${resource}/find`, {
    findFilter: { page: 1, perPage: 10, sort: "created_at", direction: "asc" },
    objectFilter: remoteIdObjectFilter(reference),
  });
  if (!page || !Array.isArray(page.items) || typeof page.totalCount !== "number") {
    throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${singular} list response.`);
  }
  const ids = page.items.map(item => (item as { id?: unknown } | undefined)?.id);
  if (!ids.every(id => typeof id === "number") || page.totalCount > 0 && ids.length === 0) throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${singular} object.`);
  const code = singular.toUpperCase();
  if (page.totalCount === 0) {
    throw new CliError(`${code}_NOT_FOUND`, `No ${singular} has remote ID ${describe(reference)}.`);
  }
  if (page.totalCount > 1) {
    throw new CliError(`${code}_AMBIGUOUS`, `${page.totalCount} ${resource} have remote ID ${describe(reference)}. Use an ID${page.totalCount > ids.length ? `; the first ${ids.length} are listed` : ""}.`, {
      details: { candidates: ids.map(id => ({ id })) },
    });
  }
  const item = await client.get<unknown>(`${resource}/${ids[0]}`);
  if (!validate(item)) throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${singular} object.`);
  return item;
}
