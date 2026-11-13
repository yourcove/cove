import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CoveConfig } from "../api/types";
import { SetupWizardPage } from "../pages/SetupWizardPage";

const mocks = vi.hoisted(() => ({
  saveConfig: vi.fn(),
  bootstrapStatus: vi.fn(),
}));

vi.mock("../api/client", () => ({
  auth: { bootstrapStatus: mocks.bootstrapStatus },
  database: {},
  jobs: {},
  metadata: {},
  stashMigration: {},
  system: { saveConfig: mocks.saveConfig },
}));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ refreshMe: vi.fn() }) }));
vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({ availableThemes: [], activeThemeId: null, setActiveTheme: vi.fn() }),
}));

const config = {
  covePaths: [{ path: "/media", excludeVideo: false, excludeImage: false, excludeAudio: false, excludeText: false }],
  ui: { language: "en" },
} as unknown as CoveConfig;

async function openConfirmStep() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SetupWizardPage config={config} onComplete={vi.fn()} />
    </QueryClientProvider>,
  );
  await userEvent.click(screen.getByRole("button", { name: /get started/i }));
  await userEvent.click(screen.getByRole("button", { name: /start fresh/i }));
  await userEvent.click(screen.getByRole("button", { name: /next/i }));
}

describe("setup wizard SFW mode", () => {
  beforeEach(() => {
    mocks.saveConfig.mockReset().mockResolvedValue({});
    mocks.bootstrapStatus.mockReset().mockResolvedValue({ ownerExists: true });
  });

  it("leaves SFW mode off unless it is chosen", async () => {
    await openConfirmStep();

    expect(screen.getByRole("checkbox", { name: /SFW mode/ })).not.toBeChecked();
    await userEvent.click(screen.getByRole("button", { name: /save & continue/i }));

    await waitFor(() => expect(mocks.saveConfig).toHaveBeenCalledTimes(1));
    expect(mocks.saveConfig.mock.calls[0][0].ui).toEqual({ language: "en", sfwMode: false });
  });

  it("saves SFW mode with the rest of the interface settings when chosen", async () => {
    await openConfirmStep();

    await userEvent.click(screen.getByRole("checkbox", { name: /SFW mode/ }));
    await userEvent.click(screen.getByRole("button", { name: /save & continue/i }));

    await waitFor(() => expect(mocks.saveConfig).toHaveBeenCalledTimes(1));
    expect(mocks.saveConfig.mock.calls[0][0].ui).toEqual({ language: "en", sfwMode: true });
    expect(mocks.saveConfig.mock.calls[0][0].covePaths).toEqual(config.covePaths);
  });
});
