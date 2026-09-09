import { ConfigStore } from "./config";
import { CliError } from "./errors";
import type { CoveCliConfig, LoginResponse, StoredProfile } from "./types";

interface ClientOptions {
  store: ConfigStore;
  profileName: string;
  profile: StoredProfile;
  transientCredential?: boolean;
  timeoutMs?: number;
  fetch?: typeof globalThis.fetch;
}

function objectValue(value: unknown, key: string): unknown {
  return value && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined;
}

function serverErrorMessage(body: unknown): string | undefined {
  for (const key of ["message", "error", "detail", "title"]) {
    const value = objectValue(body, key);
    if (typeof value === "string" && value.trim()) return value;
  }
  return undefined;
}

export class CoveClient {
  readonly server: string;
  private profile: StoredProfile;
  private readonly store: ConfigStore;
  private readonly profileName: string;
  private readonly transientCredential: boolean;
  private readonly timeoutMs: number;
  private readonly fetchImplementation: typeof globalThis.fetch;

  constructor(options: ClientOptions) {
    this.server = options.profile.server;
    this.profile = structuredClone(options.profile);
    this.store = options.store;
    this.profileName = options.profileName;
    this.transientCredential = options.transientCredential ?? false;
    this.timeoutMs = options.timeoutMs ?? 30_000;
    this.fetchImplementation = options.fetch ?? globalThis.fetch;
  }

  async get<T>(path: string, timeoutMs = this.timeoutMs): Promise<T> {
    return this.request<T>(path, {}, true, timeoutMs);
  }

