import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "../auth/AuthContext";
import { authStore } from "../auth/authStore";

const mocks = vi.hoisted(() => ({
  me: vi.fn(),
  serverAwareFetch: vi.fn(),
}));

vi.mock("../api/client", () => ({
  auth: { me: mocks.me },
  nextAccessTokenRefreshAt: () => null,
  refreshAccessTokenIfDue: async () => {},
}));

vi.mock("../state/serverAvailability", () => ({
  serverAwareFetch: mocks.serverAwareFetch,
}));

function LoginProbe() {
  const { login, user } = useAuth();
  const [result, setResult] = useState("idle");

  return (
    <div>
      <button
        type="button"
        onClick={() => {
          void login("existing-user", "secret").then((value) => setResult(value.ok ? "ok" : (value.error ?? "failed")));
        }}
      >
        Sign in
      </button>
      <div data-testid="result">{result}</div>
      <div data-testid="username">{user?.username ?? "anonymous"}</div>
      <div data-testid="permissions">{user?.permissions.join(",") ?? ""}</div>
    </div>
  );
}

const meResponse = {
  user: { id: "17", username: "existing-user", kind: "user" as const, isSystem: false, hasPassword: true },
  permissions: ["videos.read", "dashboards.read"],
  readGrantedEntityKinds: ["video"],
};

function loginResponse(extra: Record<string, unknown>) {
  return new Response(
    JSON.stringify({ token: "access-token", refreshToken: "refresh-token", username: "existing-user", ...extra }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

async function signIn() {
  render(
    <AuthProvider authEnabled>
      <LoginProbe />
    </AuthProvider>,
  );
  // The mount-time probe finds no session.
  await waitFor(() => expect(mocks.me).toHaveBeenCalledTimes(1));
  fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
  await waitFor(() => expect(screen.getByTestId("result")).toHaveTextContent("ok"));
}

describe("AuthProvider password login", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authStore.clear();
    mocks.me.mockRejectedValue(new Error("not authenticated"));
  });

  it("signs in with the user the login response carries, without asking /me again", async () => {
    mocks.serverAwareFetch.mockResolvedValue(loginResponse({ me: meResponse }));

    await signIn();

    expect(screen.getByTestId("username")).toHaveTextContent("existing-user");
    expect(screen.getByTestId("permissions")).toHaveTextContent("videos.read,dashboards.read");
    expect(authStore.getUser()).toMatchObject({ id: "17", readGrantedEntityKinds: ["video"], hasPassword: true });
    expect(authStore.getAccessToken()).toBe("access-token");
    expect(mocks.me).toHaveBeenCalledTimes(1);
  });

  it("asks /me when the login response leaves the user out", async () => {
    mocks.me.mockRejectedValueOnce(new Error("not authenticated")).mockResolvedValueOnce(meResponse);
    mocks.serverAwareFetch.mockResolvedValue(loginResponse({ me: null }));

    await signIn();

    expect(screen.getByTestId("username")).toHaveTextContent("existing-user");
    expect(mocks.me).toHaveBeenCalledTimes(2);
  });
});
