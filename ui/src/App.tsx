import { useState, useEffect, useCallback, useMemo, lazy, Suspense } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Database, Loader2 } from "lucide-react";
import { Navbar } from "./components/Navbar";
import { TutorialStoryboardDialog, TUTORIAL_STORYBOARD_EVENT, openTutorialStoryboard, type TutorialOpenRequest } from "./components/TutorialStoryboardDialog";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { RouteRegistryProvider, useRouteRegistry } from "./router/RouteRegistry";
import { AppConfigProvider, useAppConfig } from "./state/AppConfigContext";
import { ExtensionLoaderProvider, useExtensions } from "./extensions/ExtensionLoader";
import { canAccessExtensionContribution } from "./extensions/extension-permissions";
import { VideoQueueProvider } from "./state/VideoQueueContext";
import { SetupWizardPage } from "./pages/SetupWizardPage";
import { LoginPage } from "./pages/LoginPage";
import { AuthBootstrapPage } from "./pages/AuthBootstrapPage";
import { RedeemInvitePage } from "./pages/RedeemInvitePage";
import { AuthProvider, useAuth } from "./auth/AuthContext";
import { auth, database } from "./api/client";
import { useKeySequence } from "./hooks/useKeySequence";
import { KeyboardShortcutProvider, useKeyboardShortcuts } from "./keyboard/KeyboardShortcutProvider";
import { KeyboardShortcutsDialog } from "./components/KeyboardShortcutsDialog";
import { LOCATION_CHANGE_EVENT, Route, buildCurrentUrl, buildRoutePath, buildRouteUrl, navigateToUrl, parseCurrentRoute, parseLegacyHashRoute, readStoredRoute, resolveCurrentRoute, syncRouteHistory } from "./router/location";
import { DetailListStateCacheProvider } from "./hooks/useDetailListUrlState";
import { AppFloatingUI } from "./components/AppFloatingUI";
import { ServerAvailabilityBanner } from "./components/ServerAvailabilityBanner";
import { MutationFailureNotice } from "./components/MutationFailureNotice";
import { StartupGate } from "./components/StartupGate";
import { getApiValidationFailureDetail } from "./utils/requestFailure";
import { ExtensionKeyboardActions } from "./extensions/ExtensionKeyboardActions";

function normalizeRoute(route: Route): Route {
  if (route.page === "logs") {
    return { page: "settings" };
  }

  return route;
}

const BUILTIN_ROUTE_PERMISSIONS: Partial<Record<Route["page"], string>> = {
  videos: "videos.read",
  video: "videos.read",
  audios: "audios.read",
  audio: "audios.read",
  texts: "texts.read",
  text: "texts.read",
  "video-span": "segments.read",
  segments: "segments.read",
  segment: "segments.read",
  face: "faces.read",
  performers: "performers.read",
  performer: "performers.read",
  studios: "studios.read",
  studio: "studios.read",
  tags: "tags.read",
  tag: "tags.read",
  galleries: "galleries.read",
  gallery: "galleries.read",
  groups: "groups.read",
  group: "groups.read",
  compilation: "groups.read",
  images: "images.read",
  image: "images.read",
  faces: "faces.read",
  videoparser: "videos.read",
  duplicates: "videos.read",
  stats: "system.read",
};

