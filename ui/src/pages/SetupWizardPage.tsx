import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { system, stashMigration } from "../api/client";
import type { StashPreviewResult, StashImportResult } from "../api/client";
import type { CoveConfig, CovePathConfig } from "../api/types";
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
} from "lucide-react";

interface Props {
  config: CoveConfig;
  onComplete: () => void;
}

type Step = "welcome" | "source" | "paths" | "confirm" | "stash-config" | "done";

export function SetupWizardPage({ config, onComplete }: Props) {
  const [step, setStep] = useState<Step>("welcome");
  const [paths, setPaths] = useState<CovePathConfig[]>(
    config.covePaths.length > 0
      ? config.covePaths
      : [{ path: "", excludeVideo: false, excludeImage: false, excludeAudio: false }]
  );
  const [error, setError] = useState<string | null>(null);
  const [stashDbPath, setStashDbPath] = useState("");
  const [stashPreview, setStashPreview] = useState<StashPreviewResult | null>(null);
  const [stashResult, setStashResult] = useState<StashImportResult | null>(null);
  const queryClient = useQueryClient();

  const stashPreviewMut = useMutation({
    mutationFn: () => stashMigration.preview(stashDbPath),
    onSuccess: (data) => setStashPreview(data),
    onError: (err: Error) => setError(err.message),
  });
  const stashImportMut = useMutation({
    mutationFn: () => stashMigration.import(stashDbPath),
    onSuccess: (data) => { setStashResult(data); setStep("done"); queryClient.invalidateQueries(); },
    onError: (err: Error) => setError(err.message),
  });

  const saveMut = useMutation({
    mutationFn: (cfg: CoveConfig) => system.saveConfig(cfg),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["system-config"] });
      setStep("done");
    },
    onError: (err: Error) => setError(err.message),
  });

  const addPath = () => {
    setPaths([...paths, { path: "", excludeVideo: false, excludeImage: false, excludeAudio: false }]);
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

  return (
    <div className="min-h-screen bg-background flex items-center justify-center p-4">
      <div className="w-full max-w-2xl">
        {/* Progress indicator */}
        {(() => {
          const isStashPath = ["stash-config"].includes(step) || stashResult !== null;
          const freshSteps: Step[] = ["welcome", "source", "paths", "confirm", "done"];
          const stashSteps: Step[] = ["welcome", "source", "stash-config", "done"];
          const stepList = isStashPath ? stashSteps : freshSteps;
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
                Cove is a self-hosted organizer for your media library. Let's get set up
                by configuring your library.
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
                Start with a fresh Cove library, or import your existing Stash library.
              </p>
              <div className="grid grid-cols-2 gap-4">
                <button
                  onClick={() => setStep("paths")}
                  className="flex flex-col items-center gap-3 p-6 bg-card border-2 border-border hover:border-accent rounded-xl transition-colors text-left"
                >
                  <FolderOpen className="w-8 h-8 text-accent" />
                  <div>
                    <div className="font-semibold text-foreground mb-1">Start Fresh</div>
                    <div className="text-xs text-secondary">Configure library paths and scan from scratch.</div>
                  </div>
                </button>
                <button
                  onClick={() => setStep("stash-config")}
                  className="flex flex-col items-center gap-3 p-6 bg-card border-2 border-border hover:border-accent rounded-xl transition-colors text-left"
                >
                  <Database className="w-8 h-8 text-accent" />
                  <div>
                    <div className="font-semibold text-foreground mb-1">Import from Stash</div>
                    <div className="text-xs text-secondary">Migrate your existing Stash database to Cove.</div>
                  </div>
                </button>
              </div>
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

          {step === "stash-config" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Import from Stash</h2>
              <p className="text-sm text-secondary mb-6">
                Enter the path to your Stash SQLite database file (usually <code className="text-xs bg-card px-1 py-0.5 rounded">~/.stash/stash-go.sqlite</code>).
              </p>
              <div className="space-y-4">
                <div className="flex gap-2">
                  <div className="flex-1 flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2">
                    <Database className="w-4 h-4 text-muted flex-shrink-0" />
                    <input
                      type="text"
                      value={stashDbPath}
                      onChange={(e) => { setStashDbPath(e.target.value); setStashPreview(null); }}
                      placeholder="/path/to/stash-go.sqlite"
                      className="flex-1 bg-transparent outline-none text-sm text-foreground"
                    />
                  </div>
                  <button
                    onClick={() => { setError(null); stashPreviewMut.mutate(); }}
                    disabled={stashDbPath.trim() === "" || stashPreviewMut.isPending}
                    className="px-4 py-2 text-sm bg-card border border-border hover:border-accent text-foreground rounded-lg disabled:opacity-50 transition-colors flex items-center gap-1.5"
                  >
                    {stashPreviewMut.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
                    Preview
                  </button>
                </div>

                {stashPreview && (
                  <div className="bg-card border border-border rounded-xl p-4">
                    <h3 className="text-xs font-medium uppercase tracking-wide text-muted mb-3">Database Summary</h3>
                    <div className="grid grid-cols-3 gap-3">
                      {([
                        { label: "Scenes", value: stashPreview.scenes },
                        { label: "Images", value: stashPreview.images },
                        { label: "Galleries", value: stashPreview.galleries },
                        { label: "Performers", value: stashPreview.performers },
                        { label: "Tags", value: stashPreview.tags },
                        { label: "Studios", value: stashPreview.studios },
                      ]).map(({ label, value }) => (
                        <div key={label} className="text-center">
                          <div className="text-2xl font-bold text-foreground">{value}</div>
                          <div className="text-xs text-muted">{label}</div>
                        </div>
                      ))}
                    </div>
                  </div>
                )}

                {error && (
                  <div className="bg-red-900/20 border border-red-700/50 rounded-lg p-3 text-sm text-red-300">{error}</div>
                )}
              </div>

              <div className="flex justify-between mt-6">
                <button
                  onClick={() => { setStep("source"); setStashPreview(null); setError(null); }}
                  className="flex items-center gap-1.5 px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
                >
                  <ChevronLeft className="w-4 h-4" /> Back
                </button>
                <button
                  onClick={() => { setError(null); stashImportMut.mutate(); }}
                  disabled={!stashPreview || stashImportMut.isPending}
                  className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {stashImportMut.isPending ? (
                    <><Loader2 className="w-4 h-4 animate-spin" /> Importing…</>
                  ) : (
                    <>Import <ChevronRight className="w-4 h-4" /></>
                  )}
                </button>
              </div>
            </div>
          )}

          {step === "paths" && (
            <div className="p-8">
              <h2 className="text-xl font-bold text-foreground mb-2">Library Paths</h2>
              <p className="text-sm text-secondary mb-6">
                Add the directories containing your media files. Cove will scan these
                directories for scenes, images, and galleries.
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
                    <div className="flex gap-4 pl-8">
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
                  onClick={() => setStep("source")}
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
                      {(p.excludeVideo || p.excludeImage) && (
                        <span className="text-xs text-muted">
                          (excludes: {[p.excludeVideo && "video", p.excludeImage && "images"].filter(Boolean).join(", ")})
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
                  onClick={() => { setStep("paths"); setError(null); }}
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
                    {([
                      { label: "Scenes", value: stashResult.scenes },
                      { label: "Images", value: stashResult.images },
                      { label: "Galleries", value: stashResult.galleries },
                      { label: "Performers", value: stashResult.performers },
                      { label: "Tags", value: stashResult.tags },
                      { label: "Studios", value: stashResult.studios },
                    ]).map(({ label, value }) => (
                      <div key={label} className="bg-card border border-border rounded-lg p-2">
                        <div className="text-xl font-bold text-foreground">{value}</div>
                        <div className="text-xs text-muted">{label}</div>
                      </div>
                    ))}
                  </div>
                </div>
              ) : (
                <p className="text-secondary mb-6 max-w-md mx-auto">
                  Your library paths have been configured. Head to Settings &gt; Tasks to start
                  scanning for content.
                </p>
              )}
              <p className="text-xs text-muted mb-6">
                You can add more paths, configure scrapers, and set up MetadataServer connections in Settings.
              </p>
              <div className="flex justify-center gap-3">
                <button
                  onClick={onComplete}
                  className="inline-flex items-center gap-2 px-6 py-3 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium transition-colors"
                >
                  Go to Dashboard <ChevronRight className="w-4 h-4" />
                </button>
                <button
                  onClick={() => { onComplete(); window.location.hash = "#/settings"; }}
                  className="inline-flex items-center gap-2 px-6 py-3 bg-card border border-border text-secondary hover:text-foreground rounded-lg font-medium transition-colors"
                >
                  <Settings className="w-4 h-4" /> Open Settings
                </button>
              </div>
            </div>
          )}
        </div>

        {/* Skip link */}
        {step !== "done" && (
          <div className="text-center mt-4">
            <button
              onClick={onComplete}
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
