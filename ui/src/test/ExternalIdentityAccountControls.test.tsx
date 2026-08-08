import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  externalProviders: vi.fn(),
  externalLinks: vi.fn(),
  startExternalLink: vi.fn(),
  previewExternalLink: vi.fn(),
  confirmExternalLink: vi.fn(),
  cancelExternalLink: vi.fn(),
  removeExternalLink: vi.fn(),
  changePassword: vi.fn(),
  logout: vi.fn(),
  user: {
    id: "1",
    username: "alice",
    permissions: [],
    hasPassword: true,
    isSystem: false,
  },
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    auth: {
      ...actual.auth,
      externalProviders: mocks.externalProviders,
      externalLinks: mocks.externalLinks,
      startExternalLink: mocks.startExternalLink,
      previewExternalLink: mocks.previewExternalLink,
      confirmExternalLink: mocks.confirmExternalLink,
      cancelExternalLink: mocks.cancelExternalLink,
      removeExternalLink: mocks.removeExternalLink,
      changePassword: mocks.changePassword,
    },
  };
});

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    authEnabled: true,
    user: mocks.user,
    logout: mocks.logout,
  }),
}));

import { ExternalIdentityAccountControls } from "../pages/SettingsPage";

function renderControls() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <ExternalIdentityAccountControls />
    </QueryClientProvider>,
  );
}

describe("external identity account controls", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState({}, "", "/settings/my/account");
    Object.assign(mocks.user, { hasPassword: true, isSystem: false });
    mocks.externalProviders.mockResolvedValue([{
      id: "provider-a",
      label: "Example SSO",
      startUrl: "/api/plugins/example/login",
      linkStartUrl: "/api/plugins/example/link",
      extensionId: "example",
      order: 10,
    }]);
    mocks.externalLinks.mockResolvedValue([{
      id: 4,
      userId: 1,
      extensionId: "example",
      providerId: "provider-a",
      providerLabel: "Example SSO",
      accountLabel: "alice@example.test",
      createdAt: new Date().toISOString(),
    }]);
    mocks.startExternalLink.mockResolvedValue({ confirmationCode: "pending-code" });
    mocks.previewExternalLink.mockResolvedValue({
      providerLabel: "Example SSO",
      accountLabel: "alice@example.test",
    });
    mocks.confirmExternalLink.mockResolvedValue({ id: 5 });
  });

  it("lists linked identities and explicitly confirms a new provider link", async () => {
    const user = userEvent.setup();
    renderControls();

    expect(await screen.findByText("alice@example.test")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Link Example SSO" }));
    await waitFor(() => expect(mocks.startExternalLink).toHaveBeenCalledWith("/api/plugins/example/link"));

    expect(await screen.findByText("Confirm external identity")).toBeInTheDocument();
    expect(screen.getAllByText("alice@example.test")).toHaveLength(2);
    await user.click(screen.getByRole("button", { name: "Confirm link" }));

    await waitFor(() => expect(mocks.confirmExternalLink).toHaveBeenCalledWith("pending-code"));
  });

  it("keeps local passwords non-removable for every user", async () => {
    renderControls();

    expect(await screen.findByText(/External sign-in is optional/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove local password" })).not.toBeInTheDocument();
  });
});
