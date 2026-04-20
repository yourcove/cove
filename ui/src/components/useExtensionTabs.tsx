import { useMemo, useState, useEffect } from "react";
import { useExtensions } from "../extensions/ExtensionLoader";
import { ExtensionErrorBoundary } from "./ExtensionErrorBoundary";

interface Tab {
  key: string;
  label: string;
  count?: number;
  order?: number;
}

/**
 * Hook to merge built-in tabs with extension-contributed tabs for a detail page.
 * Returns the merged tab list and a renderer for extension tab content.
 *
 * Pass `entityId` to enable dynamic count fetching for extension tabs that declare a `countEndpoint`.
 */
export function useExtensionTabs(pageType: string, builtInTabs: Tab[], entityId?: number) {
  const { getTabsForPage, resolveComponent } = useExtensions();

  const extTabs = getTabsForPage(pageType);

  // Fetch counts for extension tabs with countEndpoint
  const [extCounts, setExtCounts] = useState<Record<string, number>>({});
  useEffect(() => {
    if (entityId == null) return;
    const toFetch = extTabs.filter((t) => t.countEndpoint);
    if (toFetch.length === 0) return;
    let cancelled = false;
    Promise.all(
      toFetch.map(async (t) => {
        try {
          const url = t.countEndpoint!.replace("{entityId}", String(entityId));
          const res = await fetch(url);
          if (res.ok) {
            const data = await res.json();
            return { key: t.key, count: data.count ?? 0 } as { key: string; count: number };
          }
        } catch {}
        return null;
      })
    ).then((results) => {
      if (cancelled) return;
      const counts: Record<string, number> = {};
      for (const r of results) {
        if (r) counts[r.key] = r.count;
      }
      setExtCounts(counts);
    });
    return () => { cancelled = true; };
  }, [entityId, extTabs]);

  const allTabs = useMemo((): Tab[] => {
    // Assign implicit orders to built-in tabs based on array position
    const withOrder = builtInTabs.map((t, i) => ({
      ...t,
      order: t.order ?? i * 10,
    }));
    const ext = extTabs.map((t) => ({
      key: `ext:${t.key}`,
      label: t.label,
      count: extCounts[t.key],
      order: t.order,
    }));
    return [...withOrder, ...ext].sort((a, b) => (a.order ?? 100) - (b.order ?? 100));
  }, [builtInTabs, extTabs, extCounts]);

  // Extension tab counts for rendering CountCards in stats areas
  const extensionCounts = useMemo(
    () =>
      extTabs
        .filter((t) => t.countEndpoint && extCounts[t.key] != null)
        .map((t) => ({ key: t.key, label: t.label, count: extCounts[t.key], icon: t.icon })),
    [extTabs, extCounts]
  );

  const renderExtensionTab = (activeTab: string, entityId: number, onNavigate?: (r: any) => void) => {
    if (!activeTab.startsWith("ext:")) return null;
    const extTabKey = activeTab.replace("ext:", "");
    const extTab = extTabs.find((t) => t.key === extTabKey);
    if (!extTab) return null;
    const Component = resolveComponent(extTab.componentName);
    if (!Component) {
      return (
        <div className="p-4 text-muted">
          Extension component not found: {extTab.componentName}
        </div>
      );
    }
    return (
      <ExtensionErrorBoundary extensionId={extTab.extensionId}>
        <Component entityId={entityId} onNavigate={onNavigate} />
      </ExtensionErrorBoundary>
    );
  };

  return { allTabs, renderExtensionTab, extensionCounts };
}
