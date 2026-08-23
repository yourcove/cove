import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ConfirmDialog } from "../components/ConfirmDialog";

describe("ConfirmDialog progress", () => {
  it("shows deletion progress and locks file options after a batch run starts", () => {
    const onConfirm = vi.fn();
    render(
      <ConfirmDialog
        open
        title="Delete 120 images"
        message="Deletion runs in batches."
        confirmLabel="Retry remaining"
        onConfirm={onConfirm}
        onCancel={() => {}}
        progress={{ completed: 50, total: 120 }}
        lockOptions
        showDeleteFile
        showDeleteGenerated
      />,
    );

    expect(screen.getByText("50/120 deleted")).toBeInTheDocument();
    expect(screen.getByLabelText("Also delete file from disk")).toBeDisabled();
    expect(screen.getByLabelText("Also delete generated files")).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: "Retry remaining" }));
    expect(onConfirm).toHaveBeenCalledWith({ deleteFile: false, deleteGenerated: false });
  });
});
