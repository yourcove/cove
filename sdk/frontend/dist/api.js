/**
 * API client helpers for Cove extensions.
 * Extensions should use these instead of raw fetch() for consistency.
 */
const BASE_URL = "/api";
/** Make a typed JSON request to the Cove API. */
export async function request(path, options = {}) {
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
    if (res.status === 204)
        return undefined;
    return res.json();
}
export class ApiError extends Error {
    status;
    body;
    path;
    constructor(status, body, path) {
        super(`API ${status} ${path}: ${body}`);
        this.status = status;
        this.body = body;
        this.path = path;
        this.name = "ApiError";
    }
}
/**
 * Extension data store — scoped key-value storage for your extension.
 * Backed by the /api/extensions/{id}/data endpoints.
 */
export function createExtensionStore(extensionId) {
    const base = `/extensions/${extensionId}/data`;
    return {
        get: (key) => request(`${base}/${encodeURIComponent(key)}`).then(r => r.value),
        set: (key, value) => request(base, { method: "POST", body: JSON.stringify({ key, value }) }),
        delete: (key) => request(`${base}/${encodeURIComponent(key)}`, { method: "DELETE" }),
        getAll: () => request(`${base}`),
    };
}
/**
 * Run a job defined by your extension.
 */
export function runExtensionJob(extensionId, jobId, parameters) {
    return request(`/extensions/${extensionId}/jobs/${jobId}/run`, {
        method: "POST",
        body: parameters ? JSON.stringify(parameters) : undefined,
    });
}
