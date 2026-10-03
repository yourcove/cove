import type { QueryClient } from "@tanstack/react-query";
import { dashboards } from "../api/client";
import type { Dashboard, DashboardSummary, DashboardWidget } from "../api/types";

// The home page's dashboard query, kept apart from the page itself so sign-in can start it while the
// page's code is still loading. Both must use these exact keys and this loader, or the page would
// fetch again instead of picking up what sign-in started.

export interface DashboardPageData {
  list: DashboardSummary[];
  dashboard: Dashboard;
  missingRequested: boolean;
  readOnly: boolean;
}

export function dashboardPageQueryKey(principalKey: string, dashboardId?: number) {
  return ["dashboard-page", principalKey, dashboardId ?? "default"] as const;
}

export function savedFilterQueryKey(principalKey: string, savedFilterId: number) {
  return ["saved-filter", principalKey, savedFilterId] as const;
}

/**
 * Loads the dashboard to show with one request, and seeds the saved filters its rows read so each
 * row can query its items straight away. `buildLegacyWidgets` supplies the layout an account without
 * a dashboard is bootstrapped from, and the read-only layout shown to principals without one.
 */
export async function loadDashboardPage(
  queryClient: QueryClient,
  principalKey: string,
  dashboardId: number | undefined,
  buildLegacyWidgets: () => Promise<DashboardWidget[]>,
): Promise<DashboardPageData> {
  try {
    let view = await dashboards.view(dashboardId);
    if (!view.dashboard) {
      // Only a first-time bootstrap needs the locally stored layout.
      await dashboards.bootstrap(await buildLegacyWidgets());
      view = await dashboards.view(dashboardId);
    }
    if (!view.dashboard) throw new Error("No dashboard is available.");
    for (const filter of view.savedFilters) {
      queryClient.setQueryData(savedFilterQueryKey(principalKey, filter.id), filter);
    }
    return {
      list: view.dashboards,
      dashboard: view.dashboard,
      missingRequested: dashboardId != null && !view.requestedFound,
      readOnly: false,
    };
  } catch (error) {
    // Anonymous and share-link principals have no personal storage. Preserve their existing home
    // experience as a local, read-only standard dashboard.
    if (!(error instanceof Error) || !error.message.includes("API Error 401")) throw error;
    const standard: Dashboard = {
      id: 0,
      name: "Standard",
      isDefault: true,
      version: 1,
      createdAt: "",
      updatedAt: "",
      widgets: await buildLegacyWidgets(),
    };
    return { list: [standard], dashboard: standard, missingRequested: dashboardId != null, readOnly: true };
  }
}
