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
