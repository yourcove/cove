/**
 * API client helpers for Cove extensions.
 * Extensions should use these instead of raw fetch() for consistency.
 */
/** Make a typed JSON request to the Cove API. */
export declare function request<T>(path: string, options?: RequestInit): Promise<T>;
export declare class ApiError extends Error {
    readonly status: number;
    readonly body: string;
    readonly path: string;
    constructor(status: number, body: string, path: string);
}
/**
 * Extension data store — scoped key-value storage for your extension.
 * Backed by the /api/extensions/{id}/data endpoints.
 */
export declare function createExtensionStore(extensionId: string): {
    get: (key: string) => Promise<string | null>;
    set: (key: string, value: string) => Promise<void>;
    delete: (key: string) => Promise<void>;
    getAll: () => Promise<Record<string, string>>;
};
/**
 * Run a job defined by your extension.
 */
export declare function runExtensionJob(extensionId: string, jobId: string, parameters?: Record<string, string>): Promise<void>;
//# sourceMappingURL=api.d.ts.map