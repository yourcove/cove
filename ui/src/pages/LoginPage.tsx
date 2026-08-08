import { useEffect, useRef, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { auth } from "../api/client";
import type { ExternalLoginMethodRow } from "../api/client";
import { useAuth } from "../auth/AuthContext";

const unsafeLocalUrlCharacters = /[\\\u0000-\u001f\u007f]/;

function getSafePostLoginRedirect(): string | null {
  const redirect = new URLSearchParams(window.location.search).get("redirect");
  if (!redirect || !redirect.startsWith("/") || redirect.startsWith("//") || unsafeLocalUrlCharacters.test(redirect)) {
    return null;
  }

  try {
    const url = new URL(redirect, window.location.origin);
    if (url.origin !== window.location.origin || url.pathname === "/login") return null;
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return null;
  }
}

function buildExternalStartUrl(method: ExternalLoginMethodRow): string | null {
  const raw = method.startUrl;
  if (!raw.startsWith("/") || raw.startsWith("//") || unsafeLocalUrlCharacters.test(raw)) {
    return null;
  }

  try {
    const url = new URL(raw, window.location.origin);
    if (url.origin !== window.location.origin) return null;
    const redirect = getSafePostLoginRedirect();
    if (redirect) url.searchParams.set("returnUrl", redirect);
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return null;
  }
}

export function LoginPage() {
  const { login, externalLoginRedeem } = useAuth();
  const { data: bootstrapStatus } = useQuery({ queryKey: ["auth", "bootstrap-status"], queryFn: auth.bootstrapStatus });
  const { data: externalProviders = [] } = useQuery({
    queryKey: ["auth", "external-providers"],
    queryFn: auth.externalProviders,
    retry: false,
    staleTime: 30_000,
  });
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [externalSubmitting, setExternalSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const externalResultHandled = useRef(false);

  useEffect(() => {
    if (externalResultHandled.current) return;

    const url = new URL(window.location.href);
    const fragment = new URLSearchParams(url.hash.startsWith("#") ? url.hash.slice(1) : url.hash);
    const queryMarkers = url.searchParams.has("external_login_code")
      || url.searchParams.has("external_login_error");
    const codeValues = fragment.getAll("external_login_code");
    const errorValues = fragment.getAll("external_login_error");
    const hasCode = codeValues.length > 0;
    const code = codeValues.length === 1 ? codeValues[0] : null;
    const hasProviderError = errorValues.length > 0;
    if (!queryMarkers && !hasCode && !hasProviderError) return;

    externalResultHandled.current = true;
    url.searchParams.delete("external_login_code");
    url.searchParams.delete("external_login_error");
    fragment.delete("external_login_code");
    fragment.delete("external_login_error");
    const remainingFragment = fragment.toString();
    url.hash = remainingFragment ? `#${remainingFragment}` : "";
    window.history.replaceState(
      window.history.state,
      "",
      `${url.pathname}${url.search}${url.hash}`,
    );

    if (queryMarkers || (hasCode && hasProviderError) || errorValues.length > 1) {
      setError("External sign-in expired or was already used.");
      return;
    }

    if (hasProviderError) {
      setError(errorValues[0] === "unlinked"
        ? "This external identity is not linked. Sign in locally, then link it from Account settings."
        : "External sign-in failed. Please try again.");
      return;
    }

    if (!code) {
      setError("External sign-in expired or was already used.");
      return;
    }

    setExternalSubmitting(true);
    setError(null);
    void externalLoginRedeem(code)
      .then(result => {
        if (!result.ok) {
          setError(result.error ?? "External sign-in could not be completed.");
        }
      })
      .catch(() => setError("External sign-in could not be completed."))
      .finally(() => setExternalSubmitting(false));
  }, [externalLoginRedeem]);

  const externalLoginMethods = externalProviders
    .filter(method => method.showOnLoginPage !== false)
    .map(method => ({ method, href: buildExternalStartUrl(method) }))
    .filter((entry): entry is { method: ExternalLoginMethodRow; href: string } => entry.href !== null);

  const busy = submitting || externalSubmitting;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    const result = await login(username, password);
    setSubmitting(false);
    if (!result.ok) setError(result.error ?? "Login failed.");
  }

  return (
    <div className="min-h-screen bg-background flex items-center justify-center p-4">
      <div className="w-full max-w-sm bg-surface rounded-lg shadow-lg p-6 space-y-4">
        <div className="text-center">
          <h1 className="text-2xl font-semibold text-foreground">Cove</h1>
          <p className="text-sm text-muted-foreground">Sign in to continue</p>
        </div>
        <form onSubmit={onSubmit} className="space-y-3">
          <div className="space-y-1">
            <label htmlFor="login-username" className="text-sm text-muted-foreground">Username</label>
            <input
              id="login-username"
              type="text"
              autoComplete="username"
              autoFocus
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              disabled={busy}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          <div className="space-y-1">
            <label htmlFor="login-password" className="text-sm text-muted-foreground">Password</label>
            <input
              id="login-password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              disabled={busy}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          {error && (
            <div role="alert" className="text-sm text-red-500 bg-red-500/10 rounded px-3 py-2">{error}</div>
          )}
          <button
            type="submit"
            disabled={busy || !username || !password}
            className="w-full rounded bg-accent text-accent-foreground px-3 py-2 font-medium disabled:opacity-50 hover:opacity-90"
          >
            {externalSubmitting ? "Completing sign in..." : submitting ? "Signing in..." : "Sign in"}
          </button>
        </form>
        {externalLoginMethods.length > 0 ? (
          <div className="space-y-3 border-t border-border pt-3">
            <div className="text-center text-xs uppercase tracking-wide text-muted-foreground">Or continue with</div>
            <div className="space-y-2">
              {externalLoginMethods.map(({ method, href }) => (
                <a
                  key={`${method.extensionId}:${method.id}`}
                  href={href}
                  className="block w-full rounded border border-border bg-background px-3 py-2 text-center font-medium text-foreground hover:border-accent hover:text-accent"
                >
                  {method.label}
                </a>
              ))}
            </div>
          </div>
        ) : null}
        <div className="flex flex-col gap-2 border-t border-border pt-3 text-center text-sm">
          {bootstrapStatus?.ownerExists === false ? (
            <a className="text-accent hover:underline" href="/auth/bootstrap">First time here? Owner setup -&gt;</a>
          ) : null}
          <a className="text-muted-foreground hover:text-accent hover:underline" href="/auth/redeem-invite">Have an invite token? -&gt;</a>
        </div>
      </div>
    </div>
  );
}
