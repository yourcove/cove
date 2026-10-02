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

  it("keeps showing the fallback while the resetKey is unchanged", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const { rerender } = render(
      <ExtensionErrorBoundary fallback={<div>failed</div>} resetKey={1}>
        <Boom throwNow={true} />
      </ExtensionErrorBoundary>,
    );
    rerender(
      <ExtensionErrorBoundary fallback={<div>failed</div>} resetKey={1}>
        <Boom throwNow={false} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.getByText("failed")).toBeInTheDocument();
    expect(screen.queryByText("ok")).not.toBeInTheDocument();
    spy.mockRestore();
  });

  it("retries the children without rendering the fallback again when the resetKey changes", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const fallbackRender = vi.fn(() => <div>failed</div>);
    const { rerender } = render(
      <ExtensionErrorBoundary fallbackRender={fallbackRender} resetKey={1}>
        <Boom throwNow={true} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.getByText("failed")).toBeInTheDocument();
    fallbackRender.mockClear();

    rerender(
      <ExtensionErrorBoundary fallbackRender={fallbackRender} resetKey={2}>
        <Boom throwNow={false} />
      </ExtensionErrorBoundary>,
    );
    expect(screen.getByText("ok")).toBeInTheDocument();
    expect(fallbackRender).not.toHaveBeenCalled();
    spy.mockRestore();
  });
});
