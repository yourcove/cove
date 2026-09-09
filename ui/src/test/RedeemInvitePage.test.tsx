import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { RedeemInvitePage } from "../pages/RedeemInvitePage";

const mocks = vi.hoisted(() => ({
  bootstrapStatus: vi.fn(),
  inviteInfo: vi.fn(),
  redeemInvite: vi.fn(),
  redeemSetupToken: vi.fn(),
  refreshMe: vi.fn(),
}));

vi.mock("../api/client", () => ({
  auth: {
    bootstrapStatus: mocks.bootstrapStatus,
    inviteInfo: mocks.inviteInfo,
    redeemInvite: mocks.redeemInvite,
    redeemSetupToken: mocks.redeemSetupToken,
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ refreshMe: mocks.refreshMe }),
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <RedeemInvitePage />
    </QueryClientProvider>,
  );
}

describe("RedeemInvitePage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState({}, "", "/auth/redeem-invite?token=invite-token");
    mocks.bootstrapStatus.mockResolvedValue({ ownerExists: true, authEnabled: true, hasSetupToken: false });
    mocks.inviteInfo.mockResolvedValue({ valid: true, usernameRequired: false, username: "invited-user" });
  });

  it("shows password length guidance returned by the API", async () => {
    const user = userEvent.setup();
    mocks.redeemInvite.mockRejectedValue(
      new Error(
        `API Error 400: ${JSON.stringify({
          errors: { Password: ["Password must be 8-200 characters."] },
        })}`,
      ),
    );
    renderPage();

    await screen.findByDisplayValue("invited-user");
    await user.type(screen.getByLabelText("New password"), "short");
    await user.type(screen.getByLabelText("Confirm password"), "short");
    await user.click(screen.getByRole("button", { name: "Redeem" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Password must be 8-200 characters.");
    expect(mocks.redeemInvite).toHaveBeenCalledWith("invite-token", "short", "invited-user");
  });

  it("does not render internal details from an unexpected server error", async () => {
    const user = userEvent.setup();
    mocks.redeemInvite.mockRejectedValue(
      new Error("API Error 500: System.InvalidOperationException: internal stack trace"),
    );
    renderPage();

    await screen.findByDisplayValue("invited-user");
    await user.type(screen.getByLabelText("New password"), "long-enough-password");
    await user.type(screen.getByLabelText("Confirm password"), "long-enough-password");
    await user.click(screen.getByRole("button", { name: "Redeem" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The server returned an error. Please try again.");
    expect(screen.getByRole("alert")).not.toHaveTextContent("InvalidOperationException");
    expect(screen.getByRole("alert")).not.toHaveTextContent("stack trace");
  });
});
