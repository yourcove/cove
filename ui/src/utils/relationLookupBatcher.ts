import type { ResolveScrapeRelationsRequest, ResolveScrapeRelationsResult, ScrapeRelationMatch } from "../api/types";
import { relationKey } from "../components/ScrapeRelationChoices";
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

const ownMatches = (matches: ScrapeRelationMatch[], names: string[]) => {
  const keys = new Set(names.map(relationKey));
  return matches.filter((match) => keys.has(relationKey(match.input)));
};

/**
 * Wraps the library lookup so the tagger's rows share it. The server loads every tag and alias for each
 * request whatever its size, so lookups made together go out as one request, and lookups made while one
 * is out wait for it and then go out together; each caller gets back only the matches for its own names.
 */
export function createRelationLookupBatcher(send: ResolveRelations): ResolveRelations {
  let waiting: Waiting[] = [];
  let scheduled = false;
  let sending = false;

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
    if (sending || waiting.length === 0) return;
    const batch = waiting;
    waiting = [];
    sending = true;
    void run(batch).finally(() => {
      sending = false;
      flush();
    });
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