  async post<T>(path: string, body?: unknown): Promise<T> {
    return this.request<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });
  }

  async put<T>(path: string, body?: unknown): Promise<T> {
    return this.request<T>(path, { method: "PUT", body: body === undefined ? undefined : JSON.stringify(body) });
  }

  async delete<T>(path: string): Promise<T> {
    return this.request<T>(path, { method: "DELETE" });
  }

  async download(path: string, allowRefresh = true): Promise<{ body: ReadableStream<Uint8Array>; contentLength?: number; contentType?: string; disposition?: string }> {
    const requestUrl = this.url(path);
    const headers = new Headers({ Accept: "*/*" });
    const credential = this.profile.credential;
    if (credential?.type === "session") headers.set("Authorization", `Bearer ${credential.accessToken}`);
    if (credential?.type === "apiToken") headers.set("Authorization", `Bearer ${credential.token}`);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    let response: Response;
    try {
      response = await this.fetchImplementation(requestUrl, { headers, signal: controller.signal });
    } catch (error) {
      clearTimeout(timer);
      if (error instanceof Error && error.name === "AbortError") throw new CliError("REQUEST_TIMEOUT", `The download from ${this.server} timed out.`);
      throw new CliError("NETWORK_ERROR", `Could not download from the Cove server at ${this.server}.`, { details: error instanceof Error ? error.message : undefined });
    }
    if (response.status === 401 && allowRefresh && credential?.type === "session" && !this.transientCredential) {
      clearTimeout(timer);
      await this.refresh(credential.accessToken);
      return this.download(path, false);
    }
    if (!response.ok) {
      const text = await response.text();
      clearTimeout(timer);
      let body: unknown;
      try { body = text ? JSON.parse(text) : undefined; } catch { body = undefined; }
      const message = serverErrorMessage(body);
      throw new CliError(`HTTP_${response.status}`, message ?? `Cove returned HTTP ${response.status}.`, { status: response.status, details: body });
    }
    if (!response.body) {
      clearTimeout(timer);
      throw new CliError("INVALID_RESPONSE", "Cove returned a download without a response body.");
    }
    const contentLengthHeader = response.headers.get("content-length");
    const contentLength = contentLengthHeader === null ? undefined : Number(contentLengthHeader);
    clearTimeout(timer);
    return {
      body: response.body,
      ...(contentLength !== undefined && Number.isSafeInteger(contentLength) && contentLength >= 0 ? { contentLength } : {}),
      ...(response.headers.get("content-type") ? { contentType: response.headers.get("content-type")! } : {}),
      ...(response.headers.get("content-disposition") ? { disposition: response.headers.get("content-disposition")! } : {}),
    };
  }

  async request<T>(path: string, init: RequestInit = {}, allowRefresh = true, timeoutMs = this.timeoutMs): Promise<T> {
    const requestUrl = this.url(path);
    const headers = new Headers(init.headers);
    headers.set("Accept", "application/json");
    if (init.body !== undefined) headers.set("Content-Type", "application/json");
    const credential = this.profile.credential;
    if (credential?.type === "session") headers.set("Authorization", `Bearer ${credential.accessToken}`);
    if (credential?.type === "apiToken") headers.set("Authorization", `Bearer ${credential.token}`);

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    let response: Response;
    let text: string;
    try {
      response = await this.fetchImplementation(requestUrl, { ...init, headers, signal: controller.signal });
      text = await response.text();
    } catch (error) {
      if (error instanceof Error && error.name === "AbortError") {
        throw new CliError("REQUEST_TIMEOUT", `The request to ${this.server} timed out.`);
      }
      throw new CliError("NETWORK_ERROR", `Could not connect to the Cove server at ${this.server}.`, { details: error instanceof Error ? error.message : undefined });
    } finally {
      clearTimeout(timer);
    }

    if (response.status === 401 && allowRefresh && credential?.type === "session" && !this.transientCredential) {
      await this.refresh(credential.accessToken, timeoutMs);
      return this.request<T>(path, init, false, timeoutMs);
    }

    let body: unknown;
    try {
      body = text ? JSON.parse(text) : undefined;
    } catch {
      throw new CliError("INVALID_RESPONSE", `Cove returned a non-JSON response (${response.status}).`, { status: response.status });
    }
    if (!response.ok) {
      const code = objectValue(body, "code");
      const message = serverErrorMessage(body);
      throw new CliError(
        typeof code === "string" ? code : `HTTP_${response.status}`,
        message ?? `Cove returned HTTP ${response.status}.`,
        { status: response.status, details: body },
      );
    }
    return body as T;
  }

  private url(path: string): string {
    const base = new URL(`${this.server}/api/`);
    const target = new URL(path.replace(/^\/+/, "").replace(/^api\//, ""), base);
    if (target.origin !== base.origin || !target.pathname.startsWith(base.pathname)) {
      throw new CliError("INVALID_ARGUMENT", "API paths must stay beneath the server's /api endpoint.");
    }
    return target.toString();
  }

  private async refresh(rejectedAccessToken: string, timeoutMs = this.timeoutMs): Promise<void> {
    await this.store.withLock(async () => {
      const config = await this.store.load();
      const stored = config.profiles[this.profileName];
      if (!stored || stored.server !== this.server || stored.credential?.type !== "session") {
        throw new CliError("AUTH_REQUIRED", "The stored Cove session is no longer available. Log in again.", { status: 401 });
      }
      if (stored.credential.accessToken !== rejectedAccessToken) {
        this.profile = structuredClone(stored);
        return;
      }

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      let response: Response;
      let body: Partial<LoginResponse> & { code?: string; message?: string } | undefined;
      try {
        response = await this.fetchImplementation(`${this.server}/api/auth/refresh`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Accept: "application/json" },
          body: JSON.stringify({ refreshToken: stored.credential.refreshToken }),
          signal: controller.signal,
        });
        body = await response.json().catch(() => undefined) as typeof body;
      } catch (error) {
        if (error instanceof Error && error.name === "AbortError") {
          throw new CliError("REQUEST_TIMEOUT", `The token refresh request to ${this.server} timed out.`);
        }
        throw new CliError("NETWORK_ERROR", `Could not refresh the Cove session at ${this.server}.`, { details: error instanceof Error ? error.message : undefined });
      } finally {
        clearTimeout(timer);
      }
      if (!response.ok) {
        if (response.status === 409 && body?.code === "REFRESH_TOKEN_ROTATED") {
          const latest = await this.store.load();
          const latestProfile = latest.profiles[this.profileName];
          if (latestProfile?.credential?.type === "session" && latestProfile.credential.accessToken !== rejectedAccessToken) {
            this.profile = structuredClone(latestProfile);
            return;
          }
        }
        throw new CliError(body?.code ?? "AUTH_REQUIRED", body?.message ?? "The Cove session could not be refreshed. Log in again.", { status: response.status });
      }
      if (!body?.token || !body.refreshToken) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid token refresh response.");
      stored.credential = {
        type: "session",
        accessToken: body.token,
        refreshToken: body.refreshToken,
        accessExpires: body.accessExpires,
        refreshExpires: body.refreshExpires,
      };
      await this.store.save(config as CoveCliConfig);
      this.profile = structuredClone(stored);
    });
  }
}
