import { authedFetch } from "../api/client";

export interface ExtensionFetchOptions extends RequestInit {
  /** Optional timeout in milliseconds. Extension requests do not time out by default. */
  timeoutMs?: number | null;
}

function normalizeSameOriginApiUrl(input: string): string {
  let url: URL;
  try {
    url = new URL(input, window.location.origin);
  } catch {
    throw new TypeError("extensionFetch requires a valid same-origin Cove API URL.");
  }

  const isApiPath = url.pathname === "/api" || url.pathname.startsWith("/api/");
  if (url.origin !== window.location.origin || !isApiPath) {
    throw new TypeError("extensionFetch requires a same-origin Cove API URL.");
  }

  return url.href;
}

/** Authenticated fetch for extension-owned, same-origin Cove API endpoints. */
export async function extensionFetch(input: string, init: ExtensionFetchOptions = {}): Promise<Response> {
  const url = normalizeSameOriginApiUrl(input);
  const { timeoutMs = null, ...requestInit } = init;
  return authedFetch(url, { ...requestInit, redirect: "error", timeoutMs });
}
