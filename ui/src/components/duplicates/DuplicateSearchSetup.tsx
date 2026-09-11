import { useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Crown, Eye, FileCheck2, Fingerprint, FolderMinus, FolderPlus, Loader2, Search, Type, X } from "lucide-react";
import { metadata } from "../../api/client";
import type { DuplicateMatchType } from "../../api/types";
import { LibraryFolderTree } from "../LibraryFolderTree";
import { DuplicateDialog } from "./DuplicateDialog";
import { KeeperRulesEditor, describeRules } from "./KeeperRulesEditor";
import { ACCURACY_PRESETS, MATCH_METHODS, type SearchPreferences } from "./duplicateModel";

const METHOD_ICONS: Record<DuplicateMatchType, ReactNode> = {
  phash: <Eye className="h-5 w-5" />,
  fingerprint: <Fingerprint className="h-5 w-5" />,
  title: <Type className="h-5 w-5" />,
  remoteId: <FileCheck2 className="h-5 w-5" />,
};

export function DuplicateSearchSetup({
  preferences,
  onChange,
  onStart,
  onCancel,
  isStarting,
  canRun,
  canReadFiles,
  error,
}: {
  preferences: SearchPreferences;
  onChange: (next: SearchPreferences) => void;
  onStart: () => void;
  onCancel?: () => void;
  isStarting: boolean;
  canRun: boolean;
  canReadFiles: boolean;
  error?: string | null;
}) {
  const [folderDialog, setFolderDialog] = useState<"include" | "exclude" | null>(null);
  const [rulesOpen, setRulesOpen] = useState(false);
  const update = (patch: Partial<SearchPreferences>) => onChange({ ...preferences, ...patch });
  const method = MATCH_METHODS.find((item) => item.value === preferences.matchType) ?? MATCH_METHODS[0];
  const customDistance = !ACCURACY_PRESETS.some((preset) => preset.distance === preferences.distance);
  const pathRulesMissingValues = preferences.keeperRules.some(
    (rule) => (rule.type === "path" || rule.type === "codec") && !rule.values?.length,
  );

  return (
    <section className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="space-y-6 p-5">
        <div>
          <SectionLabel step={1} title="How should videos be compared?" />
          <div className="mt-3 grid gap-2 sm:grid-cols-2 xl:grid-cols-4" role="radiogroup" aria-label="Match method">
            {MATCH_METHODS.map((option) => {
              const active = option.value === preferences.matchType;
              return (
                <button
                  key={option.value}
                  type="button"
                  role="radio"
                  aria-checked={active}
                  onClick={() => update({ matchType: option.value })}
                  className={`group flex items-start gap-3 rounded-lg border px-3 py-3 text-left transition-colors ${
                    active
                      ? "border-accent bg-accent/10 ring-1 ring-accent/40"
                      : "border-border bg-surface/60 hover:border-accent/50 hover:bg-surface"
                  }`}
                >
                  <span
                    className={`mt-0.5 rounded-md p-1.5 ${active ? "bg-accent text-white" : "bg-card text-secondary group-hover:text-foreground"}`}
                  >
                    {METHOD_ICONS[option.value]}
                  </span>
                  <span className="min-w-0">
                    <span className="block text-sm font-semibold text-foreground">{option.label}</span>
                    <span className="block text-xs text-muted">{option.description}</span>
                  </span>
                </button>
              );
            })}
          </div>
          <p className="mt-2 text-sm text-secondary">{method.detail}</p>

          {preferences.matchType === "phash" ? (
            <div className="mt-4 grid gap-4 rounded-lg border border-border/70 bg-surface/40 p-4 lg:grid-cols-[1fr_auto]">
              <div>
                <div className="mb-2 flex items-baseline justify-between gap-3">
                  <span className="text-sm font-medium text-foreground">Accuracy</span>
                  <span className="text-xs text-muted">
                    {customDistance
                      ? `Custom: up to ${preferences.distance} differing bits`
                      : ACCURACY_PRESETS.find((preset) => preset.distance === preferences.distance)?.hint}
                  </span>
                </div>
                <div className="flex flex-wrap gap-1 rounded-lg bg-card p-1" role="radiogroup" aria-label="Accuracy">
                  {ACCURACY_PRESETS.map((preset) => (
                    <button
                      key={preset.label}
                      type="button"
                      role="radio"
                      aria-checked={preferences.distance === preset.distance}
                      onClick={() => update({ distance: preset.distance })}
                      className={`flex-1 rounded-md px-3 py-1.5 text-sm transition-colors ${
                        preferences.distance === preset.distance
                          ? "bg-accent text-white shadow"
                          : "text-secondary hover:bg-surface hover:text-foreground"
                      }`}
                    >
                      {preset.label}
                    </button>
                  ))}
                </div>
                <details className="mt-2 text-xs text-muted">
                  <summary className="cursor-pointer select-none hover:text-secondary">Fine-tune distance</summary>
                  <div className="mt-2 flex items-center gap-3">
                    <input
                      type="range"
                      min={0}
                      max={16}
                      value={preferences.distance}
                      onChange={(event) => update({ distance: Number(event.target.value) })}
                      className="w-48 accent-accent"
                      aria-label="pHash distance"
                    />
                    <span className="tabular-nums text-secondary">{preferences.distance} bits</span>
                  </div>
                </details>
              </div>
              <label className="block text-sm lg:w-56">
                <span className="mb-2 block font-medium text-foreground">Durations may differ by</span>
                <span className="flex items-center gap-2">
                  <input
                    type="number"
                    min={0}
                    max={3600}
                    value={preferences.durationDiff}
                    onChange={(event) => update({ durationDiff: Math.max(0, Number(event.target.value) || 0) })}
                    className="w-24 rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
                  />
                  <span className="text-secondary">seconds</span>
                </span>
              </label>
            </div>
          ) : null}
        </div>

        <div>
          <SectionLabel step={2} title="Where should Cove look?" />
          <div className="mt-3 flex flex-wrap items-start gap-x-6 gap-y-3">
            <div className="min-w-0 flex-1 space-y-2">
              <FolderChips
                label="Only in"
                empty="Whole library"
                paths={preferences.includePaths}
                tone="include"
                onRemove={(path) => update({ includePaths: preferences.includePaths.filter((item) => item !== path) })}
              />
              {preferences.excludePaths.length > 0 ? (
                <FolderChips
                  label="Except"
                  paths={preferences.excludePaths}
                  tone="exclude"
                  onRemove={(path) =>
                    update({ excludePaths: preferences.excludePaths.filter((item) => item !== path) })
                  }
                />
              ) : null}
              {canReadFiles ? (
                <div className="flex flex-wrap gap-2 pt-1">
                  <button
                    type="button"
                    onClick={() => setFolderDialog("include")}
                    className="inline-flex items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground"
                  >
                    <FolderPlus className="h-4 w-4" />
                    Limit to folders
                  </button>
                  <button
                    type="button"
                    onClick={() => setFolderDialog("exclude")}
                    className="inline-flex items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground"
                  >
                    <FolderMinus className="h-4 w-4" />
                    Exclude folders
                  </button>
                </div>
              ) : (
                <p className="text-xs text-muted">Folder scopes require permission to read file paths.</p>
              )}
            </div>
            <label className="text-sm">
              <span className="mb-1.5 block text-secondary">Skip videos shorter than</span>
              <span className="flex items-center gap-2">
                <input
                  type="number"
                  min={0}
                  value={preferences.minimumDuration}
                  onChange={(event) => update({ minimumDuration: Math.max(0, Number(event.target.value) || 0) })}
                  className="w-24 rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
                />
                <span className="text-secondary">seconds</span>
              </span>
            </label>
          </div>
        </div>

        <div>
          <SectionLabel step={3} title="Which copy should be kept?" />
          <div className="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border/70 bg-surface/40 px-4 py-3">
            <div className="flex min-w-0 items-center gap-3">
              <Crown className="h-5 w-5 shrink-0 text-amber-400" />
              <div className="min-w-0 text-sm">
                <div className="text-foreground">
                  Cove pre-selects the best copy in every group. You review everything before anything is removed.
                </div>
                <div className="truncate text-muted">
                  Best by: <span className="text-secondary">{describeRules(preferences.keeperRules)}</span>
                </div>
              </div>
            </div>
            <button
              type="button"
              onClick={() => setRulesOpen(true)}
              className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground"
            >
              Customize rules
            </button>
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border bg-surface/40 px-5 py-3">
        <p className="text-xs text-muted">Settings are remembered for your next search.</p>
        <div className="flex items-center gap-2">
          {error ? <span className="text-sm text-red-300">{error}</span> : null}
          {onCancel ? (
            <button type="button" onClick={onCancel} className="px-3 py-2 text-sm text-secondary hover:text-foreground">
              Cancel
            </button>
          ) : null}
          <button
            type="button"
            onClick={onStart}
            disabled={!canRun || isStarting || pathRulesMissingValues}
            title={
              !canRun
                ? "You do not have permission to run jobs"
                : pathRulesMissingValues
                  ? "A codec or folder rule has no preferences"
                  : undefined
            }
            className="inline-flex items-center gap-2 rounded-lg bg-accent px-5 py-2 text-sm font-semibold text-white shadow hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isStarting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Find duplicates
          </button>
        </div>
      </div>

      <FolderScopeDialog
        mode={folderDialog}
        selected={folderDialog === "exclude" ? preferences.excludePaths : preferences.includePaths}
        onClose={() => setFolderDialog(null)}
        onChange={(paths) => update(folderDialog === "exclude" ? { excludePaths: paths } : { includePaths: paths })}
      />
      <DuplicateDialog
        open={rulesOpen}
        onClose={() => setRulesOpen(false)}
        title="Keeper rules"
        subtitle="Rules run top to bottom. The first rule where the copies differ picks the keeper."
        footer={
          <button
            type="button"
            data-autofocus
            onClick={() => setRulesOpen(false)}
            className="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover"
          >
            Done
          </button>
        }
      >
        <KeeperRulesEditor
          rules={preferences.keeperRules}
          onChange={(keeperRules) => update({ keeperRules })}
          canReadFiles={canReadFiles}
        />
      </DuplicateDialog>
    </section>
  );
}

