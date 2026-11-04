import revisions from "./mediaCacheRevisions.json";

// Cache revisions of media route families, sent as the `r` query parameter. Bump a family in the JSON
// file when browsers must stop reusing what they cached from it: versioned media is cached as immutable,
// so a new URL is the only way to make them refetch. The server builds some of these URLs too, from
// MediaCacheRevisions in src/Cove.Api/Http/MediaCacheRevisions.cs; a backend test fails if the two differ.
// That file also records why each family was last bumped. Revisions only ever go up: an earlier value,
// 0 included, names URLs browsers may still have cached.
export type MediaCacheFamily = keyof typeof revisions;

/** Query parameters that give a family's URLs its current cache key. A revision of 0 adds nothing. */
export function mediaCacheRevision(family: MediaCacheFamily): { r?: number } {
  const revision = revisions[family];
  return revision > 0 ? { r: revision } : {};
}
