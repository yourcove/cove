/**
 * API client helpers for Cove extensions.
 * Extensions should use these instead of raw fetch() for consistency.
 */

const BASE_URL = "/api";

/** Make a typed JSON request to the Cove API. */
export async function request<T>(
  path: string,
  options: RequestInit = {}
): Promise<T> {
  const url = `${BASE_URL}${path}`;
  const res = await fetch(url, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...options.headers,
    },
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new ApiError(res.status, text || res.statusText, path);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: string,
    public readonly path: string
  ) {
    super(`API ${status} ${path}: ${body}`);
    this.name = "ApiError";
  }
}

/**
 * Extension data store — scoped key-value storage for your extension.
 * Backed by the /api/extensions/{id}/data endpoints.
 */
export function createExtensionStore(extensionId: string) {
  const base = `/extensions/${extensionId}/data`;
  return {
    get: (key: string) => request<{ value: string | null }>(`${base}/${encodeURIComponent(key)}`).then(r => r.value),
    set: (key: string, value: string) => request<void>(base, { method: "POST", body: JSON.stringify({ key, value }) }),
    delete: (key: string) => request<void>(`${base}/${encodeURIComponent(key)}`, { method: "DELETE" }),
    getAll: () => request<Record<string, string>>(`${base}`),
  };
}

/**
 * Run a job defined by your extension.
 */
export function runExtensionJob(
  extensionId: string,
  jobId: string,
  parameters?: Record<string, string>
): Promise<void> {
  return request<void>(`/extensions/${extensionId}/jobs/${jobId}/run`, {
    method: "POST",
    body: parameters ? JSON.stringify(parameters) : undefined,
  });
}
