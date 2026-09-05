import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { auth, database, jobs, metadata, system, stashMigration } from "../api/client";
import type { StashPreviewResult, StashImportOptions, StashImportResult, StashPathMapping } from "../api/client";
import type { CoveConfig, CovePathConfig, JobInfo } from "../api/types";
import { authStore } from "../auth/authStore";
import { useAuth } from "../auth/AuthContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import { navigateToUrl } from "../router/location";
import {
  FolderOpen,
  Plus,
  Trash2,
  ChevronRight,
  ChevronLeft,
  Check,
  Loader2,
  Play,
  Settings,
  Database,
  RefreshCw,
  BookOpen,
  FolderCheck,
  AlertTriangle,
} from "lucide-react";

interface Props {
  config: CoveConfig;
  onComplete: (options?: { showTutorial?: boolean }) => void;
}

export type Step =
  "welcome" | "source" | "paths" | "confirm" | "stash-config" | "backup-restore" | "owner" | "theme" | "done";
export type SetupMode = "fresh" | "stash" | "backup" | null;
type ActiveSetupMode = Exclude<SetupMode, null>;

export function buildSetupStepList(activeMode: ActiveSetupMode, needsOwnerSetup: boolean): Step[] {
  const ownerStep: Step[] = needsOwnerSetup ? ["owner"] : [];
  if (activeMode === "stash") {
    return ["welcome", "source", ...ownerStep, "stash-config", "theme", "done"];
  }
  if (activeMode === "backup") {
    return ["welcome", "source", "backup-restore", ...ownerStep, "theme", "done"];
  }
  return ["welcome", "source", "paths", "confirm", ...ownerStep, "theme", "done"];
}

export function resolveStashSetupEntryStep(ownerExists: boolean): Step {
  return ownerExists ? "stash-config" : "owner";
}

export function resolveOwnerNextStep(activeMode: ActiveSetupMode, stashImportComplete: boolean): Step {
  return activeMode === "stash" && !stashImportComplete ? "stash-config" : "theme";
}

export function resolveOwnerBackStep(activeMode: ActiveSetupMode, stashImportComplete: boolean): Step {
  if (activeMode === "stash") {
    return stashImportComplete ? "stash-config" : "source";
  }
  return activeMode === "fresh" ? "confirm" : "backup-restore";
}

interface BackupRestoreResultSummary {
  backupPath: string;
  preRestoreBackupPath: string | null;
  configBackupPath: string | null;
}

const TUTORIAL_STEPS = [
  {
    eyebrow: "Step 1",
    title: "Scan and generate your library",
    description:
      "After setup, run Scan to index files, then use Scan & Generate when you want previews, thumbnails, hashes, sprites, or segments.",
    actionLabel: "Open Scan & Generate",
    highlight: "This is the fastest way to move from an empty library to something you can actually browse.",
    checklist: [
      "Scan the folders you just added",
      "Run Generate for previews and images",
      "Come back later if you add more media",
    ],
    icon: FolderOpen,
    kind: "tasks",
  },
  {
    eyebrow: "Step 2",
    title: "Browse with the view that fits the media",
    description:
      "Videos and images each have multiple layouts. Start with grid, then try feed, wall, or Infinite page size when you want long browsing sessions.",
    actionLabel: "Open Videos or Images",
    highlight:
      "Feed is better for reading details. Wall is better for visual skimming. Infinite keeps the list moving.",
    checklist: [
      "Switch between grid, wall, and feed",
      "Use page size to turn on Infinite",
      "Open detail pages when something needs cleanup",
    ],
    icon: Play,
    kind: "browse",
  },
  {
    eyebrow: "Step 3",
    title: "Scrape and identify when titles are incomplete",
    description:
      "If a video or image is missing tags, performers, studios, or dates, use Scrape or Identify from the media pages and then tune providers in Settings.",
    actionLabel: "Use Scrape or Identify",
    highlight:
      "Start from a single item first so you can verify the source and field mappings before doing it in bulk.",
    checklist: [
      "Open one item and inspect the current fields",
      "Run Scrape or Identify",
      "Adjust scrapers or MetadataServer settings if the match looks wrong",
    ],
    icon: Database,
    kind: "metadata",
  },
  {
    eyebrow: "Step 4",
    title: "Keep settings and docs within reach",
    description:
      "Most setup tasks live in Settings, and the docs site fills in the edge cases: extensions, MetadataServer setup, import workflows, and troubleshooting.",
    actionLabel: "Use Settings and docs.cove.app",
    highlight: "If you forget where something from this wizard went, it is almost always in Settings.",
    checklist: [
      "Return to Settings for paths, scrapers, and themes",
      "Use docs.cove.app for deeper walkthroughs",
      "Treat the wizard as the fast path, not the only path",
    ],
    icon: BookOpen,
    kind: "docs",
  },
] as const;

function getThemePreviewColor(cssVariables: Record<string, string> | undefined, key: string, fallback: string) {
  return cssVariables?.[`--${key}`] ?? cssVariables?.[`--color-${key}`] ?? fallback;
}

