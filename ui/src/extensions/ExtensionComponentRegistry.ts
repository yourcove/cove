import type { ComponentType } from "react";

export type ExtensionComponent = ComponentType<any>;
export type ExtensionComponentOwner = string | symbol;

/**
 * Component exports keyed by their owning extension as well as their export name.
 * Two extensions can therefore use the same local component name without replacing
 * one another.
 */
export class ExtensionComponentRegistry {
  private readonly componentsByExtension = new Map<ExtensionComponentOwner, Map<string, ExtensionComponent>>();
  private readonly revisionsByExtension = new Map<ExtensionComponentOwner, number>();

  register(extensionId: ExtensionComponentOwner, components: Record<string, ExtensionComponent>): void {
    this.componentsByExtension.set(extensionId, new Map(Object.entries(components)));
    this.revisionsByExtension.set(extensionId, (this.revisionsByExtension.get(extensionId) ?? 0) + 1);
  }

  resolve(extensionId: ExtensionComponentOwner, componentName: string): ExtensionComponent | undefined {
    return this.componentsByExtension.get(extensionId)?.get(componentName);
  }

  getRevision(extensionId: ExtensionComponentOwner): number {
    return this.revisionsByExtension.get(extensionId) ?? 0;
  }

  unregister(extensionId: ExtensionComponentOwner): void {
    this.componentsByExtension.delete(extensionId);
    this.revisionsByExtension.set(extensionId, (this.revisionsByExtension.get(extensionId) ?? 0) + 1);
  }
}
