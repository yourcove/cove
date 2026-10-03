import { useState } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  MetadataDiff,
  defaultDiffSelection,
  scalarStatus,
  summarizeDiff,
  type DiffField,
  type DiffRecord,
} from "../components/MetadataDiff";

const fields: DiffField[] = [
  { key: "title", label: "Title" },
  { key: "description", label: "Description" },
  { key: "empty", label: "Empty field" },
  { key: "missing", label: "Missing field" },
  { key: "same", label: "Same field" },
  { key: "fill", label: "Fillable field" },
  { key: "tags", label: "Tags", kind: "list", itemKey: (item) => String(item).toLowerCase() },
  { key: "cover", label: "Cover", render: (value) => <img alt="Custom cover" src={String(value)} /> },
];
// `source` is the incoming side, `target` the kept side.
const source: DiffRecord = {
  label: "Scraped",
  values: {
    title: "Scraped title",
    description: "Scraped description",
    empty: null,
    same: "Same",
    fill: "Filled in",
    tags: ["Shared", "New"],
    cover: "/source.png",
  },
  provenance: { title: "StashDB" },
};
const target: DiffRecord = {
  label: "Library",
  values: {
    title: "Library title",
    description: "Library description",
    empty: "Populated",
    missing: "Present",
    same: "Same",
    fill: null,
    tags: ["shared", "Existing"],
    cover: "/target.png",
  },
};
function Harness() {
  const [value, onChange] = useState(() => defaultDiffSelection(fields, source, target));
  return <MetadataDiff {...{ fields, source, target, value, onChange }} />;
}
describe("MetadataDiff", () => {
  it("keeps conflicts, fills empty fields from the incoming side, and lets each field be chosen", () => {
    render(<Harness />);
    expect(screen.getByLabelText("Title from target")).toBeChecked();
    expect(screen.getByLabelText("Fillable field from source")).toBeChecked();
    expect(screen.getByLabelText("Empty field from target")).toBeChecked();
    fireEvent.click(screen.getByLabelText("Title from source"));
    fireEvent.click(screen.getByLabelText("Empty field from source"));
    expect(screen.getByLabelText("Title from source")).toBeChecked();
    expect(screen.getByLabelText("Description from target")).toBeChecked();
    expect(screen.getByLabelText("Empty field from source")).toBeChecked();
    expect(screen.getByLabelText("Missing field from source")).toBeDisabled();
    expect(within(screen.getByRole("group", { name: "Title" })).getByText("StashDB")).toBeInTheDocument();
    expect(scalarStatus(fields[0], source, target)).toBe("conflict");
    expect(scalarStatus(fields[5], source, target)).toBe("filled");
    expect(scalarStatus(fields[4], source, target)).toBe("identical");
  });
  it("labels the outcome of every visible row", () => {
    render(<Harness />);
    expect(
      within(screen.getByRole("group", { name: "Title" })).getAllByText("Conflict · keeping library"),
    ).not.toHaveLength(0);
    expect(
      within(screen.getByRole("group", { name: "Fillable field" })).getAllByText("Filled from scraped"),
    ).not.toHaveLength(0);
    fireEvent.click(screen.getByLabelText("Title from source"));
    expect(within(screen.getByRole("group", { name: "Title" })).getAllByText("Replaced from scraped")).not.toHaveLength(
      0,
    );
  });
  it("shows list items as chips and allows leaving out shared and unique items", () => {
    render(<Harness />);
    expect(screen.getByRole("button", { name: "Remove Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove Tags: shared" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Tags: new" }));
    expect(screen.getByRole("button", { name: "Add Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    const group = screen.getByRole("group", { name: "Tags" });
    expect(group.querySelectorAll('[data-state="excluded"]')).toHaveLength(2);
    expect(group.querySelectorAll('[data-state="kept"]')).toHaveLength(1);
  });
  it("applies combined, kept-only and incoming-only presets", () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "Use source Tags" }));
    expect(screen.getByRole("button", { name: "Remove Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
    expect(screen.getByRole("button", { name: "Add Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use combined Tags" })).toHaveAttribute("aria-pressed", "true");
  });
  it("collapses identical fields and keeps always-visible rows", () => {
    render(<Harness />);
    expect(screen.queryByRole("group", { name: "Same field" })).not.toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Cover" })).toBeInTheDocument();
    expect(screen.getAllByAltText("Custom cover")).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: /1 identical/ }));
    expect(screen.getByRole("group", { name: "Same field" })).toBeInTheDocument();
  });
  it("supports sentence labels, will-create chips, mode-only lists and locked kept items", () => {
    const applyFields: DiffField[] = [
      {
        key: "tags",
        label: "Tags",
        kind: "list",
        itemKey: (item) => String(item).toLowerCase(),
        itemIsNew: (item) => item === "Brand new",
        lockKeptItems: true,
      },
      { key: "urls", label: "URLs", kind: "list", itemKey: (item) => String(item), modesOnly: true },
    ];
    const incoming: DiffRecord = {
      label: "From StashDB",
      sentenceLabel: "StashDB",
      values: { tags: ["Shared", "Brand new"], urls: ["https://a"] },
    };
    const current: DiffRecord = {
      label: "Current",
      sentenceLabel: "current",
      values: { tags: ["shared", "Existing"], urls: ["https://b"] },
    };
    function ApplyHarness() {
      const [value, onChange] = useState(() => defaultDiffSelection(applyFields, incoming, current));
      return <MetadataDiff {...{ fields: applyFields, source: incoming, target: current, value, onChange }} />;
    }
    render(<ApplyHarness />);
    const tags = screen.getByRole("group", { name: "Tags" });
    expect(tags.querySelector('[data-state="new"]')).toHaveTextContent("Brand new");
    expect(within(tags).getByText(/1 added \(1 new\)/)).toBeInTheDocument();
    expect(within(tags).queryByRole("button", { name: "Remove Tags: existing" })).not.toBeInTheDocument();
    expect(within(tags).getByRole("button", { name: "Remove Tags: brand new" })).toBeInTheDocument();
    expect(within(tags).getByRole("button", { name: "Use source Tags" })).toHaveTextContent("Only StashDB");
    expect(within(tags).getByRole("button", { name: "Use target Tags" })).toHaveTextContent("Only current");
    const urls = screen.getByRole("group", { name: "URLs" });
    expect(within(urls).queryByRole("button", { name: /Remove URLs/ })).not.toBeInTheDocument();
    expect(within(urls).getByRole("button", { name: "Use combined URLs" })).toBeInTheDocument();
    const summary = summarizeDiff(applyFields, incoming, current, defaultDiffSelection(applyFields, incoming, current));
    expect(summary.changes.map((change) => change.text)).toEqual(["1 tag added (1 new)", "1 url added"]);
  });

  it("summarises what the selection will do", () => {
    const value = defaultDiffSelection(fields, source, target);
    const summary = summarizeDiff(fields, source, target, value);
    expect(summary.changeCount).toBe(2);
    expect(summary.changes.map((change) => change.text)).toEqual([
      "Fillable field filled from scraped",
      "1 tag added",
      "Title, Description, Cover kept from library (conflict)",
      "3 unchanged",
    ]);
  });
});