// Lazy-loaded page components for code splitting
const VideosPage = lazy(() => import("./pages/VideosPage").then(m => ({ default: m.VideosPage })));
const AudiosPage = lazy(() => import("./pages/AudiosPage").then(m => ({ default: m.AudiosPage })));
const TextsPage = lazy(() => import("./pages/TextsPage").then(m => ({ default: m.TextsPage })));
const SegmentsPage = lazy(() => import("./pages/SegmentsPage").then(m => ({ default: m.SegmentsPage })));
const PerformersPage = lazy(() => import("./pages/PerformersPage").then(m => ({ default: m.PerformersPage })));
const StudiosPage = lazy(() => import("./pages/StudiosPage").then(m => ({ default: m.StudiosPage })));
const TagsPage = lazy(() => import("./pages/TagsPage").then(m => ({ default: m.TagsPage })));
const GalleriesPage = lazy(() => import("./pages/GalleriesPage").then(m => ({ default: m.GalleriesPage })));
const GroupsPage = lazy(() => import("./pages/GroupsPage").then(m => ({ default: m.GroupsPage })));
const ImagesPage = lazy(() => import("./pages/ImagesPage").then(m => ({ default: m.ImagesPage })));
const SettingsPage = lazy(() => import("./pages/SettingsPage").then(m => ({ default: m.SettingsPage })));
const StatsPage = lazy(() => import("./pages/StatsPage").then(m => ({ default: m.StatsPage })));
const VideoDetailPage = lazy(() => import("./pages/VideoDetailPage").then(m => ({ default: m.VideoDetailPage })));
const AudioDetailPage = lazy(() => import("./pages/AudioDetailPage").then(m => ({ default: m.AudioDetailPage })));
const TextDetailPage = lazy(() => import("./pages/TextDetailPage").then(m => ({ default: m.TextDetailPage })));
const SegmentDetailPage = lazy(() => import("./pages/SegmentDetailPage").then(m => ({ default: m.SegmentDetailPage })));
const ResolvedSpanPlayPage = lazy(() => import("./pages/ResolvedSpanPlayPage").then(m => ({ default: m.ResolvedSpanPlayPage })));
const PerformerDetailPage = lazy(() => import("./pages/PerformerDetailPage").then(m => ({ default: m.PerformerDetailPage })));
const StudioDetailPage = lazy(() => import("./pages/StudioDetailPage").then(m => ({ default: m.StudioDetailPage })));
const TagDetailPage = lazy(() => import("./pages/TagDetailPage").then(m => ({ default: m.TagDetailPage })));
const GalleryDetailPage = lazy(() => import("./pages/GalleryDetailPage").then(m => ({ default: m.GalleryDetailPage })));
const GroupDetailPage = lazy(() => import("./pages/GroupDetailPage").then(m => ({ default: m.GroupDetailPage })));
const CompilationPlayerPage = lazy(() => import("./pages/CompilationPlayerPage").then(m => ({ default: m.CompilationPlayerPage })));
const ImageDetailPage = lazy(() => import("./pages/ImageDetailPage").then(m => ({ default: m.ImageDetailPage })));
const FacesPage = lazy(() => import("./pages/FacesPage").then(m => ({ default: m.FacesPage })));
const FaceDetailPage = lazy(() => import("./pages/FaceDetailPage").then(m => ({ default: m.FaceDetailPage })));
const DuplicateFinderPage = lazy(() => import("./pages/DuplicateFinderPage").then(m => ({ default: m.DuplicateFinderPage })));

const VideoFilenameParserPage = lazy(() => import("./pages/VideoFilenameParserPage").then(m => ({ default: m.VideoFilenameParserPage })));
const HomePage = lazy(() => import("./pages/HomePage").then(m => ({ default: m.HomePage })));

