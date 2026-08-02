import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  installFromZip: vi.fn(),
  refreshManifest: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    extensions: {
      ...actual.extensions,
      installFromZip: mocks.installFromZip,
      list: vi.fn().mockResolvedValue([]),
      registrySearch: vi.fn().mockResolvedValue({ items: [], totalCount: 0, pageSize: 20 }),
      registryGetCategories: vi.fn().mockResolvedValue([]),
      registryCheckUpdates: vi.fn().mockResolvedValue([]),
    },
  };
});

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    manifest: { tutorialTopics: [] },
    refreshManifest: mocks.refreshManifest,
  }),
}));

import { FindAndInstallExtensions } from "../pages/SettingsPage";

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(queryClient, "invalidateQueries");
  render(
    <QueryClientProvider client={queryClient}>
      <FindAndInstallExtensions />
    </QueryClientProvider>,
  );
  return { invalidate };
}

describe("direct extension ZIP installation", () => {
  beforeEach(() => {
    mocks.installFromZip.mockReset();
    mocks.refreshManifest.mockReset().mockResolvedValue({ tutorialTopics: [] });
  });

  it("keeps URL and ZIP actions distinct, requires a file, confirms trust, and invalidates caches on success", async () => {
    mocks.installFromZip.mockResolvedValue({ extensionId: "com.example.upload", version: "1.0.0" });
    const user = userEvent.setup();
    const { invalidate } = renderPanel();

    await user.click(screen.getByTitle("More extension actions"));
    expect(screen.getByRole("button", { name: "Install from URL..." })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Install from ZIP..." }));

    const formInstall = screen.getByRole("button", { name: /^Install$/ });
    expect(formInstall).toBeDisabled();
    const file = new File(["archive"], "extension.zip", { type: "application/zip" });
    await user.upload(screen.getByLabelText("Extension ZIP file"), file);
    expect(formInstall).toBeEnabled();
    await user.click(formInstall);

    expect(screen.getByText(/uploaded ZIP are unsafe/i)).toBeInTheDocument();
    const trustDialog = screen.getByRole("heading", { name: "Install Unverified Extension" }).parentElement!;
    await user.click(within(trustDialog).getByRole("button", { name: /^Install$/ }));

    await waitFor(() => expect(mocks.installFromZip).toHaveBeenCalledWith(file, true));
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["extensions-list"] }));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["registry-search"] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["registry-updates"] });
    expect(mocks.refreshManifest).toHaveBeenCalled();
  });

  it("shows upload failures in the trust dialog", async () => {
    mocks.installFromZip.mockRejectedValue(new Error("Malformed ZIP"));
    const user = userEvent.setup();
    renderPanel();

    await user.click(screen.getByTitle("More extension actions"));
    await user.click(screen.getByRole("button", { name: "Install from ZIP..." }));
    fireEvent.change(screen.getByLabelText("Extension ZIP file"), {
      target: { files: [new File(["bad"], "bad.zip", { type: "application/zip" })] },
    });
    await user.click(screen.getByRole("button", { name: /^Install$/ }));
    const trustDialog = screen.getByRole("heading", { name: "Install Unverified Extension" }).parentElement!;
    await user.click(within(trustDialog).getByRole("button", { name: /^Install$/ }));

    expect(await screen.findByText("Malformed ZIP")).toBeInTheDocument();
  });
});
