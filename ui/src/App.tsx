import { useState, useEffect, useCallback, useMemo, lazy, Suspense } from "react";
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { Navbar } from "./components/Navbar";
import { KeyboardShortcutsDialog } from "./components/KeyboardShortcutsDialog";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { RouteRegistryProvider, useRouteRegistry } from "./router/RouteRegistry";
import { AppConfigProvider, useAppConfig } from "./state/AppConfigContext";
import { ExtensionLoaderProvider, useExtensions } from "./extensions/ExtensionLoader";
import { SceneQueueProvider } from "./state/SceneQueueContext";
import { SetupWizardPage } from "./pages/SetupWizardPage";
import { useKeySequence } from "./hooks/useKeySequence";
import { LOCATION_CHANGE_EVENT, Route, buildCurrentUrl, buildRoutePath, navigateToUrl, parseCurrentRoute, parseLegacyHashRoute, syncRouteHistory } from "./router/location";

// Lazy-loaded page components for code splitting
const ScenesPage = lazy(() => import("./pages/ScenesPage").then(m => ({ default: m.ScenesPage })));
const PerformersPage = lazy(() => import("./pages/PerformersPage").then(m => ({ default: m.PerformersPage })));
const StudiosPage = lazy(() => import("./pages/StudiosPage").then(m => ({ default: m.StudiosPage })));
const TagsPage = lazy(() => import("./pages/TagsPage").then(m => ({ default: m.TagsPage })));
const GalleriesPage = lazy(() => import("./pages/GalleriesPage").then(m => ({ default: m.GalleriesPage })));
const GroupsPage = lazy(() => import("./pages/GroupsPage").then(m => ({ default: m.GroupsPage })));
const ImagesPage = lazy(() => import("./pages/ImagesPage").then(m => ({ default: m.ImagesPage })));
const SettingsPage = lazy(() => import("./pages/SettingsPage").then(m => ({ default: m.SettingsPage })));
const StatsPage = lazy(() => import("./pages/StatsPage").then(m => ({ default: m.StatsPage })));
const SceneDetailPage = lazy(() => import("./pages/SceneDetailPage").then(m => ({ default: m.SceneDetailPage })));
const PerformerDetailPage = lazy(() => import("./pages/PerformerDetailPage").then(m => ({ default: m.PerformerDetailPage })));
const StudioDetailPage = lazy(() => import("./pages/StudioDetailPage").then(m => ({ default: m.StudioDetailPage })));
const TagDetailPage = lazy(() => import("./pages/TagDetailPage").then(m => ({ default: m.TagDetailPage })));
const GalleryDetailPage = lazy(() => import("./pages/GalleryDetailPage").then(m => ({ default: m.GalleryDetailPage })));
const GroupDetailPage = lazy(() => import("./pages/GroupDetailPage").then(m => ({ default: m.GroupDetailPage })));
const ImageDetailPage = lazy(() => import("./pages/ImageDetailPage").then(m => ({ default: m.ImageDetailPage })));
const LogsPage = lazy(() => import("./pages/LogsPage").then(m => ({ default: m.LogsPage })));
const SceneMarkersPage = lazy(() => import("./pages/SceneMarkersPage").then(m => ({ default: m.SceneMarkersPage })));

const SceneFilenameParserPage = lazy(() => import("./pages/SceneFilenameParserPage").then(m => ({ default: m.SceneFilenameParserPage })));
const HomePage = lazy(() => import("./pages/HomePage").then(m => ({ default: m.HomePage })));

