// Auth token storage (localStorage-backed) with a tiny pub/sub for React.
// Tokens are also exposed via getters used by the API client.

import type { AuthUserKind, UserUiPreferences } from "../api/types";

const ACCESS_KEY = "cove_access_token";
const REFRESH_KEY = "cove_refresh_token";
const USER_KEY = "cove_user";
const SHARE_TOKEN_KEY = "cove_share_token";
const SHARE_PASSWORD_KEY = "cove_share_password";

export interface AuthUser {
  id: string;
  username: string;
  kind?: AuthUserKind;
  isSystem?: boolean;
  hasPassword?: boolean;
  permissions: string[];
  readGrantedEntityKinds?: string[];
  uiPreferences?: UserUiPreferences | null;
}

const READ_PERMISSION_GRANTS: Record<string, string> = {
  "videos.read": "video",
  "audios.read": "audio",
  "texts.read": "text",
  "images.read": "image",
  "faces.read": "face",
  "performers.read": "performer",
  "galleries.read": "gallery",
  "studios.read": "studio",
  "tags.read": "tag",
  "groups.read": "group",
  "segments.read": "segment",
};

type Listener = () => void;
const listeners = new Set<Listener>();

function emit() {
  for (const l of listeners) l();
}

export const authStore = {
  getAccessToken(): string | null {
    try {
      return localStorage.getItem(ACCESS_KEY);
    } catch {
      return null;
    }
  },
  getRefreshToken(): string | null {
    try {
      return localStorage.getItem(REFRESH_KEY);
    } catch {
      return null;
    }
  },
  getUser(): AuthUser | null {
    try {
      const raw = localStorage.getItem(USER_KEY);
      return raw ? (JSON.parse(raw) as AuthUser) : null;
    } catch {
      return null;
    }
  },
  getShareToken(): string | null {
    try {
      return sessionStorage.getItem(SHARE_TOKEN_KEY);
    } catch {
      return null;
    }
  },
  getSharePassword(): string | null {
    try {
      return sessionStorage.getItem(SHARE_PASSWORD_KEY);
    } catch {
      return null;
    }
  },
  setTokens(access: string | null, refresh: string | null) {
    try {
      if (access) localStorage.setItem(ACCESS_KEY, access);
      else localStorage.removeItem(ACCESS_KEY);
      if (refresh) localStorage.setItem(REFRESH_KEY, refresh);
      else localStorage.removeItem(REFRESH_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  setUser(user: AuthUser | null) {
    try {
      if (user) localStorage.setItem(USER_KEY, JSON.stringify(user));
      else localStorage.removeItem(USER_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  setShareToken(token: string | null) {
    try {
      if (token) sessionStorage.setItem(SHARE_TOKEN_KEY, token);
      else sessionStorage.removeItem(SHARE_TOKEN_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  setSharePassword(password: string | null) {
    try {
      if (password) sessionStorage.setItem(SHARE_PASSWORD_KEY, password);
      else sessionStorage.removeItem(SHARE_PASSWORD_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  clearShareCredentials() {
    try {
      sessionStorage.removeItem(SHARE_TOKEN_KEY);
      sessionStorage.removeItem(SHARE_PASSWORD_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  clear() {
    try {
      localStorage.removeItem(ACCESS_KEY);
      localStorage.removeItem(REFRESH_KEY);
      localStorage.removeItem(USER_KEY);
      sessionStorage.removeItem(SHARE_TOKEN_KEY);
      sessionStorage.removeItem(SHARE_PASSWORD_KEY);
    } catch {
      /* ignore */
    }
    emit();
  },
  subscribe(fn: Listener): () => void {
    listeners.add(fn);
    return () => {
      listeners.delete(fn);
    };
  },
};

// Wildcard-aware permission check matching the server's CovePrincipal.Has().
export function hasPermission(
  perms: string[] | undefined | null,
  key: string,
  readGrantedEntityKinds?: string[] | null,
): boolean {
  if (!perms || perms.length === 0) return false;
  if (perms.includes("*")) return true;
  if (perms.includes(key)) return true;
  const dot = key.indexOf(".");
  const grantedEntityKind = READ_PERMISSION_GRANTS[key];
  if (grantedEntityKind && (readGrantedEntityKinds ?? []).includes(grantedEntityKind)) return true;
  if (dot < 0) return false;
  const resource = key.slice(0, dot);
  const verb = key.slice(dot + 1);
  if (perms.includes(`${resource}.*`)) return true;
  if (perms.includes(`*.${verb}`)) return true;
  return false;
}
