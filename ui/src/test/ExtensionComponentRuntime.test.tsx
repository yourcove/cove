import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ExtensionComponentOverride } from "../api/types";
import { ExtensionComponentRegistry } from "../extensions/ExtensionComponentRegistry";
import { ExtensionComponentOverrideHost } from "../extensions/ExtensionComponentOverrideHost";

interface OverrideProps {
  label: string;
  renderDefault: () => ReactNode;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("ExtensionComponentRegistry", () => {
  it("keeps identically named components isolated by extension owner", () => {
    const AlphaShared = () => <div>alpha</div>;
    const BetaShared = () => <div>beta</div>;
    const registry = new ExtensionComponentRegistry();

    registry.register("extension.alpha", { Shared: AlphaShared });
    registry.register("extension.beta", { Shared: BetaShared });

    expect(registry.resolve("extension.alpha", "Shared")).toBe(AlphaShared);
    expect(registry.resolve("extension.beta", "Shared")).toBe(BetaShared);
  });
});

describe("ExtensionComponentOverrideHost", () => {
  it("composes matching overrides by descending priority around the native renderer", () => {
    const registry = new ExtensionComponentRegistry();
    const Outer = ({ label, renderDefault }: OverrideProps) => (
      <section data-testid="outer">
        outer:{label}
        {renderDefault()}
      </section>
    );
    const Inner = ({ label, renderDefault }: OverrideProps) => (
      <div data-testid="inner">
        inner:{label}
        {renderDefault()}
      </div>
    );
    registry.register("extension.outer", { MediaOverride: Outer });
    registry.register("extension.inner", { MediaOverride: Inner });

    const contributions: ExtensionComponentOverride[] = [
      {
        targetComponent: "sample.panel",
        extensionId: "extension.inner",
        componentName: "MediaOverride",
        priority: 100,
      },
      {
        targetComponent: "sample.panel",
        extensionId: "extension.outer",
        componentName: "MediaOverride",
        priority: 200,
      },
      {
        targetComponent: "unrelated.target",
        extensionId: "extension.outer",
        componentName: "MediaOverride",
        priority: 300,
      },
    ];

    render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={contributions}
        registry={registry}
        componentProps={{ label: "Tag cover" }}
        renderDefault={() => <span data-testid="native">native:Tag cover</span>}
      />,
    );

    expect(screen.getByTestId("outer")).toContainElement(screen.getByTestId("inner"));
    expect(screen.getByTestId("inner")).toContainElement(screen.getByTestId("native"));
    expect(screen.getByTestId("outer")).toHaveTextContent("outer:Tag cover");
    expect(screen.getByTestId("inner")).toHaveTextContent("inner:Tag cover");
    expect(screen.getByText("native:Tag cover")).toBeInTheDocument();
  });

  it("renders the native component when no usable override is registered", () => {
    const registry = new ExtensionComponentRegistry();
    const contributions: ExtensionComponentOverride[] = [
      {
        targetComponent: "sample.panel",
        extensionId: "extension.missing",
        componentName: "MissingOverride",
        priority: 100,
      },
    ];

    render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={contributions}
        registry={registry}
        componentProps={{ label: "Tag cover" }}
        renderDefault={() => <span>native fallback</span>}
      />,
    );

    expect(screen.getByText("native fallback")).toBeInTheDocument();
  });

