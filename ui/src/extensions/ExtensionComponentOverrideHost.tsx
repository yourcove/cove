import type { ReactNode } from "react";
import type { ExtensionComponentOverride } from "../api/types";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";
import { ExtensionComponentRegistry, type ExtensionComponent } from "./ExtensionComponentRegistry";

interface ResolvedOverride {
  contribution: ExtensionComponentOverride;
  Component: ExtensionComponent;
  revision: number;
}

interface ExtensionComponentOverrideHostProps<TProps extends object> {
  targetComponent: string;
  contributions: readonly ExtensionComponentOverride[];
  registry: ExtensionComponentRegistry;
  componentProps: TProps;
  renderDefault: () => ReactNode;
  resetKey?: unknown;
}

function compareOverrides(a: ExtensionComponentOverride, b: ExtensionComponentOverride): number {
  return (
    b.priority - a.priority ||
    compareOrdinal(a.extensionId, b.extensionId) ||
    compareOrdinal(a.componentName, b.componentName)
  );
}

function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Composes component overrides as middleware. Each override receives a
 * renderDefault function that advances to the next override, ending at the host
 * renderer. A boundary around each layer makes a failed override take the same
 * path without hiding lower-priority contributions.
 */
export function ExtensionComponentOverrideHost<TProps extends object>({
  targetComponent,
  contributions,
  registry,
  componentProps,
  renderDefault,
  resetKey,
}: ExtensionComponentOverrideHostProps<TProps>) {
  const resolved = contributions
    .filter((contribution) => contribution.targetComponent === targetComponent)
    .sort(compareOverrides)
    .flatMap<ResolvedOverride>((contribution) => {
      const Component = registry.resolve(contribution.extensionId, contribution.componentName);
      return Component ? [{ contribution, Component, revision: registry.getRevision(contribution.extensionId) }] : [];
    });

  const renderLayer = (index: number): ReactNode => {
    const layer = resolved[index];
    if (!layer) {
      return renderDefault();
    }

    const next = () => renderLayer(index + 1);
    const { contribution, Component, revision } = layer;

    return (
      <ExtensionErrorBoundary
        key={`${contribution.extensionId}:${contribution.componentName}:${contribution.priority}:${revision}:${index}`}
        extensionId={contribution.extensionId}
        fallbackRender={next}
        resetKey={resetKey}
      >
        <Component {...componentProps} renderDefault={next} />
      </ExtensionErrorBoundary>
    );
  };

  return <>{renderLayer(0)}</>;
}
