import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TagBadge } from "../components/shared";

const groupedTag = { color: null, tagGroupColor: "#5f95ce", tagGroupName: "Example group" };

describe("TagBadge group marker", () => {
  it("names the tag's group in the marker tooltip", () => {
    render(<TagBadge name="Grouped tag" tag={groupedTag} />);

    expect(screen.getByTitle("Tag group: Example group")).toBeInTheDocument();
  });

  it("prefers an explicit group name over the tag's", () => {
    render(<TagBadge name="Grouped tag" tag={groupedTag} groupName="Other group" />);

    expect(screen.getByTitle("Tag group: Other group")).toBeInTheDocument();
  });

  it("falls back to a generic tooltip when the group name is unknown", () => {
    render(<TagBadge name="Grouped tag" groupColor="#5f95ce" />);

    expect(screen.getByTitle("Tag group")).toBeInTheDocument();
  });

  it("keeps the tag's group name when given the tag's own group color", () => {
    render(<TagBadge name="Grouped tag" tag={groupedTag} groupColor="#5F95CE" />);

    expect(screen.getByTitle("Tag group: Example group")).toBeInTheDocument();
  });

  it("doesn't borrow the tag's group name for an explicit color from another group", () => {
    render(<TagBadge name="Grouped tag" tag={groupedTag} groupColor="#a43737" />);

    expect(screen.getByTitle("Tag group")).toBeInTheDocument();
  });

  it("shows no marker for an ungrouped tag", () => {
    render(<TagBadge name="Ungrouped tag" tag={{ color: null, tagGroupColor: null, tagGroupName: null }} />);

    expect(screen.queryByTitle(/Tag group/)).not.toBeInTheDocument();
  });
});
