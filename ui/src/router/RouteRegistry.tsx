import { createContext, useContext, useState, useCallback, type ReactNode, type ComponentType } from "react";
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
  /** Named extension slot (e.g. "video-detail-sidebar") */
  slot: string;
  /** Render function invoked with the host-provided context */
  render: (context: TContext) => ReactNode;
  /** Optional ordering. Lower values render first. */
  order?: number;
  /** Changes when the owning bundle is replaced so failed boundaries can recover. */
  resetKey?: unknown;
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
      const idx = prev.findIndex((s) => s.id === entry.id);
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

export function ExtensionSlot<TContext>({
  slot,
  context,
  fallback,
  entryClassName,
}: {
  slot: string;
  context: TContext;
  /** `null` renders nothing on crash (the default is an error box). */
  fallback?: ReactNode;
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
        <ExtensionErrorBoundary key={entry.id} extensionId={entry.id} fallback={fallback} resetKey={entry.resetKey}>
          <div className={entryClassName}>{entry.render(context)}</div>
        </ExtensionErrorBoundary>
      ))}
    </>
  );
}
