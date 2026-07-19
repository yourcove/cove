import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const { slotCalls } = vi.hoisted(() => ({
  slotCalls: [] as Array<{ slot: string; context: Record<string, unknown>; contextResetKey?: unknown }>,
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: (props: { slot: string; context: Record<string, unknown>; contextResetKey?: unknown }) => {
    slotCalls.push(props);
    return <div data-testid="cover-editor-extension" />;
  },
}));

import { CoverImageDialog } from "../components/CoverImageDialog";

describe("CoverImageDialog extension media contract", () => {
  beforeEach(() => slotCalls.splice(0));

  it("exposes generic entity context beside the native cover editor", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <CoverImageDialog
        open
        title="Set Tag Cover"
        entityType="tag"
        entityId={17}
        currentImageUrl="/tag-17.jpg"
        onUpload={vi.fn()}
        onDelete={vi.fn()}
        onClose={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Cover")).toBeInTheDocument();
    expect(screen.getByTestId("cover-editor-extension")).toBeInTheDocument();
    expect(screen.getByText("Set Tag Cover").parentElement?.parentElement)
      .toHaveClass("max-h-[90vh]", "overflow-y-auto");
    expect(slotCalls).toEqual([{
      slot: "entity-cover-editor",
      context: {
        entityType: "tag",
        entityId: 17,
        coverKey: "primary",
        currentImageUrl: "/tag-17.jpg",
        canEdit: true,
      },
      contextResetKey: "tag:17:primary",
      fallback: null,
    }]);
  });
});
