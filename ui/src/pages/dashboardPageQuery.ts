import type { QueryClient } from "@tanstack/react-query";
import { dashboards } from "../api/client";
import type { Dashboard, DashboardSummary, DashboardWidget } from "../api/types";
import type { AuthUser } from "../auth/authStore";

// The home page's dashboard query, kept apart from the page itself so sign-in can start it while the
// page's code is still loading. Both must use these exact keys and this loader, or the page would
// fetch again instead of picking up what sign-in started.

export interface DashboardPageData {
  list: DashboardSummary[];
  dashboard: Dashboard;
  missingRequested: boolean;
  readOnly: boolean;
}

/** Separates cached dashboards per principal, so a different sign-in never sees the last one's. */
export function dashboardPrincipalKey(user: Pick<AuthUser, "id" | "kind"> | null | undefined) {
  return user ? `${user.kind}:${user.id}` : "anonymous";
}

export function dashboardPageQueryKey(principalKey: string, dashboardId?: number) {
  return ["dashboard-page", principalKey, dashboardId ?? "default"] as const;
}

export function savedFilterQueryKey(principalKey: string, savedFilterId: number) {
  return ["saved-filter", principalKey, savedFilterId] as const;
}

/** A prefetch found no dashboard and left creating one to the page that shows it. */
export class DashboardBootstrapDeferred extends Error {
  constructor() {
    super("The dashboard is created when the page that shows it renders.");
    this.name = "DashboardBootstrapDeferred";
  }
}

/**
 * Loads the dashboard to show with one request, and seeds the saved filters its rows read so each
 * row can query its items straight away. `buildLegacyWidgets` supplies the layout an account without
 * a dashboard is bootstrapped from, and the read-only layout shown to principals without one.
 * `mayBootstrap` is asked before an account without a dashboard gets one persisted; when it declines,
 * the load fails with {@link DashboardBootstrapDeferred} instead.
 */
export async function loadDashboardPage(
  queryClient: QueryClient,
  principalKey: string,
  dashboardId: number | undefined,
  buildLegacyWidgets: () => Promise<DashboardWidget[]>,
  mayBootstrap: () => boolean = () => true,
): Promise<DashboardPageData> {
  try {
    let view = await dashboards.view(dashboardId);
    if (!view.dashboard) {
      if (!mayBootstrap()) throw new DashboardBootstrapDeferred();
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

/**
 * Starts loading a dashboard page before it renders: the page's code and, sharing the page's query
 * key and loader, its data. The page then picks up the request in flight instead of starting its own.
 *
 * The prefetch only reads. It runs before the extension runtime knows whether the page will render at
 * all, so an account without a dashboard gets one only once the page is observing the query; if no
 * page is yet, the query fails with {@link DashboardBootstrapDeferred}, which a page mounting later
 * refetches with its own loader.
 */
export function prefetchDashboardPage(queryClient: QueryClient, principalKey: string, dashboardId?: number) {
  const page = import("./HomePage");
  // Only the bootstrap path awaits the module here; the page's own lazy import reports a load failure.
  page.catch(() => undefined);
  const queryKey = dashboardPageQueryKey(principalKey, dashboardId);
  void queryClient.prefetchQuery({
    queryKey,
    queryFn: () =>
      loadDashboardPage(
        queryClient,
        principalKey,
        dashboardId,
        async () => (await page).buildLegacyDashboardWidgets(),
        () => (queryClient.getQueryCache().find({ queryKey, exact: true })?.getObserversCount() ?? 0) > 0,
      ),
    // A deferred bootstrap is not a failure to retry; the page refetches it when it mounts.
    retry: (failureCount, error) => !(error instanceof DashboardBootstrapDeferred) && failureCount < 1,
  });
}