function SectionLabel({ step, title }: { step: number; title: string }) {
  return (
    <h2 className="flex items-center gap-2 text-sm font-semibold text-foreground">
      <span className="flex h-5 w-5 items-center justify-center rounded-full bg-accent/15 text-[11px] text-accent">
        {step}
      </span>
      {title}
    </h2>
  );
}

function FolderChips({
  label,
  empty,
  paths,
  tone,
  onRemove,
}: {
  label: string;
  empty?: string;
  paths: string[];
  tone: "include" | "exclude";
  onRemove: (path: string) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-1.5 text-sm">
      <span className="w-14 shrink-0 text-muted">{label}</span>
      {paths.length === 0 ? <span className="text-foreground">{empty}</span> : null}
      {paths.map((path) => (
        <span
          key={path}
          title={path}
          className={`inline-flex max-w-full items-center gap-1 rounded-full border py-0.5 pl-2.5 pr-1 text-xs ${
            tone === "include"
              ? "border-accent/40 bg-accent/10 text-foreground"
              : "border-red-500/40 bg-red-500/10 text-red-200"
          }`}
        >
          <span className="max-w-[22rem] truncate">{path}</span>
          <button
            type="button"
            aria-label={`Remove ${path}`}
            onClick={() => onRemove(path)}
            className="rounded-full p-0.5 opacity-70 hover:bg-black/20 hover:opacity-100"
          >
            <X className="h-3 w-3" />
          </button>
        </span>
      ))}
    </div>
  );
}

