import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { StartupGate } from "../components/StartupGate";
import { AppConfigProvider } from "../state/AppConfigContext";

const apiMocks = vi.hoisted(() => ({
  status: vi.fn(),
  getConfig: vi.fn(() => new Promise(() => undefined)),
}));

vi.mock("../api/client", () => ({
  system: apiMocks,
}));

describe("StartupGate", () => {
  beforeEach(() => {
    apiMocks.status.mockReset();
  });

  it("keeps the connection screen visible while retrying and continues after recovery", async () => {
    let resolveRetry!: (status: { authEnabled: boolean }) => void;
    apiMocks.status.mockRejectedValueOnce(new TypeError("Failed to fetch")).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveRetry = resolve;
        }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <AppConfigProvider>
          <StartupGate>
            <div>Application ready</div>
          </StartupGate>
        </AppConfigProvider>
      </QueryClientProvider>,
    );

    await screen.findByRole("heading", { name: "Can’t connect to the Cove server" });
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("button", { name: "Trying again…" })).toBeDisabled();

    resolveRetry({ authEnabled: false });
    await waitFor(() => expect(screen.getByText("Application ready")).toBeInTheDocument());
  });
});
