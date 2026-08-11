import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { NameConflictCleanupPanel } from "../features/tag-name-conflicts/NameConflictCleanupPanel";

const state = vi.hoisted(() => ({ summaryArguments: [] as [boolean, boolean] | [] }));

vi.mock("../features/tag-name-conflicts/useTagNameConflicts", () => ({
  useTagNameConflictSummary: (enabled: boolean, includeEntityConflicts: boolean) => {
    state.summaryArguments = [enabled, includeEntityConflicts];
    return {
      data: {
        tagUnresolvedGroupCount: 2,
        performerUnresolvedGroupCount: 3,
        studioUnresolvedGroupCount: 4,
      },
    };
  },
}));

vi.mock("../features/tag-name-conflicts/TagNameConflictCleanupPanel", () => ({
  TagNameConflictCleanupPanel: () => <div>Tag cleanup details</div>,
}));

vi.mock("../features/tag-name-conflicts/EntityNameConflictCleanupPanel", () => ({
  EntityNameConflictCleanupPanel: ({ entityType }: { entityType: string }) => <div>{entityType} cleanup details</div>,
}));

describe("NameConflictCleanupPanel", () => {
  it("keeps legacy tag-only delegates on the tag scanner", () => {
    render(<NameConflictCleanupPanel includeEntityConflicts={false} />);

    expect(state.summaryArguments).toEqual([true, false]);
    expect(screen.getByRole("tab", { name: /tags2/i })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /performers/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /studios/i })).not.toBeInTheDocument();
    expect(screen.getByText("Tag cleanup details")).toBeInTheDocument();
  });

  it("shows all conflict types to generalized cleanup administrators", () => {
    render(<NameConflictCleanupPanel includeEntityConflicts />);

    expect(state.summaryArguments).toEqual([true, true]);
    expect(screen.getByRole("tab", { name: /tags2/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /performers3/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /studios4/i })).toBeInTheDocument();
  });
});
