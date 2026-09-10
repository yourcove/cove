import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ExtensionRuntimeFailure } from "../extensions/ExtensionRuntimeReconciler";
import { ExtensionLoadFailureDetails, ExtensionLoadNotice } from "../extensions/ExtensionLoadStatus";

const state = vi.hoisted(() => ({ failures: [] as ExtensionRuntimeFailure[] }));
vi.mock("../extensions/ExtensionLoader", () => ({ useExtensions: () => ({ loadFailures: state.failures }) }));

afterEach(() => {
  cleanup();
  state.failures = [];
});

describe("extension loading status", () => {
  it("keeps a notice dismissed across rerenders, but shows a new failure after recovery", () => {
    const failure: ExtensionRuntimeFailure = { extensionId: "broken", phase: "load", message: "Failed import" };
    state.failures = [failure];
    const view = render(<ExtensionLoadNotice />);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss extension loading notice" }));
    view.rerender(<ExtensionLoadNotice />);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    state.failures = [];
    view.rerender(<ExtensionLoadNotice />);
    state.failures = [failure];
    view.rerender(<ExtensionLoadNotice />);
    expect(screen.getByRole("alert")).toHaveTextContent("1 extension couldn’t load its UI.");
  });

  it("offers a page reload for cleanup failures instead of retrying initialization", () => {
    const failure: ExtensionRuntimeFailure = { extensionId: "removed", phase: "cleanup", message: "Unload failed" };
    render(<ExtensionLoadFailureDetails failure={failure} retry={vi.fn()} />);
    expect(screen.getByText("UI cleanup failed")).toBeInTheDocument();
    expect(screen.getByText("Unload failed")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload Cove" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry failed extensions" })).not.toBeInTheDocument();
  });

  it("explains blocked dependencies and reports retry request failures", async () => {
    const failure: ExtensionRuntimeFailure = {
      extensionId: "dependent",
      phase: "dependency",
      message: "Required extension 'base' could not load its UI.",
    };
    let reject!: (error: Error) => void;
    const retry = vi.fn(
      () =>
        new Promise<void>((_resolve, fail) => {
          reject = fail;
        }),
    );
    render(<ExtensionLoadFailureDetails failure={failure} retry={retry} />);
    expect(screen.getByText("UI blocked by dependency")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry failed extensions" }));
    expect(screen.getByRole("button", { name: "Retrying…" })).toBeDisabled();
    await act(async () => reject(new Error("Offline")));
    expect(screen.getByRole("alert")).toHaveTextContent("Couldn’t retry extension loading.");
    expect(screen.getByRole("button", { name: "Retry failed extensions" })).toBeEnabled();
  });
});
