export function normalizeQueryError(error: unknown): Error | null {
  if (error instanceof Error) return error;
  if (error == null) return null;
  return new Error(String(error));
}

export function getLoadError<TData>(data: TData | undefined, error: unknown): Error | null {
  return data === undefined ? normalizeQueryError(error) : null;
}

export function isApiNotFoundError(error: unknown): boolean {
  const normalized = normalizeQueryError(error);
  return normalized ? /^API Error 404\b/i.test(normalized.message) : false;
}

export type QueryLoadState<TData> =
  | { status: "pending" }
  | { status: "error"; error: Error; retry?: () => void }
  | { status: "empty"; data: TData }
  | { status: "success"; data: TData };

interface ResolveQueryLoadStateOptions<TData> {
  data: TData | undefined;
  isPending: boolean;
  error: unknown;
  isEmpty: (data: TData) => boolean;
  retry?: () => void;
}

export function resolveQueryLoadState<TData>({
  data,
  isPending,
  error,
  isEmpty,
  retry,
}: ResolveQueryLoadStateOptions<TData>): QueryLoadState<TData> {
  if (data === undefined) {
    if (isPending) return { status: "pending" };
    const normalizedError = normalizeQueryError(error);
    if (normalizedError) return { status: "error", error: normalizedError, retry };
    return { status: "pending" };
  }

  return isEmpty(data) ? { status: "empty", data } : { status: "success", data };
}

/** What a type-to-search result list should show for its current query. */
export type SearchResultState = "error" | "searching" | "empty" | "results";

/**
 * Picks the state of a type-to-search list whose query keeps the previous results while the next search loads
 * (`placeholderData: keepPreviousData`). A failed search is reported as a failure, never as "no matches":
 * nothing about the library was learned, and acting on "no matches" (creating what seems to be missing) could
 * duplicate something that exists. Nothing is reported as "no matches" until the current text has an answer:
 * while it has none and there is nothing to keep on screen, the list says it is searching, also when the fetch
 * is paused offline. With results kept from an earlier search, those stay on screen (the caller disables
 * them); that includes a retry after a failure, which TanStack starts afresh from the last successful answer.
 * A background refresh of a settled empty answer keeps saying "no matches".
 *
 * A query that is disabled has no answer either and reads as "searching"; callers show their own hint for text
 * too short to search before asking.
 */
export function getSearchResultState(
  query: { isError: boolean; isFetching: boolean; isPending: boolean; isPlaceholderData: boolean },
  resultCount: number,
): SearchResultState {
  if (query.isError && !query.isFetching) return "error";
  if (resultCount === 0 && (query.isPending || query.isPlaceholderData || query.isError)) return "searching";
  return resultCount === 0 ? "empty" : "results";
}
