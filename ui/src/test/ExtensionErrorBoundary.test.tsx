import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";

function Boom({ throwNow }: { throwNow: boolean }) {
  if (throwNow) throw new Error("kaboom");
  return <div>ok</div>;
}

describe("ExtensionErrorBoundary", () => {
  it("renders children when there is no error", () => {
    render(
      <ExtensionErrorBoundary>
        <Boom throwNow={false} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.getByText("ok")).toBeInTheDocument();
  });

  it("shows the default error box when a child throws and no fallback is given", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    render(
      <ExtensionErrorBoundary extensionId="com.example.ext">
        <Boom throwNow={true} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.getByText(/Extension error/)).toBeInTheDocument();
    spy.mockRestore();
  });

  it("renders nothing (not the error box) when a child throws and fallback is null", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const { container } = render(
      <ExtensionErrorBoundary extensionId="com.example.ext" fallback={null}>
        <Boom throwNow={true} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.queryByText(/Extension error/)).not.toBeInTheDocument();
    expect(container).toBeEmptyDOMElement();
    spy.mockRestore();
  });
});
