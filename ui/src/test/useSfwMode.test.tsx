import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AppConfigProvider, useSfwMode } from "../state/AppConfigContext";

const apiMocks = vi.hoisted(() => ({
  status: vi.fn(),
  getConfig: vi.fn(() => new Promise(() => undefined)),
}));

vi.mock("../api/client", () => ({
  system: apiMocks,
}));

function Probe() {
  return <div>{useSfwMode() ? "sfw" : "default"}</div>;
}

function renderProbe() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <AppConfigProvider>
        <Probe />
      </AppConfigProvider>
    </QueryClientProvider>,
  );
}

describe("useSfwMode", () => {
  beforeEach(() => {
    apiMocks.status.mockReset();
  });

  it("is on when the system status reports SFW mode, without needing the system configuration", async () => {
    apiMocks.status.mockResolvedValue({ authEnabled: true, sfwMode: true });

    renderProbe();

    expect(await screen.findByText("sfw")).toBeInTheDocument();
  });

  it("is off when the system status does not report SFW mode", async () => {
    apiMocks.status.mockResolvedValue({ authEnabled: true });

    renderProbe();

    expect(await screen.findByText("default")).toBeInTheDocument();
    expect(apiMocks.status).toHaveBeenCalled();
  });

  it("is off outside the app configuration provider", () => {
    render(<Probe />);

    expect(screen.getByText("default")).toBeInTheDocument();
  });
});
