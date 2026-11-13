import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { FilterDialog } from "../components/FilterDialog";
import { PERFORMER_CRITERIA } from "../components/filterCriteriaCatalogs";

const sfw = vi.hoisted(() => ({ mode: false }));

vi.mock("../state/AppConfigContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../state/AppConfigContext")>()),
  useSfwMode: () => sfw.mode,
}));

function renderDialog(activeFilter: Record<string, unknown>, onApply = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={activeFilter}
        onApply={onApply}
      />
    </QueryClientProvider>,
  );
  return onApply;
}

const HIDDEN_LABELS = ["Measurements", "Fake Tits", "Penis Length", "Circumcised", "Tattoos", "Piercings"];

describe("FilterDialog in SFW mode", () => {
  beforeEach(() => {
    sfw.mode = false;
  });

  it("offers the adult-oriented performer criteria outside SFW mode", () => {
    renderDialog({});

    for (const label of HIDDEN_LABELS) expect(screen.getByText(label)).toBeInTheDocument();
  });

  it("stops offering the adult-oriented performer criteria in SFW mode", () => {
    sfw.mode = true;
    renderDialog({});

    for (const label of HIDDEN_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    expect(screen.getByText("Ethnicity")).toBeInTheDocument();
    expect(screen.getByText("Career Start")).toBeInTheDocument();
  });

  it("keeps an already active hidden criterion visible and applies it unchanged in SFW mode", () => {
    sfw.mode = true;
    const activeFilter = { tattooCriterion: { value: "sample", modifier: "INCLUDES" } };
    const onApply = renderDialog(activeFilter);

    expect(screen.getAllByText("Tattoos").length).toBeGreaterThan(0);
    expect(screen.queryByText("Piercings")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply.mock.calls[0][0]).toMatchObject(activeFilter);
  });
});
