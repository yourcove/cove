import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { LoginPage } from "../pages/LoginPage";

const mocks = vi.hoisted(() => ({
  bootstrapStatus: vi.fn(),
  externalProviders: vi.fn(),
  login: vi.fn(),
  externalLoginRedeem: vi.fn(),
}));

vi.mock("../api/client", () => ({
  auth: {
    bootstrapStatus: mocks.bootstrapStatus,
    externalProviders: mocks.externalProviders,
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    login: mocks.login,
    externalLoginRedeem: mocks.externalLoginRedeem,
  }),
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <LoginPage />
    </QueryClientProvider>,
  );
}

describe("LoginPage external authentication", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState({}, "", "/login");
    mocks.bootstrapStatus.mockResolvedValue({
      ownerExists: true,
      authEnabled: true,
      hasSetupToken: false,
    });
    mocks.externalProviders.mockResolvedValue([]);
    mocks.login.mockResolvedValue({ ok: true });
    mocks.externalLoginRedeem.mockResolvedValue({ ok: true });
  });

  it("renders safe extension login methods and carries the local return URL", async () => {
    window.history.replaceState({}, "", "/login?redirect=%2Fsettings%3Ftab%3Dsecurity");
    mocks.externalProviders.mockResolvedValue([
      {
        id: "example-sso",
        label: "Sign in with Example SSO",
        startUrl: "/api/plugins/example.authentication/start",
        order: 10,
        extensionId: "example.authentication",
      },
      {
        id: "unsafe",
        label: "Leave Cove",
        startUrl: "https://untrusted.invalid/start",
        order: 20,
        extensionId: "untrusted.extension",
      },
    ]);

    renderPage();

    const link = await screen.findByRole("link", { name: "Sign in with Example SSO" });
    expect(link).toHaveAttribute(
      "href",
      "/api/plugins/example.authentication/start?returnUrl=%2Fsettings%3Ftab%3Dsecurity",
    );
    expect(screen.queryByRole("link", { name: "Leave Cove" })).not.toBeInTheDocument();
  });

  it("redeems a fragment-carried external login code once and removes it without losing redirect", async () => {
    window.history.replaceState(
      {},
      "",
      "/login?redirect=%2Fsettings#external_login_code=one-time-code",
    );

    renderPage();

    await waitFor(() => {
      expect(mocks.externalLoginRedeem).toHaveBeenCalledTimes(1);
      expect(mocks.externalLoginRedeem).toHaveBeenCalledWith("one-time-code");
    });

    const url = new URL(window.location.href);
    expect(url.searchParams.has("external_login_code")).toBe(false);
    expect(url.searchParams.get("redirect")).toBe("/settings");
    expect(url.hash).toBe("");
  });

  it("continues to redeem and scrub a query-carried code for extension compatibility", async () => {
    window.history.replaceState({}, "", "/login?external_login_code=legacy-code");

    renderPage();

    await waitFor(() => {
      expect(mocks.externalLoginRedeem).toHaveBeenCalledTimes(1);
      expect(mocks.externalLoginRedeem).toHaveBeenCalledWith("legacy-code");
    });
    expect(window.location.search).toBe("");
  });

  it("shows a generic error and scrubs provider error details from the URL", async () => {
    window.history.replaceState(
      {},
      "",
      "/login#external_login_error=provider-returned-sensitive-detail",
    );

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "External sign-in failed. Please try again.",
    );
    expect(window.location.hash).toBe("");
    expect(mocks.externalLoginRedeem).not.toHaveBeenCalled();
  });

  it("reports an expired or already-used redemption", async () => {
    window.history.replaceState({}, "", "/login#external_login_code=expired-code");
    mocks.externalLoginRedeem.mockResolvedValue({
      ok: false,
      error: "External sign-in expired or was already used.",
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "External sign-in expired or was already used.",
    );
  });

  it("rejects ambiguous code markers without redeeming either value", async () => {
    window.history.replaceState(
      {},
      "",
      "/login?external_login_code=first#external_login_code=second",
    );

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "External sign-in expired or was already used.",
    );
    expect(mocks.externalLoginRedeem).not.toHaveBeenCalled();
    expect(window.location.search).toBe("");
    expect(window.location.hash).toBe("");
  });
});