  it("does not render lower layers when a replacement does not delegate", () => {
    const registry = new ExtensionComponentRegistry();
    registry.register("extension.replacement", {
      Replacement: () => <div>replacement only</div>,
    });
    const renderDefault = vi.fn(() => <span>native fallback</span>);

    render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={[
          {
            targetComponent: "sample.panel",
            extensionId: "extension.replacement",
            componentName: "Replacement",
            priority: 100,
          },
        ]}
        registry={registry}
        componentProps={{ label: "Sample panel" }}
        renderDefault={renderDefault}
      />,
    );

    expect(screen.getByText("replacement only")).toBeInTheDocument();
    expect(renderDefault).not.toHaveBeenCalled();
  });

  it("uses extension and component names as deterministic priority tie breakers", () => {
    const registry = new ExtensionComponentRegistry();
    const Alpha = ({ renderDefault }: OverrideProps) => (
      <div data-testid="alpha">
        alpha
        {renderDefault()}
      </div>
    );
    const Zeta = ({ renderDefault }: OverrideProps) => (
      <div data-testid="zeta">
        zeta
        {renderDefault()}
      </div>
    );
    registry.register("extension.zeta", { MediaOverride: Zeta });
    registry.register("extension.alpha", { MediaOverride: Alpha });

    render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={[
          {
            targetComponent: "sample.panel",
            extensionId: "extension.zeta",
            componentName: "MediaOverride",
            priority: 100,
          },
          {
            targetComponent: "sample.panel",
            extensionId: "extension.alpha",
            componentName: "MediaOverride",
            priority: 100,
          },
        ]}
        registry={registry}
        componentProps={{ label: "Tag cover" }}
        renderDefault={() => <span data-testid="native">native fallback</span>}
      />,
    );

    expect(screen.getByTestId("alpha")).toContainElement(screen.getByTestId("zeta"));
    expect(screen.getByTestId("zeta")).toContainElement(screen.getByTestId("native"));
  });

  it("continues with the next override when a higher-priority component throws", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const registry = new ExtensionComponentRegistry();
    const Broken = () => {
      throw new Error("broken extension renderer");
    };
    const Working = ({ renderDefault }: OverrideProps) => (
      <div data-testid="working">
        working override
        {renderDefault()}
      </div>
    );
    registry.register("extension.broken", { MediaOverride: Broken });
    registry.register("extension.working", { MediaOverride: Working });

    const contributions: ExtensionComponentOverride[] = [
      {
        targetComponent: "sample.panel",
        extensionId: "extension.broken",
        componentName: "MediaOverride",
        priority: 200,
      },
      {
        targetComponent: "sample.panel",
        extensionId: "extension.working",
        componentName: "MediaOverride",
        priority: 100,
      },
    ];

    render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={contributions}
        registry={registry}
        componentProps={{ label: "Tag cover" }}
        renderDefault={() => <span data-testid="native">native fallback</span>}
      />,
    );

    expect(screen.getByTestId("working")).toContainElement(screen.getByTestId("native"));
    expect(screen.queryByText("broken extension renderer")).not.toBeInTheDocument();
    expect(consoleError).toHaveBeenCalled();
  });

  it("resets a failed override boundary when its owning bundle is replaced", () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const registry = new ExtensionComponentRegistry();
    const Broken = () => {
      throw new Error("broken first version");
    };
    registry.register("extension.upgrade", { Panel: Broken });
    const contributions: ExtensionComponentOverride[] = [
      {
        targetComponent: "sample.panel",
        extensionId: "extension.upgrade",
        componentName: "Panel",
        priority: 100,
      },
    ];
    const props = {
      targetComponent: "sample.panel",
      contributions,
      registry,
      componentProps: { label: "Sample panel" },
      renderDefault: () => <span>native fallback</span>,
    };
    const view = render(<ExtensionComponentOverrideHost {...props} />);
    expect(screen.getByText("native fallback")).toBeInTheDocument();

    registry.unregister("extension.upgrade");
    registry.register("extension.upgrade", { Panel: () => <div>working upgrade</div> });
    view.rerender(<ExtensionComponentOverrideHost {...props} />);

    expect(screen.getByText("working upgrade")).toBeInTheDocument();
    expect(screen.queryByText("native fallback")).not.toBeInTheDocument();
  });

  it("resets a failed override boundary when the host identity changes", () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const registry = new ExtensionComponentRegistry();
    const Conditional = ({ label }: OverrideProps) => {
      if (label === "Broken cover") {
        throw new Error("broken cover");
      }
      return <div>{label}</div>;
    };
    registry.register("extension.panel", { PanelOverride: Conditional });
    const contributions: ExtensionComponentOverride[] = [
      {
        targetComponent: "sample.panel",
        extensionId: "extension.panel",
        componentName: "PanelOverride",
        priority: 100,
      },
    ];
    const renderDefault = () => <span>native fallback</span>;
    const view = render(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={contributions}
        registry={registry}
        componentProps={{ label: "Broken cover" }}
        renderDefault={renderDefault}
        resetKey="item:1:broken"
      />,
    );
    expect(screen.getByText("native fallback")).toBeInTheDocument();

    view.rerender(
      <ExtensionComponentOverrideHost
        targetComponent="sample.panel"
        contributions={contributions}
        registry={registry}
        componentProps={{ label: "Working cover" }}
        renderDefault={renderDefault}
        resetKey="item:2:working"
      />,
    );

    expect(screen.getByText("Working cover")).toBeInTheDocument();
    expect(screen.queryByText("native fallback")).not.toBeInTheDocument();
  });
});