function ThemeMiniPreview({ cssVariables }: { cssVariables?: Record<string, string> }) {
  const background = getThemePreviewColor(cssVariables, "background", "#0b1220");
  const card = getThemePreviewColor(cssVariables, "card", "#152033");
  const surface = getThemePreviewColor(cssVariables, "surface", card);
  const accent = getThemePreviewColor(cssVariables, "accent", "#2f80ed");
  const foreground = getThemePreviewColor(cssVariables, "foreground", "#f8fafc");
  const secondary = getThemePreviewColor(cssVariables, "secondary", foreground);

  return (
    <div className="overflow-hidden rounded-xl border border-black/10" style={{ background }}>
      <div
        className="flex items-center justify-between gap-2 border-b border-black/10 px-3 py-2"
        style={{ background: surface }}
      >
        <div className="flex items-center gap-1.5">
          {[background, surface, card, accent, foreground].map((color, index) => (
            <span
              key={`${color}-${index}`}
              className="h-4 w-4 rounded-full border border-black/15"
              style={{ background: color }}
            />
          ))}
        </div>
        <div className="h-2 w-14 rounded-full" style={{ background: secondary, opacity: 0.55 }} />
      </div>
      <div className="grid grid-cols-[3.75rem_minmax(0,1fr)] gap-2 p-3">
        <div className="space-y-1.5 rounded-lg p-2" style={{ background: card }}>
          {[0.85, 0.6, 0.38].map((opacity, index) => (
            <div key={index} className="h-1.5 rounded-full" style={{ background: foreground, opacity }} />
          ))}
        </div>
        <div className="space-y-2">
          <div className="h-9 rounded-lg" style={{ background: accent }} />
          <div className="grid grid-cols-3 gap-1.5">
            {[surface, card, accent].map((color, index) => (
              <div
                key={`${color}-${index}`}
                className="h-6 rounded-md"
                style={{ background: color, opacity: index === 2 ? 0.75 : 1 }}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function TutorialPreview({ step }: { step: (typeof TUTORIAL_STEPS)[number] }) {
  if (step.kind === "tasks") {
    return (
      <div className="rounded-2xl border border-border/70 bg-background/60 p-4">
        <div className="flex items-center justify-between gap-3 border-b border-border/60 pb-3">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-muted">Settings</div>
            <div className="text-sm font-semibold text-foreground">Tasks</div>
          </div>
          <div className="rounded-full bg-accent/10 px-2.5 py-1 text-[11px] font-medium text-accent">First run</div>
        </div>
        <div className="mt-4 space-y-3">
          {["Scan library", "Generate previews"].map((label, index) => (
            <div key={label} className="rounded-2xl border border-border bg-card p-3">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <div className="text-sm font-medium text-foreground">{label}</div>
                  <div className="text-xs text-muted">
                    {index === 0 ? "Reads folders and creates items" : "Builds thumbnails, previews, and hashes"}
                  </div>
                </div>
                <div className="rounded-full bg-accent px-3 py-1 text-[11px] font-semibold text-white">
                  {index === 0 ? "Run first" : "Run second"}
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  if (step.kind === "browse") {
    return (
      <div className="rounded-2xl border border-border/70 bg-background/60 p-4">
        <div className="flex flex-wrap items-center gap-2 border-b border-border/60 pb-3 text-xs text-muted">
          {[
            { label: "Grid", active: false },
            { label: "Feed", active: true },
            { label: "Wall", active: false },
            { label: "Infinite", active: true },
          ].map((item) => (
            <div
              key={item.label}
              className={`rounded-full px-2.5 py-1 font-medium ${item.active ? "bg-accent text-white" : "bg-card text-secondary"}`}
            >
              {item.label}
            </div>
          ))}
        </div>
        <div className="mt-4 grid gap-3 md:grid-cols-2">
          {[0, 1].map((index) => (
            <div key={index} className="overflow-hidden rounded-2xl border border-border bg-card">
              <div className="aspect-[16/10] bg-accent/25" />
              <div className="space-y-2 p-3">
                <div className="h-2.5 w-3/4 rounded-full bg-foreground/80" />
                <div className="flex flex-wrap gap-1.5 text-[11px] text-muted">
                  <span className="rounded-full border border-border px-2 py-0.5">performer</span>
                  <span className="rounded-full border border-border px-2 py-0.5">tag</span>
                  <span className="rounded-full border border-border px-2 py-0.5">rating</span>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  if (step.kind === "metadata") {
    return (
      <div className="rounded-2xl border border-border/70 bg-background/60 p-4">
        <div className="grid gap-4 md:grid-cols-[1.15fr_0.85fr]">
          <div className="overflow-hidden rounded-2xl border border-border bg-card">
            <div className="aspect-[16/10] bg-accent/25" />
            <div className="space-y-2 p-3">
              <div className="h-2.5 w-5/6 rounded-full bg-foreground/80" />
              <div className="h-2.5 w-2/3 rounded-full bg-foreground/40" />
            </div>
          </div>
          <div className="rounded-2xl border border-border bg-card p-3">
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-muted">Actions</div>
            <div className="mt-3 flex flex-wrap gap-2">
              <div className="rounded-xl bg-accent px-3 py-1.5 text-xs font-semibold text-white">Scrape</div>
              <div className="rounded-xl border border-border px-3 py-1.5 text-xs font-semibold text-foreground">
                Identify
              </div>
            </div>
            <div className="mt-4 space-y-2 text-xs text-muted">
              <div className="rounded-xl bg-background/70 px-3 py-2">
                Tags, performers, studios, dates, and urls can all be reviewed before you apply.
              </div>
              <div className="rounded-xl bg-background/70 px-3 py-2">
                Use one item first before running a bulk pass.
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-2xl border border-border/70 bg-background/60 p-4">
      <div className="grid gap-3 md:grid-cols-2">
        <div className="rounded-2xl border border-border bg-card p-3">
          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-muted">Settings</div>
          <div className="mt-3 space-y-2 text-sm text-secondary">
            <div className="rounded-xl bg-background/70 px-3 py-2">Paths and scans</div>
            <div className="rounded-xl bg-background/70 px-3 py-2">Scrapers and MetadataServer</div>
            <div className="rounded-xl bg-background/70 px-3 py-2">Themes and interface options</div>
          </div>
        </div>
        <div className="rounded-2xl border border-border bg-card p-3">
          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-muted">Docs</div>
          <div className="mt-3 rounded-xl bg-accent/10 px-3 py-2 text-sm font-semibold text-accent">docs.cove.app</div>
          <div className="mt-2 text-xs text-muted">
            Use it for extension setup, troubleshooting, deeper metadata workflows, and examples that do not fit in the
            wizard.
          </div>
        </div>
      </div>
    </div>
  );
}

export function SetupWizardPage({ config, onComplete }: Props) {
  const [step, setStep] = useState<Step>("welcome");
  const [setupMode, setSetupMode] = useState<SetupMode>(null);
  const [paths, setPaths] = useState<CovePathConfig[]>(
    config.covePaths.length > 0
      ? config.covePaths
      : [{ path: "", excludeVideo: false, excludeImage: false, excludeAudio: false, excludeText: false }],
  );
  const [error, setError] = useState<string | null>(null);
  const [stashDbPath, setStashDbPath] = useState("");
  const [stashPreview, setStashPreview] = useState<StashPreviewResult | null>(null);
  const [stashResult, setStashResult] = useState<StashImportResult | null>(null);
  const [stashImportJobId, setStashImportJobId] = useState<string | null>(null);
  const [coveGeneratedPath, setCoveGeneratedPath] = useState(config.generatedPath ?? "");
  const [migrateGeneratedContent, setMigrateGeneratedContent] = useState(true);
  const [pathMappings, setPathMappings] = useState<StashPathMapping[]>([{ source: "", target: "" }]);
  const [restoreBackupPath, setRestoreBackupPath] = useState("");
  const [restoreConfigBackupPath, setRestoreConfigBackupPath] = useState("");
  const [restoreConfirmed, setRestoreConfirmed] = useState(false);
  const [backupRestoreResult, setBackupRestoreResult] = useState<BackupRestoreResultSummary | null>(null);
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [ownerUsername, setOwnerUsername] = useState("owner");
  const [ownerPassword, setOwnerPassword] = useState("");
  const [ownerConfirmPassword, setOwnerConfirmPassword] = useState("");
  const queryClient = useQueryClient();
  const { refreshMe } = useAuth();
  const { availableThemes, activeThemeId, setActiveTheme } = useExtensions();
  const importPathMappings = pathMappings
    .map((mapping) => ({ source: mapping.source.trim(), target: mapping.target.trim() }))
    // Only send rows where BOTH sides are filled. An OR here would forward a half-typed row, which the
    // backend rejects ("requires both a source and a target path") — failing the entire import over a
    // stray empty field rather than just skipping that row.
    .filter((mapping) => mapping.source !== "" && mapping.target !== "");
  const stashImportOptions: StashImportOptions = {
    coveGeneratedPath: coveGeneratedPath.trim() || undefined,
    migrateGeneratedContent,
    pathMappings: importPathMappings.length > 0 ? importPathMappings : undefined,
  };
  const updatePathMapping = (index: number, field: keyof StashPathMapping, value: string) => {
    setPathMappings((current) =>
      current.map((mapping, mappingIndex) => (mappingIndex === index ? { ...mapping, [field]: value } : mapping)),
    );
  };
  const addPathMapping = () => setPathMappings((current) => [...current, { source: "", target: "" }]);
  const removePathMapping = (index: number) =>
    setPathMappings((current) =>
      current.length <= 1 ? [{ source: "", target: "" }] : current.filter((_, mappingIndex) => mappingIndex !== index),
    );
  const activeMode =
    setupMode ??
    (step === "stash-config" || stashImportJobId !== null || stashResult !== null
      ? "stash"
      : step === "backup-restore" || backupRestoreResult !== null
        ? "backup"
        : "fresh");
  const bootstrapStatusQuery = useQuery({ queryKey: ["auth", "bootstrap-status"], queryFn: auth.bootstrapStatus });
  const needsOwnerSetup = bootstrapStatusQuery.data?.ownerExists === false;
  const stepList = buildSetupStepList(activeMode, needsOwnerSetup);
  const themeOptions = useMemo(() => availableThemes, [availableThemes]);
  const goToPostContentSetup = async () => {
    const status = await bootstrapStatusQuery.refetch();
    setStep(status.data?.ownerExists === false ? "owner" : "theme");
  };
  const handleBeginStashSetup = async () => {
    setError(null);
    const status = await bootstrapStatusQuery.refetch();
    if (status.isError || !status.data) {
      setError("Cove could not verify whether an Owner account exists. Try again before starting the Stash import.");
      return;
    }

    setSetupMode("stash");
    setStep(resolveStashSetupEntryStep(status.data.ownerExists));
  };

  const stashPreviewMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => stashMigration.preview(stashDbPath),
    onSuccess: (data) => setStashPreview(data),
    onError: (err: Error) => setError(err.message),
  });
  const stashImportMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => stashMigration.startImport(stashDbPath, stashImportOptions),
    onSuccess: ({ jobId }) => {
      setError(null);
      setStashResult(null);
      setStashImportJobId(jobId);
    },
    onError: (err: Error) => setError(err.message),
  });

  const stashImportJobQuery = useQuery({
    queryKey: ["setup", "stash-import-job", stashImportJobId],
    queryFn: () => jobs.get(stashImportJobId!),
    enabled: stashImportJobId !== null,
    retry: false,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "pending" || status === "running" ? 1000 : false;
    },
  });

  const stashImportResultQuery = useQuery({
    queryKey: ["setup", "stash-import-result", stashImportJobId],
    queryFn: () => stashMigration.importResult(stashImportJobId!),
    enabled: stashImportJobId !== null && stashImportJobQuery.data?.status === "completed",
    retry: false,
    refetchInterval: (query) => (query.state.data ? false : 500),
  });
  const latestBackupQuery = useQuery({
    queryKey: ["setup", "latest-backup"],
    queryFn: () => database.latestBackup(),
    enabled: step === "backup-restore" || activeMode === "backup",
    retry: false,
  });
  const latestConfigBackupQuery = useQuery({
    queryKey: ["setup", "latest-config-backup"],
    queryFn: () => database.latestConfigBackup(),
    enabled: step === "backup-restore" || activeMode === "backup",
    retry: false,
  });
  const backupRestoreMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      const backupPath = restoreBackupPath.trim();
      const configBackupPath = restoreConfigBackupPath.trim();

      if (!backupPath) {
        throw new Error("Backup path is required.");
      }
      if (!restoreConfirmed) {
        throw new Error("Confirm that restoring will replace the current Cove data first.");
      }

      const restoreResult = await database.restore(backupPath);
      if (configBackupPath) {
        await database.restoreConfig(configBackupPath);
      }

      return {
        backupPath,
        preRestoreBackupPath: restoreResult.preRestoreBackupPath,
        configBackupPath: configBackupPath || null,
      };
    },
    onSuccess: async (result) => {
      setError(null);
      setBackupRestoreResult(result);
      await queryClient.invalidateQueries();
      await goToPostContentSetup();
    },
    onError: (err: Error) => setError(err.message),
  });

  useEffect(() => {
    if (!stashImportResultQuery.data) return;
    setError(null);
    setStashResult(stashImportResultQuery.data);
    goToPostContentSetup();
    queryClient.invalidateQueries();
  }, [queryClient, stashImportResultQuery.data]);

  useEffect(() => {
    const job = stashImportJobQuery.data;
    if (!job) return;

    if (job.status === "failed") {
      setError(job.error ?? "Stash import failed.");
    } else if (job.status === "cancelled") {
      setError("Stash import was cancelled.");
    }
  }, [stashImportJobQuery.data]);

  const activeStashImportJob = stashImportJobQuery.data;
  const isStashImportActive = activeStashImportJob?.status === "pending" || activeStashImportJob?.status === "running";

  const saveMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (cfg: CoveConfig) => system.saveConfig(cfg),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["system-config"] });
      goToPostContentSetup();
    },
    onError: (err: Error) => setError(err.message),
  });

  const scanMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => metadata.scan(),
    onSuccess: async ({ jobId }) => {
      setError(null);
      setScanJobId(jobId);
      await queryClient.invalidateQueries({ queryKey: ["jobs"] });
    },
    onError: (err: Error) => setError(err.message),
  });

  const ownerMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => auth.bootstrapOwner(ownerUsername.trim(), ownerPassword),
    onSuccess: async (response) => {
      setError(null);
      authStore.clearShareCredentials();
      authStore.setTokens(response.token, response.refreshToken);
      await refreshMe();
      await bootstrapStatusQuery.refetch();
      setStep(resolveOwnerNextStep(activeMode, stashResult !== null));
    },
    onError: (err: Error) => setError(err.message),
  });

  const addPath = () => {
    setPaths([
      ...paths,
      { path: "", excludeVideo: false, excludeImage: false, excludeAudio: false, excludeText: false },
    ]);
  };

  const removePath = (index: number) => {
    setPaths(paths.filter((_, i) => i !== index));
  };

  const updatePath = (index: number, updates: Partial<CovePathConfig>) => {
    setPaths(paths.map((p, i) => (i === index ? { ...p, ...updates } : p)));
  };

  const validPaths = paths.filter((p) => p.path.trim() !== "");

  const handleConfirm = () => {
    const updatedConfig: CoveConfig = {
      ...config,
      covePaths: validPaths,
    };
    saveMut.mutate(updatedConfig);
  };

  const handleOwnerSubmit = () => {
    setError(null);
    if (ownerPassword !== ownerConfirmPassword) {
      setError("Passwords do not match.");
      return;
    }

    ownerMut.mutate();
  };

  const handleFinish = (target: "videos" | "settings" = "videos") => {
    const path = target === "settings" ? "/settings" : "/videos";
    navigateToUrl(backupRestoreResult ? `${path}?tutorial=getting-started` : path);

    onComplete({ showTutorial: true });

    if (backupRestoreResult) {
      window.location.reload();
    }
  };

  return (
    <div className="min-h-screen bg-background flex items-center justify-center p-4">
      <div className="w-full max-w-2xl">
        {/* Progress indicator */}
        {(() => {
          const currentIdx = stepList.indexOf(step);
          return (
            <div className="flex items-center justify-center gap-2 mb-8">
              {stepList.map((s, i) => (
                <div key={s} className="flex items-center gap-2">
                  <div
                    className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-medium transition-colors ${
                      s === step
                        ? "bg-accent text-white"
                        : currentIdx > i
                          ? "bg-green-600 text-white"
                          : "bg-card border border-border text-muted"
                    }`}
                  >
                    {currentIdx > i ? <Check className="w-4 h-4" /> : i + 1}
                  </div>
                  {i < stepList.length - 1 && <div className="w-12 h-0.5 bg-border" />}
                </div>
              ))}
            </div>
          );
        })()}

        <div className="bg-surface border border-border rounded-2xl shadow-2xl overflow-hidden">
          {step === "welcome" && (
            <div className="p-8 text-center">
              <div className="w-16 h-16 bg-accent/20 rounded-2xl flex items-center justify-center mx-auto mb-6">
                <Play className="w-8 h-8 text-accent" />
              </div>
              <h1 className="text-2xl font-bold text-foreground mb-3">Welcome to Cove</h1>
              <p className="text-secondary mb-6 max-w-md mx-auto">
                Cove is a self-hosted organizer for your media library. Let's get set up by configuring your library.
              </p>
              <button
                onClick={() => setStep("source")}
                className="inline-flex items-center gap-2 px-6 py-3 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium transition-colors"
              >
                Get Started <ChevronRight className="w-4 h-4" />
              </button>
            </div>
          )}

          {step === "source" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">How would you like to start?</h2>
              <p className="text-sm text-secondary mb-6">
                Start fresh, migrate from Stash, or restore a previous Cove backup.
              </p>
              <div className="grid gap-4 md:grid-cols-3">
                <button
                  onClick={() => {
                    setSetupMode("fresh");
                    setStep("paths");
                  }}
                  className="flex flex-col items-center gap-3 p-6 bg-card border-2 border-border hover:border-accent rounded-xl transition-colors text-left"
                >
                  <FolderOpen className="w-8 h-8 text-accent" />
                  <div>
                    <div className="font-semibold text-foreground mb-1">Start Fresh</div>
                    <div className="text-xs text-secondary">Configure library paths and scan from scratch.</div>
                  </div>
                </button>
                <button
                  onClick={handleBeginStashSetup}
                  disabled={bootstrapStatusQuery.isFetching}
                  className="flex flex-col items-center gap-3 p-6 bg-card border-2 border-border hover:border-accent rounded-xl transition-colors text-left disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Database className="w-8 h-8 text-accent" />
                  <div>
                    <div className="font-semibold text-foreground mb-1">Import from Stash</div>
                    <div className="text-xs text-secondary">Migrate your existing Stash database to Cove.</div>
                  </div>
                </button>
                <button
                  onClick={() => {
                    setSetupMode("backup");
                    setStep("backup-restore");
                  }}
                  className="flex flex-col items-center gap-3 p-6 bg-card border-2 border-border hover:border-accent rounded-xl transition-colors text-left"
                >
                  <RefreshCw className="w-8 h-8 text-accent" />
                  <div>
                    <div className="font-semibold text-foreground mb-1">Restore Cove Backup</div>
                    <div className="text-xs text-secondary">
                      Restore a Cove database backup, and optionally a config backup too.
                    </div>
                  </div>
                </button>
              </div>
              {error ? (
                <div
                  role="alert"
                  className="mt-4 rounded-lg border border-red-700/50 bg-red-900/20 p-3 text-sm text-red-300"
                >
                  {error}
                </div>
              ) : null}
              <div className="mt-6 flex justify-start">
                <button
                  onClick={() => setStep("welcome")}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
              </div>
            </div>
          )}

          {step === "backup-restore" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Restore from a Cove backup</h2>
              <p className="text-sm text-secondary mb-6">
                Choose a Cove database backup to restore. You can also provide a config backup if you want Cove paths
                and related settings restored too.
              </p>

              <div className="space-y-4">
                <div>
                  <label className="block text-sm font-medium text-foreground mb-2">Database backup path</label>
                  <div className="flex gap-2">
                    <div className="flex-1 flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2">
                      <Database className="w-4 h-4 text-muted flex-shrink-0" />
                      <input
                        type="text"
                        value={restoreBackupPath}
                        onChange={(e) => {
                          setRestoreBackupPath(e.target.value);
                          setError(null);
                        }}
                        placeholder="C:\\Backups\\cove-2026-05-01.db"
                        className="flex-1 bg-transparent outline-none text-sm text-foreground"
                      />
                    </div>
                    {latestBackupQuery.data && (
                      <button
                        onClick={() => setRestoreBackupPath(latestBackupQuery.data ?? "")}
                        className="px-4 py-2 text-sm bg-card border border-border hover:border-accent text-foreground rounded-lg transition-colors"
                      >
                        Use latest
                      </button>
                    )}
                  </div>
                  {latestBackupQuery.data ? (
                    <p className="mt-2 text-xs text-muted">Latest backup: {latestBackupQuery.data}</p>
                  ) : null}
                </div>

                <div>
                  <label className="block text-sm font-medium text-foreground mb-2">Config backup path</label>
                  <div className="flex gap-2">
                    <div className="flex-1 flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2">
                      <Settings className="w-4 h-4 text-muted flex-shrink-0" />
                      <input
                        type="text"
                        value={restoreConfigBackupPath}
                        onChange={(e) => {
                          setRestoreConfigBackupPath(e.target.value);
                          setError(null);
                        }}
                        placeholder="Optional: C:\\Backups\\cove-config-2026-05-01.json"
                        className="flex-1 bg-transparent outline-none text-sm text-foreground"
                      />
                    </div>
                    {latestConfigBackupQuery.data && (
                      <button
                        onClick={() => setRestoreConfigBackupPath(latestConfigBackupQuery.data ?? "")}
                        className="px-4 py-2 text-sm bg-card border border-border hover:border-accent text-foreground rounded-lg transition-colors"
                      >
                        Use latest
                      </button>
                    )}
                  </div>
                  <p className="mt-2 text-xs text-muted">
                    Optional. Add this when you want Cove paths and other config values restored alongside the database.
                  </p>
                  {latestConfigBackupQuery.data ? (
                    <p className="mt-1 text-xs text-muted">Latest config backup: {latestConfigBackupQuery.data}</p>
                  ) : null}
                </div>

                <label className="flex items-start gap-3 rounded-xl border border-border bg-card px-4 py-3 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={restoreConfirmed}
                    onChange={(e) => setRestoreConfirmed(e.target.checked)}
                    className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  <span>
                    <span className="block font-medium text-foreground">Replace the current Cove data</span>
                    <span className="block text-xs text-muted">
                      Restoring replaces the current library database. If you provide a config backup, that file is
                      restored too.
                    </span>
                  </span>
                </label>

                {error && (
                  <div className="bg-red-900/20 border border-red-700/50 rounded-lg p-3 text-sm text-red-300">
                    {error}
                  </div>
                )}
              </div>

              <div className="flex justify-between mt-6">
                <button
                  onClick={() => {
                    setStep("source");
                    setSetupMode(null);
                    setError(null);
                  }}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={() => {
                    setError(null);
                    backupRestoreMut.mutate();
                  }}
                  disabled={restoreBackupPath.trim() === "" || !restoreConfirmed || backupRestoreMut.isPending}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {backupRestoreMut.isPending ? (
                    <Loader2 className="w-4 h-4 animate-spin" />
                  ) : (
                    <RefreshCw className="w-4 h-4" />
                  )}
                  Restore & Continue
                </button>
              </div>
            </div>
          )}

          {step === "stash-config" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Import from Stash</h2>
              <p className="text-sm text-secondary mb-6">
                Enter the path to your Stash SQLite database file (usually{" "}
                <code className="text-xs bg-card px-1 py-0.5 rounded">~/.stash/stash-go.sqlite</code>).
              </p>
              <div className="space-y-4">
                <div className="flex gap-2">
                  <div className="flex-1 flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2">
                    <Database className="w-4 h-4 text-muted flex-shrink-0" />
                    <input
                      type="text"
                      value={stashDbPath}
                      onChange={(e) => {
                        setStashDbPath(e.target.value);
                        setStashPreview(null);
                      }}
                      placeholder="/path/to/stash-go.sqlite"
                      disabled={isStashImportActive}
                      className="flex-1 bg-transparent outline-none text-sm text-foreground"
                    />
                  </div>
                  <button
                    onClick={() => {
                      setError(null);
                      stashPreviewMut.mutate();
                    }}
                    disabled={stashDbPath.trim() === "" || stashPreviewMut.isPending || isStashImportActive}
                    className="px-4 py-2 text-sm bg-card border border-border hover:border-accent text-foreground rounded-lg disabled:opacity-50 transition-colors flex items-center gap-1.5"
                  >
                    {stashPreviewMut.isPending ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <RefreshCw className="w-4 h-4" />
                    )}
                    Preview
                  </button>
                </div>

                <details className="bg-card border border-border rounded-xl" open={false}>
                  <summary className="flex cursor-pointer items-center justify-between gap-2 px-4 py-3 text-sm font-medium text-foreground">
                    Optional import settings
                  </summary>
                  <div className="border-t border-border px-4 py-4 space-y-4">
                    <label className="flex items-start gap-3 text-sm text-secondary">
                      <input
                        type="checkbox"
                        checked={migrateGeneratedContent}
                        onChange={(e) => setMigrateGeneratedContent(e.target.checked)}
                        disabled={isStashImportActive}
                        className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                      />
                      <span>
                        <span className="block font-medium text-foreground">Migrate generated content</span>
                        <span className="block text-xs text-muted">
                          Copy Stash screenshots, previews, sprite sheets, and VTT files into Cove. Enabled by default.
                        </span>
                      </span>
                    </label>

                    <label className="block text-sm text-secondary">
                      <span className="block mb-1 font-medium text-foreground">Cove generated content path</span>
                      <input
                        type="text"
                        value={coveGeneratedPath}
                        onChange={(e) => setCoveGeneratedPath(e.target.value)}
                        placeholder={config.generatedPath ?? "D:\\Cove\\generated"}
                        disabled={isStashImportActive}
                        className="w-full bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground outline-none disabled:opacity-60"
                      />
                      <span className="block mt-1 text-xs text-muted">
                        This updates Cove's generated-assets destination before the import runs. Stash's source path is
                        still read from config.yml.
                      </span>
                    </label>

                    <div className="space-y-2 text-sm text-secondary">
                      <div className="flex items-center justify-between gap-3">
                        <span className="font-medium text-foreground">Stash path mappings</span>
                        <button
                          onClick={addPathMapping}
                          disabled={isStashImportActive}
                          className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-background px-2.5 py-1.5 text-xs text-secondary hover:text-foreground disabled:opacity-50"
                        >
                          <Plus className="h-3.5 w-3.5" />
                          Add
                        </button>
                      </div>
                      <div className="space-y-2">
                        {pathMappings.map((mapping, index) => (
                          <div key={index} className="grid gap-2 sm:grid-cols-[1fr_1fr_auto]">
                            <input
                              type="text"
                              value={mapping.source}
                              onChange={(e) => updatePathMapping(index, "source", e.target.value)}
                              placeholder="C:\\Content"
                              aria-label="Stash source path"
                              disabled={isStashImportActive}
                              className="min-w-0 bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground outline-none disabled:opacity-60"
                            />
                            <input
                              type="text"
                              value={mapping.target}
                              onChange={(e) => updatePathMapping(index, "target", e.target.value)}
                              placeholder="/media"
                              aria-label="Cove target path"
                              disabled={isStashImportActive}
                              className="min-w-0 bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground outline-none disabled:opacity-60"
                            />
                            <button
                              onClick={() => removePathMapping(index)}
                              disabled={isStashImportActive}
                              aria-label="Remove path mapping"
                              className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-border bg-background text-muted hover:text-foreground disabled:opacity-50"
                            >
                              <Trash2 className="h-4 w-4" />
                            </button>
                          </div>
                        ))}
                      </div>
                      <span className="block text-xs text-muted">
                        Map the paths stored in Stash to the paths Cove can access, such as a Docker mount at /media.
                      </span>
                    </div>
                  </div>
                </details>

                {stashPreview && (
                  <div className="bg-card border border-border rounded-xl p-4">
                    <h3 className="text-xs font-medium uppercase tracking-wide text-muted mb-3">Database Summary</h3>
                    <div className="grid grid-cols-3 gap-3">
                      {[
                        { label: "Videos", value: stashPreview.videos },
                        { label: "Images", value: stashPreview.images },
                        { label: "Galleries", value: stashPreview.galleries },
                        { label: "Performers", value: stashPreview.performers },
                        { label: "Tags", value: stashPreview.tags },
                        { label: "Studios", value: stashPreview.studios },
                      ].map(({ label, value }) => (
                        <div key={label} className="text-center">
                          <div className="text-2xl font-bold text-foreground">{value}</div>
                          <div className="text-xs text-muted">{label}</div>
                        </div>
                      ))}
                    </div>
                    {stashPreview.generatedContentFound ? (
                      <div className="mt-3 flex items-start gap-2 rounded-lg border border-green-700/50 bg-green-900/20 p-3 text-sm text-green-300">
                        <FolderCheck className="w-4 h-4 mt-0.5 flex-shrink-0" />
                        <div>
                          <div className="font-medium">Generated content folder found</div>
                          <div className="text-xs text-green-300/80 break-all">
                            Thumbnails, previews, sprites and other generated content will be migrated.
                            {stashPreview.generatedPath ? ` (${stashPreview.generatedPath})` : ""}
                          </div>
                        </div>
                      </div>
                    ) : (
                      <div className="mt-3 flex items-start gap-2 rounded-lg border border-amber-700/50 bg-amber-900/20 p-3 text-sm text-amber-300">
                        <AlertTriangle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                        <div>
                          <div className="font-medium">Generated content folder not found</div>
                          <div className="text-xs text-amber-300/80 break-all">
                            {stashPreview.generatedPath
                              ? `Cove could not access Stash's generated folder at ${stashPreview.generatedPath}. Generated content (thumbnails, previews, sprites) will not be migrated unless this folder is reachable.`
                              : "Cove could not locate Stash's generated folder from config.yml (expected next to the database). Generated content (thumbnails, previews, sprites) will not be migrated."}
                          </div>
                        </div>
                      </div>
                    )}
                  </div>
                )}

                {activeStashImportJob && <SetupImportProgressCard job={activeStashImportJob} />}

                {error && (
                  <div className="bg-red-900/20 border border-red-700/50 rounded-lg p-3 text-sm text-red-300">
                    {error}
                  </div>
                )}
              </div>

              <div className="flex justify-between mt-6">
                <button
                  onClick={() => {
                    setStep("source");
                    setSetupMode(null);
                    setStashPreview(null);
                    setError(null);
                  }}
                  disabled={isStashImportActive}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={() => {
                    setError(null);
                    stashImportMut.mutate();
                  }}
                  disabled={!stashPreview || stashImportMut.isPending || isStashImportActive}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {stashImportMut.isPending || isStashImportActive ? (
                    <>
                      <Loader2 className="w-4 h-4 animate-spin" /> Importing…
                    </>
                  ) : (
                    <>
                      Import <ChevronRight className="w-4 h-4" />
                    </>
                  )}
                </button>
              </div>
            </div>
          )}

          {step === "paths" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Library Paths</h2>
              <p className="text-sm text-secondary mb-6">
                Add the directories containing your media files. Cove will scan these directories for videos, images,
                and galleries.
              </p>

              <div className="space-y-3 mb-4">
                {paths.map((p, i) => (
                  <div key={i} className="space-y-2">
                    <div className="flex gap-2">
                      <div className="flex-1 flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2">
                        <FolderOpen className="w-4 h-4 text-muted flex-shrink-0" />
                        <input
                          type="text"
                          value={p.path}
                          onChange={(e) => updatePath(i, { path: e.target.value })}
                          placeholder="Enter directory path (e.g., /media/videos)"
                          className="flex-1 bg-transparent outline-none text-sm text-foreground"
                        />
                      </div>
                      {paths.length > 1 && (
                        <button
                          onClick={() => removePath(i)}
                          className="p-2 text-muted hover:text-red-400 transition-colors"
                        >
                          <Trash2 className="w-4 h-4" />
                        </button>
                      )}
                    </div>
                    <div className="flex flex-wrap gap-4 pl-8">
                      <label className="flex items-center gap-1.5 text-xs text-secondary">
                        <input
                          type="checkbox"
                          checked={p.excludeVideo}
                          onChange={(e) => updatePath(i, { excludeVideo: e.target.checked })}
                          className="h-3.5 w-3.5 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Exclude video
                      </label>
                      <label className="flex items-center gap-1.5 text-xs text-secondary">
                        <input
                          type="checkbox"
                          checked={p.excludeImage}
                          onChange={(e) => updatePath(i, { excludeImage: e.target.checked })}
                          className="h-3.5 w-3.5 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Exclude images
                      </label>
                      <label className="flex items-center gap-1.5 text-xs text-secondary">
                        <input
                          type="checkbox"
                          checked={p.excludeAudio}
                          onChange={(e) => updatePath(i, { excludeAudio: e.target.checked })}
                          className="h-3.5 w-3.5 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Exclude audio
                      </label>
                      <label className="flex items-center gap-1.5 text-xs text-secondary">
                        <input
                          type="checkbox"
                          checked={p.excludeText}
                          onChange={(e) => updatePath(i, { excludeText: e.target.checked })}
                          className="h-3.5 w-3.5 rounded border-border bg-card text-accent focus:ring-0"
                        />
                        Exclude texts
                      </label>
                    </div>
                  </div>
                ))}
              </div>

              <button
                onClick={addPath}
                className="flex items-center gap-1.5 text-sm text-accent hover:text-accent-hover transition-colors mb-6"
              >
                <Plus className="w-4 h-4" /> Add another path
              </button>

              <div className="flex justify-between">
                <button
                  onClick={() => {
                    setStep("source");
                    setSetupMode(null);
                  }}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={() => setStep("confirm")}
                  disabled={validPaths.length === 0}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Next <ChevronRight className="w-4 h-4" />
                </button>
              </div>
            </div>
          )}

          {step === "confirm" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Confirm Configuration</h2>
              <p className="text-sm text-secondary mb-6">
                Review your library paths before saving. You can always change these later in Settings.
              </p>

              <div className="bg-card border border-border rounded-xl p-4 mb-6">
                <h3 className="text-xs font-medium uppercase tracking-wide text-muted mb-3">Library Paths</h3>
                <div className="space-y-2">
                  {validPaths.map((p, i) => (
                    <div key={i} className="flex items-center gap-3">
                      <FolderOpen className="w-4 h-4 text-accent flex-shrink-0" />
                      <span className="text-sm text-foreground font-mono">{p.path}</span>
                      {(p.excludeVideo || p.excludeImage || p.excludeAudio || p.excludeText) && (
                        <span className="text-xs text-muted">
                          (excludes:{" "}
                          {[
                            p.excludeVideo && "video",
                            p.excludeImage && "images",
                            p.excludeAudio && "audio",
                            p.excludeText && "texts",
                          ]
                            .filter(Boolean)
                            .join(", ")}
                          )
                        </span>
                      )}
                    </div>
                  ))}
                </div>
              </div>

              {error && (
                <div className="bg-red-900/20 border border-red-700/50 rounded-lg p-3 mb-4 text-sm text-red-300">
                  {error}
                </div>
              )}

              <div className="flex justify-between">
                <button
                  onClick={() => {
                    setStep("paths");
                    setError(null);
                  }}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={handleConfirm}
                  disabled={saveMut.isPending}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg font-medium disabled:opacity-50 transition-colors"
                >
                  {saveMut.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Check className="w-4 h-4" />}
                  Save & Continue
                </button>
              </div>
            </div>
          )}

          {step === "owner" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Set the owner password</h2>
              <p className="text-sm text-secondary mb-6">
                {activeMode === "stash" && stashResult === null
                  ? "Create the Owner account before importing so ratings, favorites, and engagement data have an owner."
                  : "Create the Owner account now so Cove is ready if authentication is enabled later or the outside-IP failsafe requires sign-in."}
              </p>

              <div className="space-y-4">
                <div>
                  <label htmlFor="setup-owner-username" className="block text-sm font-medium text-foreground mb-2">
                    Owner username
                  </label>
                  <input
                    id="setup-owner-username"
                    type="text"
                    autoComplete="username"
                    value={ownerUsername}
                    onChange={(event) => setOwnerUsername(event.target.value)}
                    disabled={ownerMut.isPending}
                    className="w-full rounded-lg border border-border bg-card px-3 py-2 text-foreground outline-none focus:border-accent"
                  />
                </div>
                <div>
                  <label htmlFor="setup-owner-password" className="block text-sm font-medium text-foreground mb-2">
                    Owner password
                  </label>
                  <input
                    id="setup-owner-password"
                    type="password"
                    autoComplete="new-password"
                    value={ownerPassword}
                    onChange={(event) => setOwnerPassword(event.target.value)}
                    disabled={ownerMut.isPending}
                    className="w-full rounded-lg border border-border bg-card px-3 py-2 text-foreground outline-none focus:border-accent"
                  />
                </div>
                <div>
                  <label htmlFor="setup-owner-confirm" className="block text-sm font-medium text-foreground mb-2">
                    Confirm password
                  </label>
                  <input
                    id="setup-owner-confirm"
                    type="password"
                    autoComplete="new-password"
                    value={ownerConfirmPassword}
                    onChange={(event) => setOwnerConfirmPassword(event.target.value)}
                    disabled={ownerMut.isPending}
                    className="w-full rounded-lg border border-border bg-card px-3 py-2 text-foreground outline-none focus:border-accent"
                  />
                </div>
              </div>

              {error ? (
                <div
                  role="alert"
                  className="mt-4 rounded-lg border border-red-700/50 bg-red-900/20 p-3 text-sm text-red-300"
                >
                  {error}
                </div>
              ) : null}

              <div className="flex justify-between mt-6">
                <button
                  onClick={() => {
                    setError(null);
                    setStep(resolveOwnerBackStep(activeMode, stashResult !== null));
                  }}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={handleOwnerSubmit}
                  disabled={ownerMut.isPending || !ownerUsername.trim() || !ownerPassword || !ownerConfirmPassword}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-green-600 hover:bg-green-500 text-white rounded-lg font-medium disabled:opacity-50 transition-colors"
                >
                  {ownerMut.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Check className="w-4 h-4" />}
                  Save owner password
                </button>
              </div>
            </div>
          )}

          {step === "theme" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Pick an optional theme</h2>
              <p className="text-sm text-secondary mb-6">
                Choose the look you want to start with. You can change this later in Settings &gt; Extensions.
              </p>

              <div className="grid gap-4 sm:grid-cols-2">
                {themeOptions.map((theme) => {
                  const isSelected = (activeThemeId ?? "default") === theme.id;
                  const cssVariables = (theme.cssVariables ?? {}) as Record<string, string>;

                  return (
                    <button
                      key={theme.id}
                      onClick={() => setActiveTheme(theme.id === "default" ? "default" : theme.id)}
                      className={`rounded-2xl border p-4 text-left transition-colors ${isSelected ? "border-accent bg-accent/5 shadow-lg shadow-accent/10" : "border-border bg-card hover:border-accent/50"}`}
                    >
                      <ThemeMiniPreview cssVariables={cssVariables} />
                      <div className="mt-4 flex items-start justify-between gap-3">
                        <div>
                          <div className="font-semibold text-foreground">{theme.name}</div>
                          <div className="mt-1 text-xs text-secondary">
                            {theme.description ?? "Extension-provided theme."}
                          </div>
                        </div>
                        {isSelected ? (
                          <span className="rounded-full bg-accent px-2 py-0.5 text-[11px] font-medium text-white">
                            Selected
                          </span>
                        ) : null}
                      </div>
                    </button>
                  );
                })}
              </div>

              <div className="flex justify-between mt-6">
                {activeMode === "fresh" ? (
                  <button
                    onClick={() => setStep("confirm")}
                    className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                  >
                    <ChevronLeft className="w-4 h-4" /> Back
                  </button>
                ) : (
                  <div />
                )}
                <button
                  onClick={() => setStep("done")}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium transition-colors"
                >
                  Continue <ChevronRight className="w-4 h-4" />
                </button>
              </div>
            </div>
          )}

          {step === "done" && (
            <div className="p-8 text-center">
              <div className="w-16 h-16 bg-green-600/20 rounded-2xl flex items-center justify-center mx-auto mb-6">
                <Check className="w-8 h-8 text-green-400" />
              </div>
              <h2 className="text-2xl font-bold text-foreground mb-3">You're all set!</h2>
              {stashResult ? (
                <div className="mb-6">
                  <p className="text-secondary mb-4 max-w-md mx-auto">
                    Successfully imported your Stash library into Cove.
                  </p>
                  <div className="grid grid-cols-3 gap-3 max-w-sm mx-auto mb-2">
                    {[
                      { label: "Videos", value: stashResult.videos },
                      { label: "Images", value: stashResult.images },
                      { label: "Galleries", value: stashResult.galleries },
                      { label: "Performers", value: stashResult.performers },
                      { label: "Tags", value: stashResult.tags },
                      { label: "Studios", value: stashResult.studios },
                    ].map(({ label, value }) => (
                      <div key={label} className="bg-card border border-border rounded-lg p-2">
                        <div className="text-xl font-bold text-foreground">{value}</div>
                        <div className="text-xs text-muted">{label}</div>
                      </div>
                    ))}
                  </div>
                </div>
              ) : backupRestoreResult ? (
                <div className="mb-6 max-w-lg mx-auto text-left">
                  <p className="text-secondary mb-4 text-center">Restored your Cove library from backup.</p>
                  <div className="bg-card border border-border rounded-xl p-4 space-y-2 text-xs text-secondary">
                    <div>
                      <span className="font-medium text-foreground">Database backup:</span>{" "}
                      {backupRestoreResult.backupPath}
                    </div>
                    {backupRestoreResult.preRestoreBackupPath ? (
                      <div>
                        <span className="font-medium text-foreground">Pre-restore backup:</span>{" "}
                        {backupRestoreResult.preRestoreBackupPath}
                      </div>
                    ) : null}
                    {backupRestoreResult.configBackupPath ? (
                      <div>
                        <span className="font-medium text-foreground">Config backup:</span>{" "}
                        {backupRestoreResult.configBackupPath}
                      </div>
                    ) : (
                      <div>Config restore was skipped.</div>
                    )}
                  </div>
                </div>
              ) : (
                <p className="text-secondary mb-6 max-w-md mx-auto">
                  Your library paths have been configured. Run Scan to start indexing files, or jump straight into your
                  videos.
                </p>
              )}
              <p className="text-xs text-muted mb-6">
                You can add more paths, configure scrapers, set up MetadataServer connections, and change themes from
                Settings later.
              </p>
              <div className="flex flex-wrap justify-center gap-3">
                <button
                  onClick={() => handleFinish("videos")}
                  className="inline-flex items-center gap-2 px-6 py-3 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium transition-colors"
                >
                  Go to Videos <ChevronRight className="w-4 h-4" />
                </button>
                <button
                  onClick={() => {
                    setError(null);
                    scanMut.mutate();
                  }}
                  disabled={scanMut.isPending}
                  className="inline-flex items-center gap-2 px-6 py-3 bg-card border border-border text-secondary hover:text-foreground rounded-lg font-medium transition-colors"
                >
                  {scanMut.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
                  Scan
                </button>
                <button
                  onClick={() => handleFinish("settings")}
                  className="inline-flex items-center gap-2 px-6 py-3 bg-card border border-border text-secondary hover:text-foreground rounded-lg font-medium transition-colors"
                >
                  <Settings className="w-4 h-4" /> Open Settings
                </button>
              </div>
              {scanJobId ? (
                <p className="mt-4 text-xs text-green-300">
                  Scan started. You can stay here or open Videos while it runs.
                </p>
              ) : null}
              {error ? (
                <div className="mx-auto mt-4 max-w-md rounded-lg border border-red-700/50 bg-red-900/20 p-3 text-sm text-red-300">
                  {error}
                </div>
              ) : null}
            </div>
          )}
        </div>

        {/* Skip link */}
        {step !== "done" && (
          <div className="text-center mt-4">
            <button
              onClick={() => onComplete({ showTutorial: false })}
              className="text-xs text-muted hover:text-secondary transition-colors"
            >
              Skip setup for now
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

function formatJobDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return `${hours}h ${mins.toString().padStart(2, "0")}m`;
}

function SetupImportProgressCard({ job }: { job: JobInfo }) {
  const [now, setNow] = useState(Date.now());
  const progressHistory = useRef<{ time: number; progress: number }[]>([]);

  useEffect(() => {
    if (job.status !== "running") return;
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [job.status]);

  useEffect(() => {
    if (job.status === "running" && job.progress > 0) {
      const history = progressHistory.current;
      const currentTime = Date.now();
      history.push({ time: currentTime, progress: job.progress });

      const cutoff = currentTime - 30000;
      while (history.length > 0 && history[0].time < cutoff) history.shift();
    }
  }, [job.progress, job.status]);

  const progressPct = Math.round((job.progress ?? 0) * 100);
  const elapsedMs = now - new Date(job.startedAt).getTime();

  let etaMs: number | null = null;
  const history = progressHistory.current;
  if (history.length >= 2 && job.progress >= 0.01) {
    const first = history[0];
    const last = history[history.length - 1];
    const dt = last.time - first.time;
    const dp = last.progress - first.progress;
    if (dt > 1000 && dp > 0) {
      const rate = dp / dt;
      etaMs = (1.0 - last.progress) / rate;
    }
  }

  return (
    <div className="bg-card border border-border rounded-xl p-4">
      <div className="flex items-center gap-2">
        <span className="text-sm font-medium text-foreground">{job.description}</span>
        <span
          className={`text-xs px-1.5 py-0.5 rounded ${
            job.status === "running"
              ? "bg-green-600/20 text-green-300"
              : job.status === "pending"
                ? "bg-yellow-600/20 text-yellow-300"
                : job.status === "failed"
                  ? "bg-red-600/20 text-red-300"
                  : "bg-card text-muted"
          }`}
        >
          {job.status}
        </span>
      </div>
      <p className="text-xs text-muted mt-1 truncate">
        {job.subTask ?? (job.status === "pending" ? "Waiting for import job to start..." : "Preparing import...")}
      </p>
      {(job.status === "running" || job.status === "pending") && (
        <>
          <div className="mt-3 h-2 w-full rounded-full bg-surface overflow-hidden">
            <div
              className="h-full rounded-full bg-accent transition-all"
              style={{ width: `${Math.min(progressPct, 100)}%` }}
            />
          </div>
          <div className="flex items-center justify-between mt-1">
            <span className="text-xs text-muted">
              {progressPct}% · {formatJobDuration(elapsedMs)} elapsed
            </span>
            {etaMs != null && <span className="text-xs text-muted">~{formatJobDuration(etaMs)} remaining</span>}
          </div>
        </>
      )}
    </div>
  );
}
