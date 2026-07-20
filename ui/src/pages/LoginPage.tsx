import { useEffect, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { auth } from "../api/client";
import { useAuth } from "../auth/AuthContext";

export function LoginPage() {
  const { login, ssoRedeem } = useAuth();
  const { data: bootstrapStatus } = useQuery({ queryKey: ["auth", "bootstrap-status"], queryFn: auth.bootstrapStatus });
  const { data: oidcStatus } = useQuery({ queryKey: ["auth", "oidc-status"], queryFn: auth.oidcStatus });
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Complete a native-OIDC login: the callback endpoint redirects here with a
  // one-time sso_code that we exchange for the normal login token pair.
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const ssoCode = params.get("sso_code");
    const ssoError = params.get("sso_error");
    if (ssoError) {
      setError(
        ssoError === "user"
          ? "Signed in at the identity provider, but no matching Cove user exists."
          : "Single sign-on failed. Try again or use a password.");
      window.history.replaceState(null, "", "/login");
      return;
    }
    if (!ssoCode) return;
    setSubmitting(true);
    void ssoRedeem(ssoCode).then((result) => {
      setSubmitting(false);
      window.history.replaceState(null, "", "/login");
      if (!result.ok) setError(result.error ?? "Single sign-on failed.");
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
              disabled={submitting}
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
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          {error && (
            <div role="alert" className="text-sm text-red-500 bg-red-500/10 rounded px-3 py-2">{error}</div>
          )}
          <button
            type="submit"
            disabled={submitting || !username || !password}
            className="w-full rounded bg-accent text-accent-foreground px-3 py-2 font-medium disabled:opacity-50 hover:opacity-90"
          >
            {submitting ? "Signing in..." : "Sign in"}
          </button>
        </form>
        {oidcStatus?.enabled ? (
          <a
            href="/api/auth/oidc/login"
            className="block w-full rounded border border-accent text-accent px-3 py-2 font-medium text-center hover:bg-accent hover:text-accent-foreground"
          >
            {oidcStatus.label}
          </a>
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
