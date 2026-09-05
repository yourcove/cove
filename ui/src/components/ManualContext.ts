import { useEffect } from "react";

export interface TutorialOpenRequest {
  topicId?: string;
  slideId?: string;
  page?: string;
  context?: string;
  contexts?: string[];
}

type ManualContextValue = string | null | undefined | false;

const activeContextCounts = new Map<string, number>();

export function normalizeManualContext(value: ManualContextValue) {
  const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
  return normalized || undefined;
}

export function uniqueManualContexts(values: Iterable<ManualContextValue>) {
  const unique: string[] = [];
  const seen = new Set<string>();

  for (const value of values) {
    const normalized = normalizeManualContext(value);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    unique.push(normalized);
  }

  return unique;
}

export function registerManualContext(...contexts: ManualContextValue[]) {
  const normalizedContexts = uniqueManualContexts(contexts);
  for (const context of normalizedContexts) {
    activeContextCounts.set(context, (activeContextCounts.get(context) ?? 0) + 1);
  }

  let disposed = false;
  return () => {
    if (disposed) return;
    disposed = true;
    for (const context of normalizedContexts) {
      const count = activeContextCounts.get(context) ?? 0;
      if (count <= 1) activeContextCounts.delete(context);
      else activeContextCounts.set(context, count - 1);
    }
  };
}

export async function withManualContext<T>(contexts: ManualContextValue[], action: () => Promise<T> | T): Promise<T> {
  const unregister = registerManualContext(...contexts);
  try {
    return await action();
  } finally {
    unregister();
  }
}

export function useManualContext(contexts: ManualContextValue | ManualContextValue[], enabled = true) {
  const contextKey = Array.isArray(contexts)
    ? uniqueManualContexts(contexts).join("\u001f")
    : (normalizeManualContext(contexts) ?? "");

  useEffect(() => {
    if (!enabled) return;
    const values = Array.isArray(contexts) ? contexts : [contexts];
    return registerManualContext(...values);
    // contextKey is the normalized dependency; contexts may be an inline array.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, contextKey]);
}

export function getActiveManualContexts() {
  return [...activeContextCounts.keys()].reverse();
}

export function createManualOpenRequest(
  currentPage: string,
  activePage: string,
  pathname = window.location.pathname,
): TutorialOpenRequest {
  return {
    page: activePage,
    contexts: uniqueManualContexts([
      ...getActiveManualContexts(),
      ...createPageManualContexts(currentPage, activePage, pathname),
    ]),
  };
}

export function createPageManualContexts(currentPage: string, activePage: string, pathname: string) {
  const contexts: string[] = [];

  const normalizedPath = normalizePathname(pathname);
  contexts.push(`route:${normalizedPath}`);

  const pathParts = normalizedPath.split("/").filter(Boolean);
  if (pathParts[0] === "settings") {
    const settingsKeyParts = pathParts.slice(1);
    for (let length = settingsKeyParts.length; length >= 1; length--) {
      contexts.push(`settings-tab:${settingsKeyParts.slice(0, length).join("/")}`);
    }

    if (settingsKeyParts.length > 0 && settingsKeyParts[0] !== "extensions") {
      for (let length = settingsKeyParts.length; length >= 1; length--) {
        contexts.push(`settings-tab:extensions/${settingsKeyParts.slice(0, length).join("/")}`);
      }
      contexts.push(`route:/settings/extensions/${settingsKeyParts.join("/")}`);
    }
  }

  contexts.push(`page:${activePage}`);
  if (currentPage !== activePage) {
    contexts.push(`page:${currentPage}`);
  }

  return uniqueManualContexts(contexts);
}

function normalizePathname(pathname: string) {
  let rawPathname = pathname;
  try {
    rawPathname = new URL(pathname, window.location.origin).pathname;
  } catch {
    rawPathname = pathname.split(/[?#]/, 1)[0] || "/";
  }

  const pathParts = rawPathname
    .split("/")
    .filter(Boolean)
    .map((part) => safeDecodePathPart(part).toLowerCase());

  return pathParts.length > 0 ? `/${pathParts.join("/")}` : "/";
}

function safeDecodePathPart(part: string) {
  try {
    return decodeURIComponent(part);
  } catch {
    return part;
  }
}
