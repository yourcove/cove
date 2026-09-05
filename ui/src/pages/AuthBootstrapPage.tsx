import { useState, type FormEvent } from "react";
import { auth } from "../api/client";
import { authStore } from "../auth/authStore";
import { useAuth } from "../auth/AuthContext";
import { navigateToUrl } from "../router/location";

export function AuthBootstrapPage() {
  const { refreshMe } = useAuth();
  const [username, setUsername] = useState("owner");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    if (password !== confirmPassword) {
      setError("Passwords do not match.");
      return;
    }

    setSubmitting(true);
    try {
      const response = await auth.bootstrapOwner(username.trim(), password);
      authStore.clearShareCredentials();
      authStore.setTokens(response.token, response.refreshToken);
      await refreshMe();
      navigateToUrl("/", { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Owner setup failed.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="min-h-screen bg-background flex items-center justify-center p-4">
      <div className="w-full max-w-sm bg-surface rounded-lg shadow-lg p-6 space-y-4">
        <div className="text-center">
          <h1 className="text-2xl font-semibold text-foreground">Owner setup</h1>
          <p className="text-sm text-muted-foreground">Create the first Cove account</p>
        </div>
        <form onSubmit={onSubmit} className="space-y-3">
          <div className="space-y-1">
            <label htmlFor="bootstrap-username" className="text-sm text-muted-foreground">
              Username
            </label>
            <input
              id="bootstrap-username"
              type="text"
              autoComplete="username"
              autoFocus
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              required
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          <div className="space-y-1">
            <label htmlFor="bootstrap-password" className="text-sm text-muted-foreground">
              Password
            </label>
            <input
              id="bootstrap-password"
              type="password"
              autoComplete="new-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          <div className="space-y-1">
            <label htmlFor="bootstrap-confirm" className="text-sm text-muted-foreground">
              Confirm password
            </label>
            <input
              id="bootstrap-confirm"
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              required
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          {error ? (
            <div role="alert" className="text-sm text-red-500 bg-red-500/10 rounded px-3 py-2">
              {error}
            </div>
          ) : null}
          <button
            type="submit"
            disabled={submitting || !username.trim() || !password || !confirmPassword}
            className="w-full rounded bg-accent text-accent-foreground px-3 py-2 font-medium disabled:opacity-50 hover:opacity-90"
          >
            {submitting ? "Creating..." : "Create owner"}
          </button>
        </form>
      </div>
    </div>
  );
}
