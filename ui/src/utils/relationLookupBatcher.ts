import type { ResolveScrapeRelationsRequest, ResolveScrapeRelationsResult, ScrapeRelationMatch } from "../api/types";
import { getApiErrorCode } from "./requestFailure";

type ResolveRelations = (request: ResolveScrapeRelationsRequest) => Promise<ResolveScrapeRelationsResult>;

interface Waiting {
  request: ResolveScrapeRelationsRequest;
  resolve: (result: ResolveScrapeRelationsResult) => void;
  reject: (error: unknown) => void;
}

// Only exact repeats are dropped: the server normalizes names its own way, so two spellings the browser
// would call one name are both asked.
const uniqueNames = (names: string[]) => [...new Set(names)];

// The server answers each name exactly as it was sent, so a caller is given the matches for its own
// spellings and nothing the server matched for another caller's spelling of the same name.
const ownMatches = (matches: ScrapeRelationMatch[], names: string[]) => {
  const own = new Set(names);
  return matches.filter((match) => own.has(match.input));
};

/**
 * Wraps the library lookup so the rows of a tagger share it: lookups made in the same tick go out as one
 * request, and each caller gets back only the matches for its own names. Requests do not wait for one
 * another, so a slow or hung request holds only the lookups that went out in it.
 */
export function createRelationLookupBatcher(send: ResolveRelations): ResolveRelations {
  let waiting: Waiting[] = [];
  let scheduled = false;

  const run = async (batch: Waiting[]) => {
    try {
      const result = await send({
        tags: uniqueNames(batch.flatMap(({ request }) => request.tags)),
        performers: uniqueNames(batch.flatMap(({ request }) => request.performers)),
        studios: uniqueNames(batch.flatMap(({ request }) => request.studios ?? [])),
      });
      for (const { request, resolve } of batch)
        resolve({
          tags: ownMatches(result.tags, request.tags),
          performers: ownMatches(result.performers, request.performers),
          studios: result.studios && ownMatches(result.studios, request.studios ?? []),
        });
    } catch (error) {
      if (batch.length === 1 || getApiErrorCode(error) !== "ENTITY_NAME_CONFLICT") {
        for (const { reject } of batch) reject(error);
        return;
      }
      // Two legacy performers or studios sharing a name fail any request that names them, so each caller asks
      // again on its own and only the one with that name fails, as when every row asked for itself.
      await Promise.all(
        batch.map(async ({ request, resolve, reject }) => {
          try {
            resolve(await send(request));
          } catch (callerError) {
            reject(callerError);
          }
        }),
      );
    }
  };

  const flush = () => {
    scheduled = false;
    const batch = waiting;
    waiting = [];
    if (batch.length > 0) void run(batch);
  };

  return (request) =>
    new Promise((resolve, reject) => {
      waiting.push({ request, resolve, reject });
      if (!scheduled) {
        scheduled = true;
        setTimeout(flush, 0);
      }
    });
}