function FolderScopeDialog({
  mode,
  selected,
  onClose,
  onChange,
}: {
  mode: "include" | "exclude" | null;
  selected: string[];
  onClose: () => void;
  onChange: (paths: string[]) => void;
}) {
  const rootsQuery = useQuery({
    queryKey: ["library-folders", "roots", false],
    queryFn: () => metadata.libraryFolders(undefined, false),
    enabled: mode != null,
    retry: false,
  });
  return (
    <DuplicateDialog
      open={mode != null}
      onClose={onClose}
      title={mode === "exclude" ? "Exclude folders" : "Limit the search to folders"}
      subtitle={
        mode === "exclude"
          ? "Videos whose files are all inside these folders are skipped."
          : "Only videos with a file inside one of these folders are compared."
      }
      footer={
        <button
          type="button"
          data-autofocus
          onClick={onClose}
          className="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover"
        >
          Done
        </button>
      }
    >
      {rootsQuery.isLoading ? (
        <p className="flex items-center gap-2 text-sm text-muted">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading library folders…
        </p>
      ) : rootsQuery.isError ? (
        <p className="text-sm text-red-300">
          {rootsQuery.error instanceof Error ? rootsQuery.error.message : "Library folders could not be loaded."}
        </p>
      ) : (
        <LibraryFolderTree
          roots={rootsQuery.data ?? []}
          selected={selected}
          onToggle={(path, checked) =>
            onChange(checked ? [...new Set([...selected, path])] : selected.filter((item) => item !== path))
          }
          probeChildren={false}
          emptyHint="No library folders are configured."
        />
      )}
    </DuplicateDialog>
  );
}