export default function App() {
  const [route, setRoute] = useState<Route>(() => {
    const legacyRoute = parseLegacyHashRoute(window.location.hash);
    return legacyRoute ?? parseCurrentRoute();
  });

  useEffect(() => {
    const legacyRoute = parseLegacyHashRoute(window.location.hash);
    if (legacyRoute) {
      navigateToUrl(buildCurrentUrl(buildRoutePath(legacyRoute), window.location.search), { replace: true });
      setRoute(legacyRoute);
    }
    // Redirect /home to / (canonical home URL)
    if (window.location.pathname === "/home") {
      navigateToUrl(buildCurrentUrl("/", window.location.search), { replace: true });
    }

    syncRouteHistory("push");
  }, []);

  useEffect(() => {
    const handleLocationChange = (event: Event) => {
      syncRouteHistory(event.type === "popstate" ? "history" : "push");
      setRoute(parseCurrentRoute());
    };
    window.addEventListener("popstate", handleLocationChange);
    window.addEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    return () => {
      window.removeEventListener("popstate", handleLocationChange);
      window.removeEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    };
  }, []);

  const navigate = useCallback((r: Route) => {
    const currentPath = window.location.pathname;
    const nextPath = buildRoutePath(r);
    if (currentPath === nextPath) {
      window.dispatchEvent(new CustomEvent("cove-page-reset", { detail: r.page }));
    } else {
      navigateToUrl(nextPath);
      setRoute(r);
    }
  }, []);

  // Keyboard shortcut: "/" focuses search
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "/" && !["INPUT", "TEXTAREA", "SELECT"].includes((e.target as HTMLElement)?.tagName)) {
        e.preventDefault();
        const searchInput = document.querySelector<HTMLInputElement>("input[placeholder='Filter...']")
          ?? document.querySelector<HTMLInputElement>("input[placeholder='Search all...']");
        searchInput?.focus();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  const [showShortcuts, setShowShortcuts] = useState(false);

  // Global keyboard navigation shortcuts
  const globalBindings = useMemo(() => [
    { keys: "g h", action: () => navigate({ page: "home" }) },
    { keys: "g s", action: () => navigate({ page: "scenes" }) },
    { keys: "g i", action: () => navigate({ page: "images" }) },
    { keys: "g v", action: () => navigate({ page: "groups" }) },
    { keys: "g k", action: () => navigate({ page: "markers" }) },
    { keys: "g l", action: () => navigate({ page: "galleries" }) },
    { keys: "g p", action: () => navigate({ page: "performers" }) },
    { keys: "g u", action: () => navigate({ page: "studios" }) },
    { keys: "g t", action: () => navigate({ page: "tags" }) },
    { keys: "g z", action: () => navigate({ page: "settings" }) },
    { keys: "g d", action: () => navigate({ page: "stats" }) },
    { keys: "?", action: () => setShowShortcuts(true) },
  ], [navigate]);

  useKeySequence(globalBindings);

  return (
    <RouteRegistryProvider>
      <AppConfigProvider>
        <ExtensionLoaderProvider>
          <SceneQueueProvider>
            <AppShell route={route} navigate={navigate} />
            <KeyboardShortcutsDialog open={showShortcuts} onClose={() => setShowShortcuts(false)} />
          </SceneQueueProvider>
        </ExtensionLoaderProvider>
      </AppConfigProvider>
    </RouteRegistryProvider>
  );
}

