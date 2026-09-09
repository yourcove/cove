import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { auth } from "../api/client";
import { authStore } from "../auth/authStore";
import { useAuth } from "../auth/AuthContext";
import { navigateToUrl } from "../router/location";
import { getApiValidationFailureDetail } from "../utils/requestFailure";

type TokenMode = "invite" | "setup";

export function RedeemInvitePage() {
  const { refreshMe } = useAuth();
  const params = useMemo(() => new URLSearchParams(window.location.search), []);
  const initialToken = params.get("token") ?? "";
  const { data: bootstrapStatus } = useQuery({ queryKey: ["auth", "bootstrap-status"], queryFn: auth.bootstrapStatus });
  const [mode, setMode] = useState<TokenMode>(() => (params.get("mode") === "setup" ? "setup" : "invite"));
  const [token, setToken] = useState(initialToken);
  const [username, setUsername] = useState("owner");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const trimmedToken = token.trim();
  const inviteInfoQ = useQuery({
    queryKey: ["auth", "invite-info", trimmedToken],
    queryFn: () => auth.inviteInfo(trimmedToken),
    enabled: mode === "invite" && !!trimmedToken,
    retry: false,
  });
  const inviteInfo = inviteInfoQ.data;
  const inviteUsernameLocked = mode === "invite" && !!inviteInfo?.username && !inviteInfo.usernameRequired;
  const showUsername = mode === "setup" || mode === "invite";

  useEffect(() => {
    if (!initialToken && bootstrapStatus?.hasSetupToken && !bootstrapStatus.ownerExists) {
      setMode("setup");
    }
  }, [bootstrapStatus, initialToken]);

  useEffect(() => {
    if (mode !== "invite") return;
    if (inviteInfo?.username) {
      setUsername(inviteInfo.username);
    } else {
      setUsername("");
    }
  }, [inviteInfo?.username, mode]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    if (password !== confirmPassword) {
      setError("Passwords do not match.");
      return;
    }
    setSubmitting(true);
    try {
      const response =
        mode === "setup"
          ? await auth.redeemSetupToken(token.trim(), password, username.trim() || undefined)
          : await auth.redeemInvite(token.trim(), password, username.trim() || undefined);
      authStore.clearShareCredentials();
      authStore.setTokens(response.token, response.refreshToken);
      await refreshMe();
      navigateToUrl("/", { replace: true });
    } catch (err) {
      setError(getApiValidationFailureDetail(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="min-h-screen bg-background flex items-center justify-center p-4">
      <div className="w-full max-w-sm bg-surface rounded-lg shadow-lg p-6 space-y-4">
        <div className="text-center">
          <h1 className="text-2xl font-semibold text-foreground">Redeem token</h1>
          <p className="text-sm text-muted-foreground">Set your Cove password</p>
        </div>
        <form onSubmit={onSubmit} className="space-y-3">
          <div className="space-y-1">
            <label htmlFor="redeem-token" className="text-sm text-muted-foreground">
              Token
            </label>
            <input
              id="redeem-token"
              type="text"
              autoFocus={!token}
              value={token}
              onChange={(event) => setToken(event.target.value)}
              required
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          {showUsername ? (
            <div className="space-y-1">
              <label htmlFor="redeem-username" className="text-sm text-muted-foreground">
                Username
              </label>
              <input
                id="redeem-username"
                type="text"
                autoComplete="username"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                required
                disabled={submitting}
                readOnly={inviteUsernameLocked}
                className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
              />
            </div>
          ) : null}
          <div className="space-y-1">
            <label htmlFor="redeem-password" className="text-sm text-muted-foreground">
              New password
            </label>
            <input
              id="redeem-password"
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
            <label htmlFor="redeem-confirm" className="text-sm text-muted-foreground">
              Confirm password
            </label>
            <input
              id="redeem-confirm"
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              required
              disabled={submitting}
              className="w-full rounded border border-border bg-background px-3 py-2 text-foreground focus:outline-none focus:ring-2 focus:ring-accent"
            />
          </div>
          {mode === "invite" && inviteInfoQ.isError ? (
            <div role="alert" className="text-sm text-red-500 bg-red-500/10 rounded px-3 py-2">
              Invite token is invalid or expired.
            </div>
          ) : null}
          {error ? (
            <div role="alert" className="text-sm text-red-500 bg-red-500/10 rounded px-3 py-2">
              {error}
            </div>
          ) : null}
          <button
            type="submit"
            disabled={
              submitting || !token.trim() || !password || !confirmPassword || inviteInfoQ.isFetching || !username.trim()
            }
            className="w-full rounded bg-accent text-accent-foreground px-3 py-2 font-medium disabled:opacity-50 hover:opacity-90"
          >
            {submitting ? "Redeeming..." : "Redeem"}
          </button>
        </form>
      </div>
    </div>
  );
}
