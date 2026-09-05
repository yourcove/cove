export type ExtensionRuntimeOwner = string | symbol;

export interface ExtensionRuntimeBundleDescriptor {
  extensionId: ExtensionRuntimeOwner;
  version?: string;
  jsBundleUrl: string;
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

function bundleIdentity(descriptor: ExtensionRuntimeBundleDescriptor) {
  return `${descriptor.version ?? ""}\u0000${descriptor.jsBundleUrl}`;
}

function formatOwner(owner: ExtensionRuntimeOwner) {
  return typeof owner === "symbol" ? (owner.description ?? "internal bundle") : owner;
}

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
    byExtensionId.set(extensionId, { extensionId, version, jsBundleUrl });
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
        throw new AggregateError(
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
      const staged = new Map<
        ExtensionRuntimeOwner,
        {
          descriptor: ExtensionRuntimeBundleDescriptor;
          bundle: ResolvedBundle<TComponent, TActionHandler>;
        }
      >();

      // Import and validate every changed bundle before touching the active runtime.
      // This prevents a late import/shape failure from partially applying the set.
      const stagedEntries = await Promise.all(
        [...desired.values()].map(async (descriptor) => {
          const previous = active.get(descriptor.extensionId);
          if (previous && bundleIdentity(previous.descriptor) === bundleIdentity(descriptor)) return null;
          const moduleNamespace = await options.importBundle(descriptor.jsBundleUrl);
          return [
            descriptor.extensionId,
            {
              descriptor,
              bundle: resolveBundle<TComponent, TActionHandler>(moduleNamespace),
            },
          ] as const;
        }),
      );
      for (const entry of stagedEntries) {
        if (entry) staged.set(...entry);
      }
      if (!isCurrent()) return false;

      const affectedIds = new Set<ExtensionRuntimeOwner>();
      for (const [extensionId, record] of active) {
        const next = desired.get(extensionId);
        if (!next || bundleIdentity(record.descriptor) !== bundleIdentity(next)) {
          affectedIds.add(extensionId);
        }
      }
      for (const extensionId of staged.keys()) affectedIds.add(extensionId);
      const previousRecords = new Map(
        [...affectedIds].flatMap((extensionId) => {
          const record = active.get(extensionId);
          return record ? [[extensionId, record] as const] : [];
        }),
      );

      try {
        for (const extensionId of affectedIds) {
          if (!isCurrent()) throw staleReconciliation;
          const previous = active.get(extensionId);
          if (previous) await remove(extensionId, previous);
        }
        for (const { descriptor, bundle } of staged.values()) {
          if (!isCurrent()) throw staleReconciliation;
          await activate(descriptor, bundle);
        }
        if (!isCurrent()) throw staleReconciliation;
      } catch (reconcileError) {
        const rollbackErrors: unknown[] = [];

        for (const extensionId of affectedIds) {
          const current = active.get(extensionId);
          if (!current) continue;
          try {
            await remove(extensionId, current);
          } catch (error) {
            rollbackErrors.push(error);
          }
        }

        for (const [extensionId, previous] of previousRecords) {
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
      return true;
    });

  const dispose = () =>
    enqueue(async () => {
      if (active.size === 0) return;

      const records = [...active];
      active.clear();
      const results = await Promise.allSettled(records.map(([, record]) => record.cleanup()));
      const errors = results
        .filter((result): result is PromiseRejectedResult => result.status === "rejected")
        .map((result) => result.reason);
      if (errors.length === 1) throw errors[0];
      if (errors.length > 1) throw new AggregateError(errors, "Multiple extension unload hooks failed");
    });

  return { reconcile, dispose };
}