export default function App() {
  const [route, setRoute] = useState<Route>(() => {
    const legacyRoute = parseLegacyHashRoute(window.location.hash);
    return normalizeRoute(legacyRoute ?? resolveCurrentRoute());
  });

  useEffect(() => {
    if (window.location.pathname === "/logs") {
      const settingsLogsRoute: Route = { page: "settings" };
      navigateToUrl("/settings/system-info/logs", { replace: true, state: settingsLogsRoute });
      setRoute(settingsLogsRoute);
      syncRouteHistory("push");
      return;
    }

    const legacyRoute = parseLegacyHashRoute(window.location.hash);
    if (legacyRoute) {
      const normalizedLegacyRoute = normalizeRoute(legacyRoute);
      navigateToUrl(buildCurrentUrl(buildRoutePath(normalizedLegacyRoute), window.location.search), { replace: true, state: normalizedLegacyRoute });
      setRoute(normalizedLegacyRoute);
    } else {
      const currentRoute = resolveCurrentRoute();
      const normalizedCurrentRoute = normalizeRoute(currentRoute);
      if (normalizedCurrentRoute.page !== currentRoute.page || normalizedCurrentRoute.id !== currentRoute.id) {
        navigateToUrl(buildCurrentUrl(buildRoutePath(normalizedCurrentRoute), window.location.search), { replace: true, state: normalizedCurrentRoute });
        setRoute(normalizedCurrentRoute);
      }
    }
    // Redirect /home to / (canonical home URL)
    if (window.location.pathname === "/home") {
      navigateToUrl(buildCurrentUrl("/", window.location.search), { replace: true });
    }

    syncRouteHistory("push");
  }, []);

  useEffect(() => {
    const handleLocationChange = (event: Event) => {
      const replacesCurrentEntry = event instanceof CustomEvent && event.detail?.replace === true;
      syncRouteHistory(event.type === "popstate" ? "history" : replacesCurrentEntry ? "replace" : "push");
      const currentUrl = buildCurrentUrl(window.location.pathname, window.location.search);
      // Recover route from history.state first, then from session-scoped route history.
      // This keeps derived-query provenance available even if a navigation path only preserved the URL.
      const rawState = event instanceof PopStateEvent ? event.state : window.history.state;
      const stateRoute = rawState && typeof rawState === "object" && typeof (rawState as Route).page === "string"
        ? rawState as Route
        : undefined;
      setRoute(normalizeRoute(stateRoute ?? readStoredRoute(currentUrl) ?? parseCurrentRoute()));
    };
    window.addEventListener("popstate", handleLocationChange);
    window.addEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    return () => {
      window.removeEventListener("popstate", handleLocationChange);
      window.removeEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    };
  }, []);

  const navigate = useCallback((r: Route) => {
    const currentUrl = buildCurrentUrl(window.location.pathname, window.location.search);
    const nextUrl = buildRouteUrl(r);
    if (currentUrl === nextUrl) {
      window.dispatchEvent(new CustomEvent("cove-page-reset", { detail: r.page }));
    } else {
      // Store the full route (including non-URL-serializable fields) in history.state
      // so the location change handler can recover it without URL round-tripping.
      if (!navigateToUrl(nextUrl, { state: r })) return;
      setRoute(r);
      // Forward navigation to a different page should start at the top. Without this the
      // window keeps the previous page's scroll offset (e.g. a deep scroll position in the
      // faces list), so a shorter detail page opens scrolled to its bottom. Back/forward
      // navigation goes through popstate instead and keeps the browser's restored position.
      window.scrollTo(0, 0);
    }
  }, []);

  return (
    <RouteRegistryProvider>
      <AppConfigProvider>
        <ServerAvailabilityBanner />
        <MutationFailureNotice />
        <StartupGate>
          <AuthGate>
            <ExtensionLoaderProvider>
              <KeyboardShortcutProvider>
                <AppFloatingUI />
                <VideoQueueProvider>
                  <AppKeyboardShortcuts navigate={navigate} />
                  <ExtensionKeyboardActions route={route} />
                  <AppShell route={route} navigate={navigate} />
                </VideoQueueProvider>
              </KeyboardShortcutProvider>
            </ExtensionLoaderProvider>
          </AuthGate>
        </StartupGate>
      </AppConfigProvider>
    </RouteRegistryProvider>
  );
}

