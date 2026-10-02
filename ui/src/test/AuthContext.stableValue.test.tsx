import { render, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "../auth/AuthContext";

vi.mock("../api/client", () => ({
  auth: { me: vi.fn().mockRejectedValue(new Error("not authenticated")) },
  nextAccessTokenRefreshAt: () => null,
  refreshAccessTokenIfDue: async () => {},
}));

vi.mock("../state/serverAvailability", () => ({
  serverAwareFetch: vi.fn(),
}));

describe("AuthProvider value identity", () => {
  it("keeps hasPermission stable across provider re-renders when auth is disabled", async () => {
    const seen: Array<{ loading: boolean; hasPermission: (permission: string) => boolean }> = [];
    function Probe() {
      const { loading, hasPermission } = useAuth();
      seen.push({ loading, hasPermission });
      return null;
    }

    const { rerender } = render(
      <AuthProvider authEnabled={false}>
        <Probe />
      </AuthProvider>,
    );
    await waitFor(() => expect(seen.at(-1)?.loading).toBe(false));
    const settled = seen.at(-1)!.hasPermission;

    rerender(
      <AuthProvider authEnabled={false}>
        <Probe />
      </AuthProvider>,
    );

    expect(seen.at(-1)!.hasPermission).toBe(settled);
    expect(settled("videos.write")).toBe(true);
  });
});
