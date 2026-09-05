import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { authStore, hasPermission as hasPermImpl } from "./authStore";
import type { AuthUser } from "./authStore";
import { auth } from "../api/client";
import type { MeResponse } from "../api/types";
import { serverAwareFetch } from "../state/serverAvailability";

interface AuthState {
  user: AuthUser | null;
  permissions: string[];
  loading: boolean;
  authEnabled: boolean;
  /** Whether the current user is authenticated AND has loaded from /me. */
  ready: boolean;
}

interface AuthContextValue extends AuthState {
  login(username: string, password: string): Promise<{ ok: boolean; error?: string }>;
  externalLoginRedeem(code: string): Promise<{ ok: boolean; error?: string }>;
  logout(): Promise<void>;
  hasPermission(key: string): boolean;
  refreshMe(): Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

interface LoginResponse {
  token: string;
  refreshToken: string;
  user?: { id: number | string; username: string };
  username?: string;
}

async function fetchMe(): Promise<MeResponse | null> {
  try {
    return await auth.me();
  } catch {
    return null;
  }
}

function captureShareCredentialsFromUrl(): boolean {
  if (typeof window === "undefined") {
    return false;
  }

  const url = new URL(window.location.href);
  const shareToken = url.searchParams.get("share_token");
  const sharePassword = url.searchParams.get("share_password");
  if (!shareToken && !sharePassword) {
    return false;
  }

  if (shareToken) {
    authStore.setShareToken(shareToken);
  }
  if (sharePassword) {
    authStore.setSharePassword(sharePassword);
  }

  url.searchParams.delete("share_token");
  url.searchParams.delete("share_password");
  window.history.replaceState(null, "", `${url.pathname}${url.search}${url.hash}`);
  return true;
}

export function AuthProvider({ children, authEnabled }: { children: ReactNode; authEnabled: boolean }) {
  const [user, setUser] = useState<AuthUser | null>(() => authStore.getUser());
  const [loading, setLoading] = useState(true);
  const effectivePermissions = authEnabled ? (user?.permissions ?? []) : ["*"];
  const effectiveReadGrantedKinds = authEnabled ? (user?.readGrantedEntityKinds ?? []) : [];

  const refreshMe = useCallback(async () => {
    let me = await fetchMe();
    if (!me && authStore.getShareToken()) {
      if (!authStore.getSharePassword()) {
        const password = window.prompt("Enter the password for this share link.");
        if (password != null) {
          authStore.setSharePassword(password);
          me = await fetchMe();
        }
      }

      if (!me) {
        authStore.clearShareCredentials();
        if (authStore.getAccessToken()) {
          me = await fetchMe();
        }
      }
    }

    if (me) {
      const u: AuthUser = {
        id: String(me.user.id),
        username: me.user.username,
        kind: me.user.kind,
        isSystem: me.user.isSystem,
        hasPassword: me.user.hasPassword,
        permissions: me.permissions,
        readGrantedEntityKinds: me.readGrantedEntityKinds ?? [],
        uiPreferences: me.user.uiPreferences ?? null,
      };
      authStore.setUser(u);
      setUser(u);
    } else {
      if (authStore.getAccessToken()) {
        authStore.clear();
      } else {
        authStore.clearShareCredentials();
        authStore.setUser(null);
      }
      setUser(null);
    }
  }, []);

  // Probe /me once on every mount. Besides validating stored Cove/share credentials, this lets
  // request-level authentication extensions (for example, a trusted reverse-proxy assertion)
  // establish an ambient principal without first manufacturing a browser token.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (!authEnabled) {
        await refreshMe();
        if (!cancelled) setLoading(false);
        return;
      }
      captureShareCredentialsFromUrl();
      await refreshMe();
      if (!cancelled) setLoading(false);
    })();
    const unsub = authStore.subscribe(() => {
      setUser(authStore.getUser());
    });
    return () => {
      cancelled = true;
      unsub();
    };
  }, [authEnabled, refreshMe]);

  // Listen for global "auth required" events (from authedFetch on hard 401)
  useEffect(() => {
    const handler = () => {
      authStore.clear();
      setUser(null);
    };
    window.addEventListener("cove-auth-required", handler);
    return () => window.removeEventListener("cove-auth-required", handler);
  }, []);

  const login = useCallback(
    async (username: string, password: string) => {
      const res = await serverAwareFetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        let message = "Invalid credentials.";
        try {
          const body = (await res.json()) as { message?: string };
          if (body?.message) message = body.message;
        } catch {
          /* ignore */
        }
        return { ok: false, error: message };
      }
      const body = (await res.json()) as LoginResponse;
      authStore.clearShareCredentials();
      authStore.setTokens(body.token, body.refreshToken);
      await refreshMe();
      return { ok: true };
    },
    [refreshMe],
  );

  const externalLoginRedeem = useCallback(
    async (code: string) => {
      let res: Response;
      try {
        res = await serverAwareFetch("/api/auth/external/redeem", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ code }),
        });
      } catch {
        return { ok: false, error: "External sign-in could not be completed." };
      }

      if (!res.ok) {
        return {
          ok: false,
          error:
            res.status === 401
              ? "External sign-in expired or was already used."
              : "External sign-in could not be completed.",
        };
      }

      let body: LoginResponse;
      try {
        body = (await res.json()) as LoginResponse;
      } catch {
        return { ok: false, error: "External sign-in could not be completed." };
      }

      if (!body.token || !body.refreshToken) {
        return { ok: false, error: "External sign-in could not be completed." };
      }

      authStore.clearShareCredentials();
      authStore.setTokens(body.token, body.refreshToken);
      await refreshMe();
      if (!authStore.getUser()) {
        authStore.clear();
        return { ok: false, error: "External sign-in could not be completed." };
      }

      return { ok: true };
    },
    [refreshMe],
  );

  const logout = useCallback(async () => {
    const refresh = authStore.getRefreshToken();
    try {
      await serverAwareFetch("/api/auth/logout", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: refresh ?? "" }),
      });
    } catch {
      /* ignore */
    }
    authStore.clear();
    setUser(null);
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      permissions: effectivePermissions,
      loading,
      authEnabled,
      ready: !authEnabled || !!user,
      login,
      externalLoginRedeem,
      logout,
      hasPermission: (k: string) => hasPermImpl(effectivePermissions, k, effectiveReadGrantedKinds),
      refreshMe,
    }),
    [
      user,
      effectivePermissions,
      effectiveReadGrantedKinds,
      loading,
      authEnabled,
      login,
      externalLoginRedeem,
      logout,
      refreshMe,
    ],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}

/** Conditionally renders children only when the current user has the given permission. */
export function RequirePermission({
  perm,
  fallback = null,
  children,
}: {
  perm: string;
  fallback?: ReactNode;
  children: ReactNode;
}) {
  const { hasPermission } = useAuth();
  return hasPermission(perm) ? <>{children}</> : <>{fallback}</>;
}
