export type ExtensionRuntimeOwner = string | symbol;

export interface ExtensionRuntimeBundleDescriptor {
  extensionId: ExtensionRuntimeOwner;
  version?: string;
  jsBundleUrl: string;
  dependencies?: string[];
}

export interface ExtensionRuntimeFailure {
  extensionId: ExtensionRuntimeOwner;
  phase: "load" | "cleanup" | "dependency";
  message: string;
}

export interface ExtensionRuntimeRegistration<TComponent = unknown, TActionHandler = unknown> {
  components: Record<string, TComponent>;
  actionHandlers: Record<string, TActionHandler>;
}

export interface ExtensionRuntimeRegistrationAdapter<TComponent = unknown, TActionHandler = unknown> {
  register(
    extensionId: ExtensionRuntimeOwner,
    registration: ExtensionRuntimeRegistration<TComponent, TActionHandler>,
  ): () => void;
}

export interface ExtensionRuntimeReconcilerOptions<TComponent = unknown, TActionHandler = unknown> {
  importBundle: (url: string) => Promise<unknown>;
  registrations: ExtensionRuntimeRegistrationAdapter<TComponent, TActionHandler>;
}

export interface ExtensionRuntimeReconciler {
  reconcile(descriptors: ExtensionRuntimeBundleDescriptor[], options?: { isCurrent?: () => boolean }): Promise<boolean>;
  getFailures(): ExtensionRuntimeFailure[];
  dispose(): Promise<void>;
}

interface ResolvedBundle<TComponent, TActionHandler> {
  registration: ExtensionRuntimeRegistration<TComponent, TActionHandler>;
  onLoad?: () => void | Promise<void>;
  onUnload?: () => void | Promise<void>;
}

interface ActiveExtension<TComponent, TActionHandler> {
  descriptor: ExtensionRuntimeBundleDescriptor;
  bundle: ResolvedBundle<TComponent, TActionHandler>;
  cleanup: () => Promise<void>;
}

function unloadOrder<TComponent, TActionHandler>(
  records: Map<ExtensionRuntimeOwner, ActiveExtension<TComponent, TActionHandler>>,
) {
  const byNormalizedId = new Map(
    [...records.keys()].filter((id): id is string => typeof id === "string").map((id) => [id.toLowerCase(), id]),
  );
  const visited = new Set<ExtensionRuntimeOwner>();
  const ordered: ExtensionRuntimeOwner[] = [];
  const visit = (id: ExtensionRuntimeOwner) => {
    if (visited.has(id)) return;
    visited.add(id);
    for (const dependency of records.get(id)?.descriptor.dependencies ?? []) {
      const owner = byNormalizedId.get(dependency.toLowerCase());
      if (owner !== undefined) visit(owner);
    }
    ordered.push(id);
  };
  for (const id of records.keys()) visit(id);
  return ordered.reverse();
}

function bundleIdentity(descriptor: ExtensionRuntimeBundleDescriptor) {
  return `${descriptor.version ?? ""}\u0000${descriptor.jsBundleUrl}`;
}

function formatOwner(owner: ExtensionRuntimeOwner) {
  return typeof owner === "symbol" ? (owner.description ?? "internal bundle") : owner;
}

class ExtensionCleanupError extends AggregateError {}

function requireObject(value: unknown, label: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError(`${label} must be an object`);
  }
  return value as Record<string, unknown>;
}

function resolveExportMap<TExport>(value: unknown, label: string): Record<string, TExport> {
  if (value === undefined) return {};

  const exports = requireObject(value, label);
  for (const [name, exported] of Object.entries(exports)) {
    if (!name.trim()) {
      throw new TypeError(`${label} contains an empty export name`);
    }
    if (typeof exported !== "function") {
      throw new TypeError(`${label}.${name} must be a function`);
    }
  }

  return exports as Record<string, TExport>;
}

