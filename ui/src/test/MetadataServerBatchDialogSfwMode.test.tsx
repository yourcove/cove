import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MetadataServerBatchDialog } from "../components/MetadataServerBatchDialog";

const mocks = vi.hoisted(() => ({ batchTag: vi.fn(), sfwMode: false }));

vi.mock("../api/client", () => ({
  performers: { batchTagMetadataServer: mocks.batchTag },
  studios: { batchTagMetadataServer: vi.fn() },
  tags: { batchTagMetadataServer: vi.fn() },
}));

const config = {
  scraping: {
    metadataServers: [{ name: "Sample", endpoint: "https://metadata.example/graphql", maxRequestsPerMinute: 60 }],
    metadataBatchDefaults: { excludeFields: ["country"] },
  },
};

vi.mock("../state/AppConfigContext", () => ({
  useSfwMode: () => mocks.sfwMode,
  useAppConfig: () => ({ config }),
}));

function renderDialog(entityType: "performer" | "studio" = "performer") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MetadataServerBatchDialog open entityType={entityType} selectedIds={[1, 2]} onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

const HIDDEN_LABELS = ["Measurements", "Fake tits", "Tattoos", "Piercings"];

describe("MetadataServerBatchDialog in SFW mode", () => {
  beforeEach(() => {
    mocks.batchTag.mockReset().mockResolvedValue({});
    mocks.sfwMode = false;
  });

  it("offers the adult-oriented fields and sends only the chosen exclusions outside SFW mode", async () => {
    renderDialog();

    for (const label of HIDDEN_LABELS) expect(screen.getByText(label)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /queue tagging/i }));

    await waitFor(() => expect(mocks.batchTag).toHaveBeenCalledTimes(1));
    expect(mocks.batchTag.mock.calls[0][0].excludeFields).toEqual(["country"]);
  });

  it("hides the adult-oriented fields and always keeps their current values in SFW mode", async () => {
    mocks.sfwMode = true;
    renderDialog();

    for (const label of HIDDEN_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    expect(screen.getByText("Ethnicity")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /queue tagging/i }));

    await waitFor(() => expect(mocks.batchTag).toHaveBeenCalledTimes(1));
    expect(mocks.batchTag.mock.calls[0][0].excludeFields).toEqual([
      "country",
      "measurements",
      "faketits",
      "tattoos",
      "piercings",
    ]);
  });
});