function AppKeyboardShortcuts({ navigate }: { navigate: (route: Route) => void }) {
  const { setShortcutDialogOpen } = useKeyboardShortcuts();

  const globalBindings = useMemo(() => [
    { id: "global.shortcuts", keys: "?", surface: "global" as const, action: () => setShortcutDialogOpen(true) },
    { id: "global.help", keys: "", surface: "global" as const, action: () => openTutorialStoryboard() },
    { id: "global.home", keys: "g h", surface: "global" as const, action: () => navigate({ page: "home" }) },
    { id: "global.videos", keys: "g s", surface: "global" as const, action: () => navigate({ page: "videos" }) },
    { id: "global.audios", keys: "g a", surface: "global" as const, action: () => navigate({ page: "audios" }) },
    { id: "global.texts", keys: "g x", surface: "global" as const, action: () => navigate({ page: "texts" }) },
    { id: "global.segments", keys: "g m", surface: "global" as const, action: () => navigate({ page: "segments" }) },
    { id: "global.faces", keys: "g f", surface: "global" as const, action: () => navigate({ page: "faces" }) },
    { id: "global.images", keys: "g i", surface: "global" as const, action: () => navigate({ page: "images" }) },
    { id: "global.groups", keys: "g v", surface: "global" as const, action: () => navigate({ page: "groups" }) },
    { id: "global.galleries", keys: "g l", surface: "global" as const, action: () => navigate({ page: "galleries" }) },
    { id: "global.performers", keys: "g p", surface: "global" as const, action: () => navigate({ page: "performers" }) },
    { id: "global.studios", keys: "g u", surface: "global" as const, action: () => navigate({ page: "studios" }) },
    { id: "global.tags", keys: "g t", surface: "global" as const, action: () => navigate({ page: "tags" }) },
    { id: "global.settings", keys: "g z", surface: "global" as const, action: () => navigate({ page: "settings" }) },
    { id: "global.stats", keys: "g d", surface: "global" as const, action: () => navigate({ page: "stats" }) },
  ], [navigate, setShortcutDialogOpen]);

  useKeySequence(globalBindings);
  return null;
}

/**
 * Wraps the app with AuthProvider once we know whether auth is enabled (from /api/system/status),
 * and renders the LoginPage when auth is required but the user is not yet signed in.
 */
function AuthGate({ children }: { children: React.ReactNode }) {
  const { status } = useAppConfig();
  const authEnabled = !!status?.authEnabled;

  return (
    <AuthProvider authEnabled={authEnabled}>
      <AuthGateInner>{children}</AuthGateInner>
    </AuthProvider>
  );
}

function getPostLoginRedirectUrl(): string {
  const redirect = new URLSearchParams(window.location.search).get("redirect");
  if (!redirect || !redirect.startsWith("/") || redirect.startsWith("//")) {
    return "/";
  }

  try {
    const url = new URL(redirect, window.location.origin);
    if (url.origin !== window.location.origin || url.pathname === "/login") {
      return "/";
    }

    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return "/";
  }
}

function AuthGateInner({ children }: { children: React.ReactNode }) {
  const { authEnabled, user, loading } = useAuth();
  const { data: bootstrapStatus } = useQuery({
    queryKey: ["auth", "bootstrap-status"],
    queryFn: auth.bootstrapStatus,
    enabled: authEnabled && !user,
  });

  useEffect(() => {
    if (loading || !authEnabled || !user || window.location.pathname !== "/login") {
      return;
    }

    navigateToUrl(getPostLoginRedirectUrl(), { replace: true });
  }, [authEnabled, loading, user]);

  if (window.location.pathname === "/auth/bootstrap") {
    return <AuthBootstrapPage />;
  }
  if (window.location.pathname === "/auth/redeem-invite") {
    return <RedeemInvitePage />;
  }
  if (authEnabled && loading) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }
  if (authEnabled && !user && bootstrapStatus?.ownerExists === false) {
    // Token-first setup: when an unconsumed setup token exists the owner must be created by redeeming
    // it (RedeemInvitePage auto-selects setup mode), not via the password-only bootstrap page. This
    // also covers odd URLs like a double-slashed "//auth/redeem-invite" that miss the exact-path
    // checks above and fall through here. The server enforces the same rule (SETUP_TOKEN_REQUIRED).
    return bootstrapStatus.hasSetupToken ? <RedeemInvitePage /> : <AuthBootstrapPage />;
  }
  if (authEnabled && !user) {
    return <LoginPage />;
  }
  return <>{children}</>;
}