function resolveLifecycleHook(value: unknown, label: string): (() => void | Promise<void>) | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== "function") {
    throw new TypeError(`${label} must be a function`);
  }
  return value as () => void | Promise<void>;
}

function resolveBundle<TComponent, TActionHandler>(
  moduleNamespace: unknown,
): ResolvedBundle<TComponent, TActionHandler> {
  const namespace = requireObject(moduleNamespace, "Extension bundle module");
  const definition = requireObject(
    Object.prototype.hasOwnProperty.call(namespace, "default") ? namespace.default : namespace,
    "Extension bundle default export",
  );

  const handlerExports = definition.actionHandlers === undefined ? definition.handlers : definition.actionHandlers;

  return {
    registration: {
      components: resolveExportMap<TComponent>(definition.components, "Extension components"),
      actionHandlers: resolveExportMap<TActionHandler>(handlerExports, "Extension action handlers"),
    },
    onLoad: resolveLifecycleHook(definition.onLoad, "Extension onLoad"),
    onUnload: resolveLifecycleHook(definition.onUnload, "Extension onUnload"),
  };
}

function validateDescriptors(descriptors: ExtensionRuntimeBundleDescriptor[]) {
  const byExtensionId = new Map<ExtensionRuntimeOwner, ExtensionRuntimeBundleDescriptor>();

  for (const descriptor of descriptors) {
    const extensionId =
      typeof descriptor.extensionId === "string" ? descriptor.extensionId.trim() : descriptor.extensionId;
    const version = descriptor.version?.trim();
    const jsBundleUrl = descriptor.jsBundleUrl.trim();
    if (typeof extensionId === "string" && !extensionId) {
      throw new TypeError("Extension bundle descriptor requires an extensionId");
    }
    const owner = formatOwner(extensionId);
    if (!jsBundleUrl) throw new TypeError(`Extension '${owner}' requires a jsBundleUrl`);
    if (byExtensionId.has(extensionId)) {
      throw new TypeError(`Duplicate extension bundle descriptor for '${owner}'`);
    }
    byExtensionId.set(extensionId, { ...descriptor, extensionId, version, jsBundleUrl });
  }

  return byExtensionId;
}

function createIdempotentCleanup(onUnload: (() => void | Promise<void>) | undefined, unregister: () => void) {
  let cleanupPromise: Promise<void> | undefined;

  return () => {
    if (cleanupPromise) return cleanupPromise;

    cleanupPromise = (async () => {
      try {
        await onUnload?.();
      } finally {
        unregister();
      }
    })();
    return cleanupPromise;
  };
}

