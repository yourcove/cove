import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  TagNameConflictReadinessStatus,
  TagNameConflictReadinessUnknownStatus,
  TagNameConflictWarning,
  TagNameConflictWarningBanner,
} from "../features/tag-name-conflicts/TagNameConflictWarning";

const state = vi.hoisted(() => ({
  canManage: false,
  unresolvedGroupCount: 2,
  hasSummary: true,
  isLoading: false,
  isError: false,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: () => state.canManage }),
}));

vi.mock("../features/tag-name-conflicts/useTagNameConflicts", () => ({
  TAG_NAME_CONFLICTS_PERMISSION: "tags.name-conflicts.manage",
  useTagNameConflictSummary: () => ({
    data: state.hasSummary ? { unresolvedGroupCount: state.unresolvedGroupCount } : undefined,
    isLoading: state.isLoading,
    isError: state.isError,
  }),
}));

describe("TagNameConflictWarning", () => {
  beforeEach(() => {
    state.canManage = false;
    state.unresolvedGroupCount = 2;
    state.hasSummary = true;
    state.isLoading = false;
    state.isError = false;
  });

  it("shows the upgrade warning, unresolved count, and direct cleanup link", () => {
    render(<TagNameConflictWarningBanner unresolvedGroupCount={2} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Tag names will become globally unique in Cove 1.3.0.");
    expect(screen.getByRole("alert")).toHaveTextContent("Some current tags or aliases conflict after trimming. Review and resolve them before upgrading.");
    expect(screen.getByRole("link", { name: /2 groups/i })).toHaveAttribute("href", "/settings/operations/tag-name-conflicts");
  });

  it("reports blocked and ready states for the runtime readiness area", () => {
    const view = render(<TagNameConflictReadinessStatus unresolvedGroupCount={2} />);
    expect(screen.getByText(/2 unresolved tag-name conflict groups would block/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /review conflicts/i })).toHaveAttribute("href", "/settings/operations/tag-name-conflicts");

    view.rerender(<TagNameConflictReadinessStatus unresolvedGroupCount={0} />);
    expect(screen.getByText(/ready: no tag-name conflicts/i)).toBeInTheDocument();
  });

  it("is hidden without the administrator cleanup permission", () => {
    render(<TagNameConflictWarning />);

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("is visible to an administrator only while conflicts remain", () => {
    state.canManage = true;
    const view = render(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toBeInTheDocument();

    state.unresolvedGroupCount = 0;
    view.rerender(<TagNameConflictWarning />);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("fails closed while readiness is checking or unavailable", () => {
    state.canManage = true;
    state.hasSummary = false;
    state.isLoading = true;
    const view = render(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toHaveTextContent(/checking Cove 1.3.0 tag readiness/i);
    expect(screen.getByText(/do not upgrade/i)).toBeInTheDocument();

    state.isLoading = false;
    state.isError = true;
    view.rerender(<TagNameConflictWarning />);
    expect(screen.getByRole("alert")).toHaveTextContent(/could not be determined/i);
    expect(screen.getByRole("link", { name: /open checker/i })).toHaveAttribute("href", "/settings/operations/tag-name-conflicts");
  });

  it("shows readiness unknown in Runtime Status instead of implying success", () => {
    render(<TagNameConflictReadinessUnknownStatus checking={false} />);

    expect(screen.getByRole("status")).toHaveTextContent(/readiness unknown/i);
    expect(screen.getByText(/compatibility scan failed/i)).toBeInTheDocument();
  });
});
