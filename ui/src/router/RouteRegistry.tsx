import {
  createContext,
  useCallback,
  useContext,
  useLayoutEffect,
  useMemo,
  useState,
  type ComponentType,
  type ReactNode,
} from "react";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";

export interface NavItem {
  page: string;
  label: string;
  icon?: ComponentType<{ className?: string }>;
  order?: number;
}

export interface RouteEntry {
  /** Page key used in route state */
  page: string;
  /** Component to render for list/collection views (no id) */
  component?: ComponentType<{ onNavigate: (r: any) => void }>;
  /** Component to render for detail views (with id) */
  detailComponent?: ComponentType<{ id: number; onNavigate: (r: any) => void }>;
  navItem?: NavItem;
}

export interface SlotEntry<TContext = any> {
  /** Unique id for this extension contribution */
  id: string;
  /** Extension that owns this contribution. Used to isolate registrations and failures. */
  extensionId?: string;
  /** Named extension slot (e.g. "video-detail-sidebar") */
  slot: string;
  /** Render function invoked with the host-provided context */
  render: (context: TContext) => ReactNode;
  /** Optional ordering. Lower values render first. */
  order?: number;
  /** Changes when the owning bundle is replaced so failed boundaries can recover. */
  resetKey?: unknown;
}

export interface ExtensionSlotEntryContext<TContext extends object> {
  /** Stable fields merged over the host's dynamic context for this registration. */
  context: Partial<TContext>;
  /** Optional registration lifecycle. Its returned cleanup runs whenever this entry leaves the slot. */
  mount?: () => void | (() => void);
}

interface RouteRegistryContextValue {
  routes: RouteEntry[];
  slots: SlotEntry[];
  register: (entry: RouteEntry) => () => void;
  registerSlot: (entry: SlotEntry) => () => void;
  unregister: (page: string) => void;
  unregisterSlot: (id: string) => void;
}

const RouteRegistryContext = createContext<RouteRegistryContextValue | null>(null);

function slotRegistrationKey(entry: Pick<SlotEntry, "extensionId" | "id">) {
  return JSON.stringify([entry.extensionId ?? null, entry.id]);
}

export function RouteRegistryProvider({ children }: { children: ReactNode }) {
  const [routes, setRoutes] = useState<RouteEntry[]>([]);
  const [slots, setSlots] = useState<SlotEntry[]>([]);

  const register = useCallback((entry: RouteEntry) => {
    setRoutes((prev) => {
      // Replace if same page key already registered
      const idx = prev.findIndex((r) => r.page === entry.page);
      if (idx >= 0) {
        const next = [...prev];
        next[idx] = entry;
        return next;
      }
      return [...prev, entry];
    });
    return () => setRoutes((prev) => prev.filter((route) => route !== entry));
  }, []);

  const registerSlot = useCallback((entry: SlotEntry) => {
    setSlots((prev) => {
      const registrationKey = slotRegistrationKey(entry);
      const idx = prev.findIndex((slot) => slotRegistrationKey(slot) === registrationKey);
      if (idx >= 0) {
        const next = [...prev];
        next[idx] = entry;
        return next;
      }
      return [...prev, entry];
    });
    return () => setSlots((prev) => prev.filter((slot) => slot !== entry));
  }, []);

  const unregister = useCallback((page: string) => {
    setRoutes((prev) => prev.filter((route) => route.page !== page));
  }, []);

  const unregisterSlot = useCallback((id: string) => {
    setSlots((prev) => prev.filter((slot) => slot.id !== id));
  }, []);

  return (
    <RouteRegistryContext.Provider value={{ routes, slots, register, registerSlot, unregister, unregisterSlot }}>
      {children}
    </RouteRegistryContext.Provider>
  );
}

export function useRouteRegistry() {
  const ctx = useContext(RouteRegistryContext);
  if (!ctx) throw new Error("useRouteRegistry must be used inside RouteRegistryProvider");
  return ctx;
}

export function useHasExtensionSlot(slot: string): boolean {
  const ctx = useContext(RouteRegistryContext);
  return ctx?.slots.some((s) => s.slot === slot) ?? false;
}

function ExtensionSlotEntry<TContext extends object>({
  entry,
  context,
  createEntryContext,
  wrapperClassName,
}: {
  entry: SlotEntry<TContext>;
  context: TContext;
  createEntryContext?: (entry: SlotEntry<TContext>) => ExtensionSlotEntryContext<TContext>;
  wrapperClassName?: string;
}) {
  const entryContext = useMemo(
    () => createEntryContext?.(entry),
    [createEntryContext, entry],
  );

  useLayoutEffect(() => entryContext?.mount?.(), [entryContext]);

  const resolvedContext = useMemo(
    () => entryContext ? { ...context, ...entryContext.context } : context,
    [context, entryContext],
  );

  return <div className={wrapperClassName}>{entry.render(resolvedContext)}</div>;
}

function ExtensionSlotContribution<TContext extends object>({
  entry,
  context,
  contextResetKey,
  createEntryContext,
  wrapperClassName,
  fallback,
}: {
  entry: SlotEntry<TContext>;
  context: TContext;
  contextResetKey?: unknown;
  createEntryContext?: (entry: SlotEntry<TContext>) => ExtensionSlotEntryContext<TContext>;
  wrapperClassName?: string;
  fallback?: ReactNode;
}) {
  // The host reset identity deliberately excludes the live context object. Player
  // time updates can replace that object many times per second and must not keep
  // retrying a failed contribution. Hosts opt into recovery only at meaningful
  // identity boundaries such as a new video or compilation item.
  const boundaryResetKey = useMemo(
    () => ({ entry: entry.resetKey ?? entry, context: contextResetKey }),
    [contextResetKey, entry, entry.resetKey],
  );

  return (
    <ExtensionErrorBoundary
      extensionId={entry.extensionId ?? entry.id}
      resetKey={boundaryResetKey}
      fallback={fallback}
    >
      <ExtensionSlotEntry
        entry={entry}
        context={context}
        createEntryContext={createEntryContext}
        wrapperClassName={wrapperClassName}
      />
    </ExtensionErrorBoundary>
  );
}

export function ExtensionSlot<TContext extends object>({
  slot,
  context,
  contextResetKey,
  createEntryContext,
  wrapperClassName,
  fallback,
  entryClassName,
}: {
  slot: string;
  context: TContext;
  /** Resets failed contributions when the host context identity meaningfully changes. */
  contextResetKey?: unknown;
  createEntryContext?: (entry: SlotEntry<TContext>) => ExtensionSlotEntryContext<TContext>;
  wrapperClassName?: string;
  /** `null` renders nothing on crash (the default is an error box). */
  fallback?: ReactNode;
  /** Backward-compatible alias for wrapperClassName. */
  entryClassName?: string;
}) {
  const { slots } = useRouteRegistry();
  const matching = slots
    .filter((s) => s.slot === slot)
    .sort((a, b) => (a.order ?? 100) - (b.order ?? 100));

  if (matching.length === 0) return null;

  return (
    <>
      {matching.map((entry) => (
        <ExtensionSlotContribution
          key={slotRegistrationKey(entry)}
          entry={entry}
          context={context}
          contextResetKey={contextResetKey}
          createEntryContext={createEntryContext}
          wrapperClassName={wrapperClassName ?? entryClassName}
          fallback={fallback}
        />
      ))}
    </>
  );
}
