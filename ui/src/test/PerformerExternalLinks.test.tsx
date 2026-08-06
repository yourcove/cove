import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { PerformerExternalLinks } from "../components/PerformerExternalLinks";

const remoteIds = [
  { endpoint: "https://metadata-one.example/graphql", remoteId: "one" },
  { endpoint: "https://metadata-two.example/graphql", remoteId: "two" },
];

const urls = [
  "https://one.example/profile",
  "https://two.example/profile",
  "https://three.example/profile",
  "https://four.example/profile",
];

describe("PerformerExternalLinks", () => {
  it("uses a data-count disclosure instead of layout overflow", async () => {
    const user = userEvent.setup();
    render(<PerformerExternalLinks remoteIds={remoteIds} urls={urls} />);

    expect(screen.getAllByRole("link")).toHaveLength(4);
    const expand = screen.getByRole("button", { name: "Show all URLs" });
    expect(expand).toHaveAttribute("aria-expanded", "false");

    await user.click(expand);

    expect(screen.getAllByRole("link")).toHaveLength(6);
    const collapse = screen.getByRole("button", { name: "Show fewer URLs" });
    expect(collapse).toHaveAttribute("aria-expanded", "true");

    await user.click(collapse);

    expect(screen.getAllByRole("link")).toHaveLength(4);
    expect(screen.getByRole("button", { name: "Show all URLs" })).toBeInTheDocument();
  });

  it("renders every source without a disclosure when there are four or fewer", () => {
    render(<PerformerExternalLinks remoteIds={remoteIds.slice(0, 1)} urls={urls.slice(0, 3)} />);

    expect(screen.getAllByRole("link")).toHaveLength(4);
    expect(screen.queryByRole("button", { name: /URLs/ })).not.toBeInTheDocument();
  });
});
