import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  TagNameConflictReadinessStatus,
  TagNameConflictReadinessUnknownStatus,
  TagNameConflictWarning,
  TagNameConflictWarningBanner,
} from "../features/tag-name-conflicts/TagNameConflictWarning";

const state = vi.hoisted(() => ({
  permissions: [] as string[],
  summaryArguments: [] as [boolean, boolean] | [],
  unresolvedGroupCount: 2,
  hasSummary: true,
  isLoading: false,
  isError: false,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: (permission: string) => state.permissions.includes(permission) }),
}));

vi.mock("../features/tag-name-conflicts/useTagNameConflicts", () => ({
  TAG_NAME_CONFLICTS_PERMISSION: "tags.name-conflicts.manage",
  ENTITY_NAME_CONFLICTS_PERMISSION: "entities.name-conflicts.manage",
  useTagNameConflictSummary: (enabled: boolean, includeEntityConflicts: boolean) => {
    state.summaryArguments = [enabled, includeEntityConflicts];
    return {
      data: state.hasSummary ? { unresolvedGroupCount: state.unresolvedGroupCount } : undefined,
      isLoading: state.isLoading,
      isError: state.isError,
    };
  },
}));

describe("TagNameConflictWarning", () => {
  beforeEach(() => {
    state.permissions = [];
    state.summaryArguments = [];
    state.unresolvedGroupCount = 2;
    state.hasSummary = true;
    state.isLoading = false;
    state.isError = false;
  });

  it("shows the upgrade warning, unresolved count, and direct cleanup link", () => {
    render(<TagNameConflictWarningBanner unresolvedGroupCount={2} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Cove 1.3.0 will enforce new tag, performer, and studio name rules.");
    expect(screen.getByRole("alert")).toHaveTextContent("Some current identities conflict after trimming and case folding. Review and resolve them before upgrading.");
    expect(screen.getByRole("link", { name: /2 groups/i })).toHaveAttribute("href", "/settings/operations/name-conflicts");
  });

  it("reports blocked and ready states for the runtime readiness area", () => {
    const view = render(<TagNameConflictReadinessStatus unresolvedGroupCount={2} />);
    expect(screen.getByText(/2 unresolved name conflict groups would block/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /review conflicts/i })).toHaveAttribute("href", "/settings/operations/name-conflicts");

    view.rerender(<TagNameConflictReadinessStatus unresolvedGroupCount={0} />);
    expect(screen.getByText(/ready: no tag, performer, or studio name conflicts/i)).toBeInTheDocument();
  });

  it("is hidden without the administrator cleanup permission", () => {
    render(<TagNameConflictWarning />);

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("is visible to an administrator only while conflicts remain", () => {
    state.permissions = ["entities.name-conflicts.manage"];
    const view = render(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toBeInTheDocument();

    state.unresolvedGroupCount = 0;
    view.rerender(<TagNameConflictWarning />);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("fails closed while readiness is checking or unavailable", () => {
    state.permissions = ["entities.name-conflicts.manage"];
    state.hasSummary = false;
    state.isLoading = true;
    const view = render(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toHaveTextContent(/checking Cove 1.3.0 name readiness/i);
    expect(screen.getByText(/do not upgrade/i)).toBeInTheDocument();

    state.isLoading = false;
    state.isError = true;
    view.rerender(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toHaveTextContent(/could not be determined/i);
    expect(screen.getByRole("link", { name: /open checker/i })).toHaveAttribute("href", "/settings/operations/name-conflicts");
  });

  it("preserves tag-only warnings without requesting performer or studio details", () => {
    state.permissions = ["tags.name-conflicts.manage"];

    render(<TagNameConflictWarning />);

    expect(state.summaryArguments).toEqual([true, false]);
    expect(screen.getByRole("alert")).toHaveTextContent(/globally unique tag names and aliases/i);
    expect(screen.getByRole("alert")).not.toHaveTextContent(/performer/i);
  });

  it("shows readiness unknown in Runtime Status instead of implying success", () => {
    render(<TagNameConflictReadinessUnknownStatus checking={false} />);

    expect(screen.getByRole("status")).toHaveTextContent(/readiness unknown/i);
    expect(screen.getByText(/compatibility scan failed/i)).toBeInTheDocument();
  });
});
