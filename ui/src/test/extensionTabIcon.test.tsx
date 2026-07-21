import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderExtensionIcon } from "../extensions/ExtensionLoader";

afterEach(cleanup);

// A stand-in for an extension's own registered component (e.g. its brand logo).
const FakeLogo = ({ className }: { className?: string }) => (
  <svg data-testid="fake-logo" className={className} />
);
const resolveComponent = (name: string) => (name === "WhisparrLogo" ? FakeLogo : undefined);

describe("renderExtensionIcon", () => {
  it("renders the extension's own registered component for a bare name (brand logo)", () => {
    const node = renderExtensionIcon("WhisparrLogo", "com.example.ext", resolveComponent);
    const { container } = render(<>{node}</>);
    expect(container.querySelector('[data-testid="fake-logo"]')).not.toBeNull();
  });

  it("renders a host built-in named icon by name (no registration needed)", () => {
    const node = renderExtensionIcon("puzzle", "com.example.ext", resolveComponent);
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
      <>{renderExtensionIcon("puzzle", "com.example.ext", resolvePuzzleComponent)}</>,
    );
    expect(container.querySelector('[data-testid="fake-logo"]')).toBeNull();
  });

  it("renders an <img> when the value is an image source (URL / data-URI / path)", () => {
    // The installed-extensions roster ships an asset URL rather than a registered component, and it
    // resolves through the SAME function — the precedence is unified across every surface.
    for (const src of ["https://example.com/logo.png", "data:image/png;base64,AAAA", "/icons/x.svg"]) {
      const { container } = render(
        <>{renderExtensionIcon(src, "com.example.ext", resolveComponent)}</>,
      );
      const img = container.querySelector("img");
      expect(img).not.toBeNull();
      expect(img?.getAttribute("src")).toBe(src);
      cleanup();
    }
  });

  it("returns undefined for an unknown name so the host default applies, and warns in dev", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    expect(renderExtensionIcon("DoesNotExist", "com.example.ext", resolveComponent)).toBeUndefined();
    expect(warn).toHaveBeenCalledOnce();
    warn.mockRestore();
  });

  it("returns the caller-supplied fallback when the icon does not resolve", () => {
    const node = renderExtensionIcon("DoesNotExist", "com.example.ext", resolveComponent, {
      fallback: <span data-testid="fallback" />,
    });
    const { container } = render(<>{node}</>);
    expect(container.querySelector('[data-testid="fallback"]')).not.toBeNull();
  });

  it("returns the fallback (or undefined) for an empty or missing icon value", () => {
    expect(renderExtensionIcon("", "com.example.ext", resolveComponent)).toBeUndefined();
    expect(renderExtensionIcon(undefined, "com.example.ext", resolveComponent)).toBeUndefined();
  });
});
