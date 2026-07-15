export function normalizeQueryError(error: unknown): Error | null {
  if (error instanceof Error) return error;
  if (error == null) return null;
  return new Error(String(error));
}

export function getLoadError<TData>(data: TData | undefined, error: unknown): Error | null {
  return data === undefined ? normalizeQueryError(error) : null;
}