function AppShell({ route, navigate }: { route: Route; navigate: (r: Route) => void }) {
  const { config, configLoading, status, statusLoading } = useAppConfig();
  const { manifest } = useExtensions();
  const { shortcutDialogOpen, setShortcutDialogOpen } = useKeyboardShortcuts();
  const queryClient = useQueryClient();
  const migrateMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: database.migrate,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["system-status"] });
    },
  });
  const [setupDismissed, setSetupDismissed] = useState(() => sessionStorage.getItem("cove-setup-dismissed") === "true");
  const [setupFlowActive, setSetupFlowActive] = useState(false);
  const [tutorialOpen, setTutorialOpen] = useState(false);
  const [tutorialRequest, setTutorialRequest] = useState<TutorialOpenRequest | undefined>();

  // Owner account status: initial setup must produce an owner before the app is usable. If the wizard
  // was dismissed before an owner password was set (e.g. closed early when reaching Cove through a
  // reverse proxy), re-show it on every visit until an owner exists so the owner step can be completed
  // without needing a token. The failsafe lockdown only engages once an owner exists.
  const { data: bootstrapStatus } = useQuery({ queryKey: ["auth", "bootstrap-status"], queryFn: auth.bootstrapStatus });
  const ownerMissing = bootstrapStatus?.ownerExists === false;

  // Show setup wizard if config has no library paths and user hasn't dismissed it
  const needsSetup = config && config.covePaths.filter(p => p.path.trim() !== "").length === 0 && !setupDismissed;

  useEffect(() => {
    if (needsSetup) {
      setSetupFlowActive(true);
    }
  }, [needsSetup]);

  useEffect(() => {
    const openTutorial = (event: Event) => {
      setTutorialRequest(event instanceof CustomEvent ? event.detail : undefined);
      setTutorialOpen(true);
    };
    window.addEventListener(TUTORIAL_STORYBOARD_EVENT, openTutorial);
    return () => window.removeEventListener(TUTORIAL_STORYBOARD_EVENT, openTutorial);
  }, []);

  useEffect(() => {
    if (route.page === "manual") {
      setTutorialRequest({ topicId: route.manualTopicId, slideId: route.manualSlideId });
      setTutorialOpen(true);
      return;
    }

    const params = new URLSearchParams(window.location.search);
    const topicId = params.get("tutorial") ?? undefined;
    if (!topicId) {
      return;
    }

    setTutorialRequest({ topicId, slideId: params.get("tutorialSlide") ?? params.get("slide") ?? undefined });
    setTutorialOpen(true);
  }, [route]);

  const showSetupWizard = Boolean(config) && (ownerMissing || (!setupDismissed && (needsSetup || setupFlowActive)));

  if (configLoading || statusLoading) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }

  if (status?.migrationStatusUnknown) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center px-4">
        <div className="max-w-lg text-center space-y-4 rounded border border-yellow-500/30 bg-surface p-6 shadow-lg">
          <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-yellow-500/15 text-yellow-200">
            <AlertTriangle className="h-6 w-6" aria-hidden="true" />
          </div>
          <h1 className="text-xl font-semibold text-foreground">Database Status Unavailable</h1>
          <p className="text-sm text-muted-foreground">
            Cove could not determine whether database migrations are pending. Check the server logs before continuing.
          </p>
          {status.migrationStatusError ? (
            <div className="break-words rounded bg-background/70 p-3 text-left text-xs text-muted-foreground">
              {status.migrationStatusError}
            </div>
          ) : null}
        </div>
      </div>
    );
  }

  // Migration gate: block the app until migrations are explicitly applied.
  if (status?.migrationRequired) {
    const migrationError = migrateMutation.error ? getApiValidationFailureDetail(migrateMutation.error) : null;
    const migrationResult = migrateMutation.data;

    return (
      <div className="min-h-screen bg-background flex items-center justify-center px-4">
        <div className="max-w-lg text-center space-y-4 rounded border border-border bg-surface p-6 shadow-lg">
          <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-accent/15 text-accent">
            <Database className="h-6 w-6" aria-hidden="true" />
          </div>
          <h1 className="text-xl font-semibold text-foreground">Database Update Required</h1>
          <p className="text-sm text-muted-foreground">
            Cove needs to apply database migrations before the library can open.
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
            A database backup will be created before any migration runs.
          </p>
          <button
            type="button"
            onClick={() => migrateMutation.mutate()}
            disabled={migrateMutation.isPending}
            className="inline-flex items-center justify-center gap-2 rounded bg-accent px-4 py-2 text-sm font-medium text-background transition hover:bg-accent/90 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {migrateMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Database className="h-4 w-4" aria-hidden="true" />}
            Run Migration
          </button>
          {migrationResult?.preMigrationBackupPath ? (
            <div className="rounded bg-background/70 p-3 text-left text-xs text-muted-foreground">
              <div className="mb-1 flex items-center gap-2 font-medium text-foreground">
                <CheckCircle2 className="h-4 w-4 text-green-400" aria-hidden="true" />
                Backup created
              </div>
              <div className="break-all font-mono">{migrationResult.preMigrationBackupPath}</div>
            </div>
          ) : null}
          {migrationError ? (
            <div className="rounded border border-red-500/40 bg-red-500/10 p-3 text-left text-xs text-red-100">
              {migrationError}
            </div>
          ) : null}
        </div>
      </div>
    );
  }

  if (showSetupWizard && config) {
    return (
      <SetupWizardPage
        config={config}
        onComplete={(options) => {
          setSetupFlowActive(false);
          setSetupDismissed(true);
          sessionStorage.setItem("cove-setup-dismissed", "true");
          if (options?.showTutorial) {
            setTutorialRequest({ topicId: "getting-started" });
            setTutorialOpen(true);
          }
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
      <TutorialStoryboardDialog
        open={tutorialOpen}
        onClose={() => {
          setTutorialOpen(false);
          if (route.page === "manual") navigate({ page: "home" });
        }}
        request={tutorialRequest}
        currentPage={route.page}
        extensionTopics={manifest?.tutorialTopics ?? []}
        onTopicChange={(topicId, slideId) => {
          if (route.page === "manual") {
            navigateToUrl(buildRoutePath({ page: "manual", manualTopicId: topicId, manualSlideId: slideId }), { replace: true, state: { page: "manual", manualTopicId: topicId, manualSlideId: slideId } });
          }
        }}
      />
      <KeyboardShortcutsDialog open={shortcutDialogOpen} onClose={() => setShortcutDialogOpen(false)} />
    </div>
  );
}

export function AppRoutes({ route, navigate }: { route: Route; navigate: (r: Route) => void }) {
  const { routes } = useRouteRegistry();
  const { getPageOverride, resolveComponent, manifest } = useExtensions();
  const { hasPermission } = useAuth();

  const requiredPermission = BUILTIN_ROUTE_PERMISSIONS[route.page];
  if (requiredPermission && !hasPermission(requiredPermission)) {
    return <AccessDeniedPage navigate={navigate} />;
  }

  // 1. Check for page overrides (extension replaces a built-in page)
  const override = getPageOverride(route.page);
  if (override) {
    const Component = resolveComponent(override.extensionId, override.componentName);
    if (Component) {
      return <Component onNavigate={navigate} />;
    }
  }

  // 2. Check extension-contributed pages (new pages via UIPageDefinition)
  const extPage = manifest?.pages.find((p) => p.route === route.page);
  if (extPage?.componentName) {
    if (!canAccessExtensionContribution(extPage, hasPermission)) {
      return <AccessDeniedPage navigate={navigate} />;
    }
    const Component = resolveComponent(extPage.extensionId ?? "", extPage.componentName);
    if (Component) {
      // Pass id if this is a detail page route
      const props: Record<string, unknown> = { onNavigate: navigate };
      if ("id" in route && route.id !== undefined) {
        props.id = route.id;
      }
      if (route.slug !== undefined) {
        props.slug = route.slug;
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
      {(route.page === "home" || (route.page === "dashboard" && route.id !== undefined)) && <HomePage dashboardId={route.page === "dashboard" ? route.id : undefined} onNavigate={navigate} />}
      {route.page === "manual" && <HomePage onNavigate={navigate} />}
      {route.page === "videos" && <VideosPage onNavigate={navigate} />}
      {route.page === "video" && route.id !== undefined && <VideoDetailPage id={route.id} initialSeekTo={route.seekTo} initialTab={route.videoTab} onNavigate={navigate} />}
      {route.page === "audios" && <AudiosPage onNavigate={navigate} />}
      {route.page === "audio" && route.id !== undefined && <AudioDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "texts" && <TextsPage onNavigate={navigate} />}
      {route.page === "text" && route.id !== undefined && <TextDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "video-span" && route.id !== undefined && route.spanKey !== undefined && (
        <ResolvedSpanPlayPage videoId={route.id} spanKey={route.spanKey} profileId={route.profileId} derivedQueryDescriptor={route.derivedQueryDescriptor} onNavigate={navigate} />
      )}
      {route.page === "segments" && <SegmentsPage onNavigate={navigate} />}
      {route.page === "segment" && route.id !== undefined && <SegmentDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "faces" && <FacesPage onNavigate={navigate} />}
      {route.page === "face" && route.id !== undefined && <FaceDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "performers" && <PerformersPage onNavigate={navigate} />}
      {route.page === "performer" && route.id !== undefined && <DetailListStateCacheProvider key={`performer-${route.id}`}><PerformerDetailPage id={route.id} onNavigate={navigate} /></DetailListStateCacheProvider>}
      {route.page === "studios" && <StudiosPage onNavigate={navigate} />}
      {route.page === "studio" && route.id !== undefined && <DetailListStateCacheProvider key={`studio-${route.id}`}><StudioDetailPage id={route.id} onNavigate={navigate} /></DetailListStateCacheProvider>}
      {route.page === "tags" && <TagsPage onNavigate={navigate} />}
      {route.page === "tag" && route.id !== undefined && <DetailListStateCacheProvider key={`tag-${route.id}`}><TagDetailPage id={route.id} onNavigate={navigate} /></DetailListStateCacheProvider>}
      {route.page === "galleries" && <GalleriesPage onNavigate={navigate} />}
      {route.page === "gallery" && route.id !== undefined && <DetailListStateCacheProvider key={`gallery-${route.id}`}><GalleryDetailPage id={route.id} onNavigate={navigate} /></DetailListStateCacheProvider>}
      {route.page === "groups" && <GroupsPage onNavigate={navigate} />}
      {route.page === "group" && route.id !== undefined && <GroupDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "compilation" && route.id !== undefined && <CompilationPlayerPage id={route.id} itemOrder={route.compilationItemOrder} onNavigate={navigate} />}
      {route.page === "images" && <ImagesPage onNavigate={navigate} />}
      {route.page === "image" && route.id !== undefined && <ImageDetailPage id={route.id} onNavigate={navigate} />}
      {route.page === "settings" && <SettingsPage />}
      {route.page === "stats" && <StatsPage onNavigate={navigate} />}
      {route.page === "duplicates" && <DuplicateFinderPage onNavigate={navigate} />}
      {route.page === "videoparser" && <VideoFilenameParserPage onNavigate={navigate} />}
    </>
  );
}

function AccessDeniedPage({ navigate }: { navigate: (r: Route) => void }) {
  return (
    <div className="mx-auto flex min-h-[40vh] max-w-xl flex-col items-center justify-center gap-4 text-center">
      <h1 className="text-2xl font-semibold text-foreground">Access denied</h1>
      <p className="text-sm text-secondary">Your account does not have permission to view this page.</p>
      <button
        onClick={() => navigate({ page: "home" })}
        className="rounded-lg border border-border px-4 py-2 text-sm font-medium text-foreground hover:border-accent hover:text-accent"
      >
        Go home
      </button>
    </div>
  );
}
