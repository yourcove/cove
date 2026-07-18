import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderExtensionTabIcon } from "../extensions/ExtensionLoader";

afterEach(cleanup);

// A stand-in for an extension's own registered component (e.g. its brand logo).
const FakeLogo = ({ className }: { className?: string }) => (
  <svg data-testid="fake-logo" className={className} />
);
const resolveComponent = (name: string) => (name === "WhisparrLogo" ? FakeLogo : undefined);

describe("renderExtensionTabIcon", () => {
  it("renders the extension's own registered component for a bare name (brand logo)", () => {
    const node = renderExtensionTabIcon("WhisparrLogo", "com.example.ext", resolveComponent);
    const { container } = render(<>{node}</>);
    expect(container.querySelector('[data-testid="fake-logo"]')).not.toBeNull();
  });

  it("renders a host built-in named icon by name (no registration needed)", () => {
    const node = renderExtensionTabIcon("puzzle", "com.example.ext", resolveComponent);
    const { container } = render(<>{node}</>);
    // resolveIcon("puzzle") → the built-in Lucide component, rendered as an <svg>.
    expect(container.querySelector("svg")).not.toBeNull();
    // It is the built-in, not the extension's fake logo.
    expect(container.querySelector('[data-testid="fake-logo"]')).toBeNull();
  });

  it("built-in name wins over a same-named registered component (no shadowing)", () => {
    // An extension that registers a component literally named "puzzle" cannot hijack the built-in.
    const resolvePuzzleComponent = (name: string) => (name === "puzzle" ? FakeLogo : undefined);
    const { container } = render(
      <>{renderExtensionTabIcon("puzzle", "com.example.ext", resolvePuzzleComponent)}</>,
    );
    expect(container.querySelector('[data-testid="fake-logo"]')).toBeNull();
  });

  it("returns undefined for an unknown name so the host default applies, and warns in dev", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    expect(
      renderExtensionTabIcon("DoesNotExist", "com.example.ext", resolveComponent),
    ).toBeUndefined();
    expect(warn).toHaveBeenCalledOnce();
    warn.mockRestore();
  });

  it("returns undefined for an empty or missing icon value (host default applies)", () => {
    expect(renderExtensionTabIcon("", "com.example.ext", resolveComponent)).toBeUndefined();
    expect(renderExtensionTabIcon(undefined, "com.example.ext", resolveComponent)).toBeUndefined();
  });
});
