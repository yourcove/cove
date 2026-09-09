import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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
    expect(screen.getByRole("button", { name: "Use Default" })).toBeInTheDocument();
    expect(screen.getByTestId("cover-editor-extension")).toBeInTheDocument();
    expect(screen.getByText("Set Tag Cover").parentElement?.parentElement).toHaveClass(
      "max-h-[90vh]",
      "overflow-y-auto",
    );
    expect(slotCalls).toEqual([
      {
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
      },
    ]);
  });

  it("supports an explicit remove label", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <CoverImageDialog
          open
          title="Set Performer Cover"
          entityType="performer"
          entityId={41}
          currentImageUrl="/performer-41.jpg"
          onUpload={vi.fn()}
          onDelete={vi.fn()}
          onClose={vi.fn()}
          deleteLabel="Remove Image"
        />
      </QueryClientProvider>,
    );

    expect(screen.getByRole("button", { name: "Remove Image" })).toBeInTheDocument();
  });

  it("does not offer removal when only a generated fallback is displayed", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <CoverImageDialog
          open
          title="Set Video Cover"
          entityType="video"
          entityId={42}
          currentImageUrl="/generated-cover.jpg"
          onUpload={vi.fn()}
          onClose={vi.fn()}
          deleteLabel="Remove custom cover"
        />
      </QueryClientProvider>,
    );

    expect(screen.queryByRole("button", { name: "Remove custom cover" })).not.toBeInTheDocument();
  });

  it("reports image-operation pending state to frame actions", async () => {
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:preview");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
    const onUpload = vi.fn(() => new Promise(() => undefined));
    const { container } = render(
      <QueryClientProvider client={new QueryClient()}>
        <CoverImageDialog
          open
          title="Set Video Cover"
          entityType="video"
          entityId={42}
          onUpload={onUpload}
          onClose={vi.fn()}
          extraActions={(pending) => (
            <button type="button" disabled={pending}>
              From Current Frame
            </button>
          )}
        />
      </QueryClientProvider>,
    );
    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;

    fireEvent.change(fileInput, { target: { files: [new File(["image"], "cover.png", { type: "image/png" })] } });

    await waitFor(() => expect(screen.getByRole("button", { name: "From Current Frame" })).toBeDisabled());
    expect(onUpload).toHaveBeenCalledOnce();
  });
});
