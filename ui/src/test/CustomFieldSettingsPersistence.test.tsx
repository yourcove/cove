import { QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { customFields } from "../api/client";
import { MutationFailureNotice } from "../components/MutationFailureNotice";
import { SettingsPage } from "../pages/SettingsPage";
import { createAppQueryClient } from "../queryClient";
import { resetMutationFailureForTests } from "../state/mutationFailure";

vi.mock("../state/AppConfigContext", () => {
  const config = { covePaths: [], scraping: { scraperDirectories: [] }, ui: {} };
  return { useAppConfig: () => ({ config, configLoading: false, statusLoading: false }) };
});

vi.mock("../auth/AuthContext", () => {
  const hasPermission = () => true;
  return { useAuth: () => ({ authEnabled: false, user: null, hasPermission }) };
});

vi.mock("../extensions/ExtensionLoader", () => {
  const extensions = {
    settingsTabs: [],
    loaded: true,
    getSettingsPanelsForTab: () => [],
    resolveComponent: () => null,
  };
  return { useExtensions: () => extensions };
});

describe("Custom field settings persistence", () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    resetMutationFailureForTests();
    window.history.replaceState({}, "", "/");
  });

  it("shows the global failure notice when deleting a definition fails and retains the inline error", async () => {
    window.history.replaceState({}, "", "/settings/library/custom-fields");
    const queryClient = createAppQueryClient();
    queryClient.setDefaultOptions({ queries: { staleTime: Infinity, retry: false } });
    queryClient.setQueryData(["plugins", "tasks"], []);
    queryClient.setQueryData(["ffmpeg-capabilities"], { accelerators: [] });
    queryClient.setQueryData(
      ["custom-fields", "all"],
      [
        {
          id: 1,
          key: "example",
          label: "Example",
          type: "text",
          entityTypes: ["video"],
          options: [],
          filterable: true,
          sortable: false,
        },
      ],
    );
    const save = vi.spyOn(customFields, "replaceAll").mockRejectedValue(new Error("Duplicate field key"));

    render(
      <QueryClientProvider client={queryClient}>
        <MutationFailureNotice />
        <SettingsPage />
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Remove custom field definition" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn’t complete the action");
    expect(screen.getByText("Duplicate field key")).toBeInTheDocument();
    expect(save).toHaveBeenCalledWith([]);
    queryClient.clear();
  });
});
