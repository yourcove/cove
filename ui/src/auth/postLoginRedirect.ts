// Where to go after sign-in, from the login page's `?redirect=` parameter.

// Pages the API serves itself rather than the app (Swagger UI and the OpenAPI document). They need a full
// page load; an in-app navigation would render the app's own not-found page at that path.
const SERVER_PAGE_PREFIXES = ["/swagger", "/openapi"];

// A server page that keeps sending the browser back to sign-in (for example because the browser refuses
// Cove's access cookie) would otherwise loop forever. One full-page redirect per target per this window.
const SERVER_REDIRECT_KEY = "cove_server_page_redirect";
const SERVER_REDIRECT_WINDOW_MS = 15_000;

// Backslashes and control characters can change how a browser parses the URL; the login page rejects them too.
// oxlint-disable-next-line no-control-regex -- matching control characters is the purpose of this pattern
const UNSAFE_LOCAL_URL_CHARACTERS = /[\\\u0000-\u001f\u007f]/;

export function getPostLoginRedirectUrl(search: string, origin: string): string {
  const redirect = new URLSearchParams(search).get("redirect");
  if (
    !redirect ||
    !redirect.startsWith("/") ||
    redirect.startsWith("//") ||
    UNSAFE_LOCAL_URL_CHARACTERS.test(redirect)
  ) {
    return "/";
  }

  try {
    const url = new URL(redirect, origin);
    if (url.origin !== origin || url.pathname === "/login") {
      return "/";
    }

    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return "/";
  }
}

export function isServerPage(path: string): boolean {
  const pathname = path.split(/[?#]/, 1)[0].toLowerCase();
  return SERVER_PAGE_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));
}

/**
 * Whether to leave the app for this server page now. False when the same page was already attempted
 * moments ago, so a page that keeps returning to sign-in cannot loop.
 */
export function claimServerPageRedirect(path: string, now: number, storage: Storage | undefined): boolean {
  try {
    const previous = storage?.getItem(SERVER_REDIRECT_KEY);
    if (previous) {
      const { target, at } = JSON.parse(previous) as { target?: unknown; at?: unknown };
      if (target === path && typeof at === "number" && now - at >= 0 && now - at < SERVER_REDIRECT_WINDOW_MS) {
        return false;
      }
    }
    storage?.setItem(SERVER_REDIRECT_KEY, JSON.stringify({ target: path, at: now }));
  } catch {
    // Storage can be unavailable (private mode, blocked site data); redirect without the loop guard.
  }
  return true;
}