function AppShell({ route, navigate }: { route: Route; navigate: (r: Route) => void }) {
  const { config, configLoading, status, statusLoading } = useAppConfig();
  const [setupDismissed, setSetupDismissed] = useState(() => sessionStorage.getItem("cove-setup-dismissed") === "true");

  // Show setup wizard if config has no library paths and user hasn't dismissed it
  const needsSetup = config && config.covePaths.filter(p => p.path.trim() !== "").length === 0 && !setupDismissed;

  if (configLoading || statusLoading) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }

  // Migration gate: block the app until migrations are applied (they run on next restart)
  if (status?.migrationRequired) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center">
        <div className="max-w-md text-center space-y-4 p-8">
          <div className="text-4xl">⚙️</div>
          <h1 className="text-xl font-semibold text-foreground">Database Update Required</h1>
          <p className="text-sm text-muted-foreground">
            Cove needs to update the database schema. This will happen automatically — please restart the server.
          </p>
          {status.pendingMigrations && (
            <div className="text-xs text-muted-foreground bg-surface rounded p-3 text-left">
              <div className="font-medium mb-1">Pending migrations:</div>
              {status.pendingMigrations.map(m => (
                <div key={m} className="font-mono">{m}</div>
              ))}
            </div>
          )}
          <p className="text-xs text-muted-foreground">
            A backup will be created automatically before applying changes.
          </p>
        </div>
      </div>
    );
  }

  if (needsSetup && config) {
    return (
      <SetupWizardPage
        config={config}
        onComplete={() => {
          setSetupDismissed(true);
          sessionStorage.setItem("cove-setup-dismissed", "true");
        }}
      />
    );
  }

  return (
    <div className="min-h-screen bg-background text-foreground">
      <Navbar currentPage={route.page} navigate={navigate} />
      <main className="w-full px-3 sm:px-4 md:px-6 py-3 sm:py-5">
        <ErrorBoundary>
          <Suspense fallback={<div className="flex items-center justify-center h-64"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent"></div></div>}>
            <AppRoutes route={route} navigate={navigate} />
          </Suspense>
        </ErrorBoundary>
      </main>
    </div>
  );
}

function AppRoutes({ route, navigate }: { route: Route; navigate: (r: Route) => void }) {
  const { routes } = useRouteRegistry();
  const { getPageOverride, resolveComponent, manifest } = useExtensions();

  // 1. Check for page overrides (extension replaces a built-in page)
  const override = getPageOverride(route.page);
  if (override) {
    const Component = resolveComponent(override.componentName);
    if (Component) {
      return <Component onNavigate={navigate} />;
    }
  }

  // 2. Check extension-contributed pages (new pages via UIPageDefinition)
  const extPage = manifest?.pages.find((p) => p.route === route.page);
  if (extPage?.componentName) {
    const Component = resolveComponent(extPage.componentName);
    if (Component) {
      // Pass id if this is a detail page route
      const props: Record<string, unknown> = { onNavigate: navigate };
      if ("id" in route && route.id !== undefined) {
        props.id = route.id;
      }
      return <Component {...props} />;
    }
  }

  // 3. Check route registry (legacy extension routes)
  const extRoute = routes.find((r) => r.page === route.page);
  if (extRoute?.component) {
    const Comp = extRoute.component;
    return <Comp onNavigate={navigate} />;
  }
  if ("id" in route && route.id !== undefined) {
    const extDetail = routes.find((r) => r.page === route.page);
    if (extDetail?.detailComponent) {
      const Comp = extDetail.detailComponent;
      return <Comp id={(route as any).id} onNavigate={navigate} />;
    }
  }

  // 4. Built-in pages
  return (
    <>
      {route.page === "home" && <HomePage onNavigate={navigate} />}
      {route.page === "scenes" && <ScenesPage onNavigate={navigate} />}
      {route.page === "scene" && route.id !== undefined && <SceneDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "performers" && <PerformersPage onNavigate={navigate} />}
      {route.page === "performer" && route.id !== undefined && <PerformerDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "studios" && <StudiosPage onNavigate={navigate} />}
      {route.page === "studio" && route.id !== undefined && <StudioDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "tags" && <TagsPage onNavigate={navigate} />}
      {route.page === "tag" && route.id !== undefined && <TagDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "galleries" && <GalleriesPage onNavigate={navigate} />}
      {route.page === "gallery" && route.id !== undefined && <GalleryDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "groups" && <GroupsPage onNavigate={navigate} />}
      {route.page === "group" && route.id !== undefined && <GroupDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "images" && <ImagesPage onNavigate={navigate} />}
      {route.page === "image" && route.id !== undefined && <ImageDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "settings" && <SettingsPage />}
      {route.page === "stats" && <StatsPage onNavigate={navigate} />}
      {route.page === "logs" && <LogsPage />}
      {route.page === "markers" && <SceneMarkersPage onNavigate={navigate} />}
      {route.page === "sceneparser" && <SceneFilenameParserPage onNavigate={navigate} />}
    </>
  );
}