export function createExtensionRuntimeReconciler<TComponent = unknown, TActionHandler = unknown>(
  options: ExtensionRuntimeReconcilerOptions<TComponent, TActionHandler>,
): ExtensionRuntimeReconciler {
  const active = new Map<ExtensionRuntimeOwner, ActiveExtension<TComponent, TActionHandler>>();
  let failures: ExtensionRuntimeFailure[] = [];
  const staleReconciliation = Symbol("stale extension runtime reconciliation");
  let pending: Promise<unknown> = Promise.resolve();

  const enqueue = <T>(operation: () => Promise<T>) => {
    const result = pending.then(operation);
    pending = result.catch(() => undefined);
    return result;
  };

  const remove = async (extensionId: ExtensionRuntimeOwner, record: ActiveExtension<TComponent, TActionHandler>) => {
    if (active.get(extensionId) === record) active.delete(extensionId);
    await record.cleanup();
  };

  const activate = async (
    descriptor: ExtensionRuntimeBundleDescriptor,
    bundle: ResolvedBundle<TComponent, TActionHandler>,
  ) => {
    const unregister = options.registrations.register(descriptor.extensionId, bundle.registration);
    if (typeof unregister !== "function") {
      throw new TypeError("Extension registration adapter must return an unregister function");
    }

    const cleanup = createIdempotentCleanup(bundle.onUnload, unregister);
    const next: ActiveExtension<TComponent, TActionHandler> = { descriptor, bundle, cleanup };

    try {
      await bundle.onLoad?.();
      active.set(descriptor.extensionId, next);
    } catch (onLoadError) {
      try {
        await cleanup();
      } catch (cleanupError) {
        throw new ExtensionCleanupError(
          [onLoadError, cleanupError],
          `Extension '${formatOwner(descriptor.extensionId)}' onLoad failed and rollback cleanup also failed`,
        );
      }
      throw onLoadError;
    }
    return next;
  };

  const reconcile = (
    descriptors: ExtensionRuntimeBundleDescriptor[],
    reconcileOptions?: { isCurrent?: () => boolean },
  ) =>
    enqueue(async () => {
      const isCurrent = reconcileOptions?.isCurrent ?? (() => true);
      const desired = validateDescriptors(descriptors);
      const nextFailures = new Map<ExtensionRuntimeOwner, ExtensionRuntimeFailure>();
      const fail = (extensionId: ExtensionRuntimeOwner, phase: ExtensionRuntimeFailure["phase"], error: unknown) => {
        const message =
          error instanceof AggregateError
            ? error.errors.map((cause) => (cause instanceof Error ? cause.message : String(cause))).join("; ")
            : error instanceof Error
              ? error.message
              : String(error);
        nextFailures.set(extensionId, { extensionId, phase, message });
      };
      const staged = new Map<
        ExtensionRuntimeOwner,
        {
          descriptor: ExtensionRuntimeBundleDescriptor;
          bundle: ResolvedBundle<TComponent, TActionHandler>;
        }
      >();

      // Stage imports independently. One rejected bundle must not discard healthy imports.
      const stagedEntries = await Promise.all(
        [...desired.values()].map(async (descriptor) => {
          const previous = active.get(descriptor.extensionId);
          if (previous && bundleIdentity(previous.descriptor) === bundleIdentity(descriptor)) return null;
          try {
            const moduleNamespace = await options.importBundle(descriptor.jsBundleUrl);
            return [
              descriptor.extensionId,
              {
                descriptor,
                bundle: resolveBundle<TComponent, TActionHandler>(moduleNamespace),
              },
            ] as const;
          } catch (error) {
            fail(descriptor.extensionId, "load", error);
            return null;
          }
        }),
      );
      for (const entry of stagedEntries) {
        if (entry) staged.set(...entry);
      }
      if (!isCurrent()) return false;

      // Only dependencies with browser bundles participate here. The server already
      // validates installed/backend dependencies. Order UI initialization by dependency.
      const orderedIds: ExtensionRuntimeOwner[] = [];
      const visiting = new Set<ExtensionRuntimeOwner>();
      const visited = new Set<ExtensionRuntimeOwner>();
      const byNormalizedId = new Map(
        [...desired.keys()].filter((id): id is string => typeof id === "string").map((id) => [id.toLowerCase(), id]),
      );
      const dependenciesOf = (id: ExtensionRuntimeOwner) =>
        (desired.get(id)?.dependencies ?? []).flatMap((dep) => {
          const owner = byNormalizedId.get(dep.toLowerCase());
          return owner === undefined ? [] : [owner];
        });
      const visit = (id: ExtensionRuntimeOwner) => {
        if (visited.has(id)) return;
        if (visiting.has(id)) {
          fail(id, "dependency", "Extension UI dependencies contain a cycle.");
          return;
        }
        visiting.add(id);
        for (const dependency of dependenciesOf(id)) visit(dependency);
        visiting.delete(id);
        visited.add(id);
        orderedIds.push(id);
      };
      for (const id of desired.keys()) visit(id);
      for (const id of orderedIds) {
        const blocked = dependenciesOf(id).find((dep) => nextFailures.has(dep));
        if (blocked !== undefined) fail(id, "dependency", `Required extension '${blocked}' could not load its UI.`);
      }

      const affectedIds = new Set<ExtensionRuntimeOwner>();
      for (const [extensionId, record] of active) {
        const next = desired.get(extensionId);
        if (!next || nextFailures.has(extensionId) || bundleIdentity(record.descriptor) !== bundleIdentity(next)) {
          affectedIds.add(extensionId);
        }
      }
      for (const extensionId of staged.keys()) affectedIds.add(extensionId);
      // Dependents must release references while the old dependency is still
      // available, then initialize against the replacement (even if unchanged).
      for (const extensionId of orderedIds) {
        if (!dependenciesOf(extensionId).some((dependency) => affectedIds.has(dependency))) continue;
        affectedIds.add(extensionId);
        const previous = active.get(extensionId);
        if (previous && !staged.has(extensionId) && !nextFailures.has(extensionId)) {
          staged.set(extensionId, { descriptor: desired.get(extensionId)!, bundle: previous.bundle });
        }
      }
      const previousRecords = new Map(
        [...affectedIds].flatMap((extensionId) => {
          const record = active.get(extensionId);
          return record ? [[extensionId, record] as const] : [];
        }),
      );

      try {
        for (const extensionId of unloadOrder(previousRecords)) {
          if (!isCurrent()) throw staleReconciliation;
          const previous = active.get(extensionId);
          if (previous) {
            try {
              await remove(extensionId, previous);
            } catch (error) {
              fail(extensionId, "cleanup", error);
            }
          }
        }
        for (const extensionId of orderedIds) {
          if (!isCurrent()) throw staleReconciliation;
          const blocked = dependenciesOf(extensionId).find((dep) => nextFailures.has(dep));
          if (blocked !== undefined)
            fail(extensionId, "dependency", `Required extension '${blocked}' could not load its UI.`);
          if (nextFailures.has(extensionId)) {
            const previous = active.get(extensionId);
            if (previous) {
              // An unchanged dependent may need withdrawing after its dependency's onLoad fails.
              previousRecords.set(extensionId, previous);
              affectedIds.add(extensionId);
              try {
                await remove(extensionId, previous);
              } catch (error) {
                fail(extensionId, "cleanup", error);
              }
            }
            continue;
          }
          const entry = staged.get(extensionId);
          if (entry) {
            try {
              await activate(entry.descriptor, entry.bundle);
            } catch (error) {
              fail(extensionId, error instanceof ExtensionCleanupError ? "cleanup" : "load", error);
            }
          }
        }
        if (!isCurrent()) throw staleReconciliation;
      } catch (reconcileError) {
        const rollbackErrors: unknown[] = [];

        const rollbackRecords = new Map([...active].filter(([id]) => affectedIds.has(id)));
        for (const extensionId of unloadOrder(rollbackRecords)) {
          const current = active.get(extensionId);
          if (!current) continue;
          try {
            await remove(extensionId, current);
          } catch (error) {
            rollbackErrors.push(error);
          }
        }

        for (const extensionId of unloadOrder(previousRecords).reverse()) {
          const previous = previousRecords.get(extensionId)!;
          try {
            await activate(previous.descriptor, previous.bundle);
          } catch (error) {
            rollbackErrors.push(error);
            active.delete(extensionId);
          }
        }

        if (rollbackErrors.length > 0) {
          throw new AggregateError(
            [reconcileError, ...rollbackErrors],
            "Extension runtime reconciliation failed and rollback was incomplete",
          );
        }
        if (reconcileError === staleReconciliation) return false;
        throw reconcileError;
      }
      failures = [...nextFailures.values()];
      return true;
    });

  const dispose = () =>
    enqueue(async () => {
      if (active.size === 0) return;

      const records = new Map(active);
      active.clear();
      const errors: unknown[] = [];
      for (const id of unloadOrder(records)) {
        try {
          await records.get(id)!.cleanup();
        } catch (error) {
          errors.push(error);
        }
      }
      if (errors.length === 1) throw errors[0];
      if (errors.length > 1) throw new AggregateError(errors, "Multiple extension unload hooks failed");
    });

  return { reconcile, dispose, getFailures: () => [...failures] };
}
