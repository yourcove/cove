import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import * as ErrorBoundaryModule from "../components/ErrorBoundary";

function ThrowingComponent({ shouldThrow }: { shouldThrow: boolean }) {
  if (shouldThrow) throw new Error("Test error");
  return <div>Working</div>;
}

describe("ErrorBoundary", () => {
  it("renders children when no error", () => {
    render(
      <ErrorBoundaryModule.ErrorBoundary>
        <div>Hello</div>
      </ErrorBoundaryModule.ErrorBoundary>
    );
    expect(screen.getByText("Hello")).toBeInTheDocument();
  });

  it("renders error UI when child throws", () => {
    // Suppress console.error for the expected error
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    render(
      <ErrorBoundaryModule.ErrorBoundary>
        <ThrowingComponent shouldThrow={true} />
      </ErrorBoundaryModule.ErrorBoundary>
    );
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();
    expect(screen.getByText("Test error")).toBeInTheDocument();
    expect(screen.getByText("Try Again")).toBeInTheDocument();
    spy.mockRestore();
  });

  it("reloads the page when Try Again is clicked", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const onRetry = vi.fn();

    render(
      <ErrorBoundaryModule.ErrorBoundary onRetry={onRetry}>
        <ThrowingComponent shouldThrow={true} />
      </ErrorBoundaryModule.ErrorBoundary>
    );
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();

    fireEvent.click(screen.getByText("Try Again"));

    expect(onRetry).toHaveBeenCalledTimes(1);
    spy.mockRestore();
  });
});
