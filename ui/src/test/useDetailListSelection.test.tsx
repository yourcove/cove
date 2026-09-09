import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { useDetailListSelection } from "../hooks/useDetailListSelection";

interface ProbeProps {
  infinitePageSize: boolean;
  resetToken: string;
  fetchAllIds: () => Promise<number[]>;
}

function SelectionProbe({ infinitePageSize, resetToken, fetchAllIds }: ProbeProps) {
  const selection = useDetailListSelection({
    items: [{ id: 1 }, { id: 2 }],
    infinitePageSize,
    infiniteFilterKey: resetToken,
    fetchAllIds,
  });

  return (
    <div>
      <div data-testid="selected">{[...selection.selectedIds].join(",")}</div>
      <button
        type="button"
        onClick={() => {
          void selection.selectAll();
        }}
      >
        Select all
      </button>
      {selection.selectShown ? (
        <button type="button" onClick={selection.selectShown}>
          Select shown
        </button>
      ) : null}
      <div data-testid="pending">{selection.selectAllPending ? "pending" : "idle"}</div>
    </div>
  );
}

describe("useDetailListSelection", () => {
  it("uses all matching ids for infinite select all and keeps loaded-window selection separate", async () => {
    const user = userEvent.setup();
    const fetchAllIds = vi.fn(async () => [1, 2, 3, 4]);

    render(<SelectionProbe infinitePageSize resetToken="initial" fetchAllIds={fetchAllIds} />);

    await user.click(screen.getByRole("button", { name: "Select all" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1,2,3,4");
    });
    expect(fetchAllIds).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: "Select shown" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1,2");
    });
  });

  it("uses loaded rows for finite select all", async () => {
    const user = userEvent.setup();
    const fetchAllIds = vi.fn(async () => [1, 2, 3, 4]);

    render(<SelectionProbe infinitePageSize={false} resetToken="initial" fetchAllIds={fetchAllIds} />);

    await user.click(screen.getByRole("button", { name: "Select all" }));

    expect(screen.getByTestId("selected")).toHaveTextContent("1,2");
    expect(fetchAllIds).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Select shown" })).not.toBeInTheDocument();
  });

  it("clears selection when the infinite query identity changes", async () => {
    const user = userEvent.setup();
    const fetchAllIds = vi.fn(async () => [1, 2, 3, 4]);
    const { rerender } = render(<SelectionProbe infinitePageSize resetToken="initial" fetchAllIds={fetchAllIds} />);

    await user.click(screen.getByRole("button", { name: "Select all" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1,2,3,4");
    });

    rerender(<SelectionProbe infinitePageSize resetToken="changed" fetchAllIds={fetchAllIds} />);

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toBeEmptyDOMElement();
    });
  });
});
