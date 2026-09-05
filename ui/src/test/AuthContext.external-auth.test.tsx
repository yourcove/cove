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
}));

vi.mock("../state/serverAvailability", () => ({
  serverAwareFetch: mocks.serverAwareFetch,
}));

function ExternalLoginProbe() {
  const { externalLoginRedeem, user } = useAuth();
  const [result, setResult] = useState<string>("idle");

  return (
    <div>
      <button
        type="button"
        onClick={() => {
          void externalLoginRedeem("browser-bound-code").then((value) => {
            setResult(value.ok ? "ok" : (value.error ?? "failed"));
          });
        }}
      >
        Redeem
      </button>
      <div data-testid="result">{result}</div>
      <div data-testid="username">{user?.username ?? "anonymous"}</div>
    </div>
  );
}

const meResponse = {
  user: { id: "17", username: "existing-user", kind: "user" as const },
  permissions: ["videos.read"],
  readGrantedEntityKinds: [],
};

describe("AuthProvider external authentication", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authStore.clear();
    mocks.me.mockRejectedValue(new Error("not authenticated"));
  });

  it("loads an ambient request-level principal without client-side tokens", async () => {
    mocks.me.mockResolvedValue(meResponse);

    render(
      <AuthProvider authEnabled>
        <ExternalLoginProbe />
      </AuthProvider>,
    );

    await waitFor(() => expect(screen.getByTestId("username")).toHaveTextContent("existing-user"));
    expect(mocks.me).toHaveBeenCalledTimes(1);
    expect(authStore.getAccessToken()).toBeNull();
    expect(authStore.getRefreshToken()).toBeNull();
    expect(mocks.serverAwareFetch).not.toHaveBeenCalled();
  });

  it("stores standard Cove tokens and refreshes the authenticated user", async () => {
    mocks.me.mockRejectedValueOnce(new Error("not authenticated")).mockResolvedValueOnce(meResponse);
    mocks.serverAwareFetch.mockResolvedValue(
      new Response(
        JSON.stringify({
          token: "access-token",
          refreshToken: "refresh-token",
          username: "existing-user",
        }),
        {
          status: 200,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );

    render(
      <AuthProvider authEnabled>
        <ExternalLoginProbe />
      </AuthProvider>,
    );

    await waitFor(() => expect(mocks.me).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole("button", { name: "Redeem" }));

    await waitFor(() => expect(screen.getByTestId("result")).toHaveTextContent("ok"));
    expect(screen.getByTestId("username")).toHaveTextContent("existing-user");
    expect(authStore.getAccessToken()).toBe("access-token");
    expect(authStore.getRefreshToken()).toBe("refresh-token");
    expect(mocks.me).toHaveBeenCalledTimes(2);

    const [, request] = mocks.serverAwareFetch.mock.calls[0];
    expect(request).toMatchObject({ method: "POST" });
    expect(JSON.parse(String(request.body))).toEqual({ code: "browser-bound-code" });
  });

  it("does not store tokens when the one-time code is rejected", async () => {
    mocks.serverAwareFetch.mockResolvedValue(new Response(null, { status: 401 }));

    render(
      <AuthProvider authEnabled>
        <ExternalLoginProbe />
      </AuthProvider>,
    );

    await waitFor(() => expect(mocks.me).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole("button", { name: "Redeem" }));

    await waitFor(() => {
      expect(screen.getByTestId("result")).toHaveTextContent("External sign-in expired or was already used.");
    });
    expect(authStore.getAccessToken()).toBeNull();
    expect(authStore.getRefreshToken()).toBeNull();
    expect(mocks.me).toHaveBeenCalledTimes(1);
  });
});
