import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Database, RefreshCw, Search, Trash2 } from "lucide-react";

import { aiData } from "../../api/client";
import type {
  AiDataKind,
  AiDataPurgeRequest,
  AiDataPurgeResult,
  AiDataSelector,
  AiDataSummaryItem,
} from "../../api/types";
import { SectionCard, SelectField, SettingsMetricCard, TextField } from "../../components/SettingsPrimitives";
import { useExtensions } from "../../extensions/ExtensionLoader";

const KIND_OPTIONS: Array<{ value: AiDataKind; label: string }> = [
  { value: "embedding", label: "Embeddings" },
  { value: "detection", label: "Detections" },
  { value: "segment", label: "Segments" },
  { value: "tagApplication", label: "Tag Provenance" },
  { value: "face", label: "Faces" },
];

const MODALITY_OPTIONS = ["visual", "audio", "face", "text", "other"];
const HOST_TYPE_OPTIONS = ["video", "image", "performer", "face", "segment", "audio"];

interface FilterDraft {
  sourceKey: string;
  sourceRunId: string;
  model: string;
  modality: string;
  hostType: string;
  hostId: string;
  kinds: AiDataKind[];
}

const EMPTY_FILTERS: FilterDraft = {
  sourceKey: "",
  sourceRunId: "",
  model: "",
  modality: "",
  hostType: "",
  hostId: "",
  kinds: [],
};

export function AiDataSettingsPanel() {
  const queryClient = useQueryClient();
  const { getSettingsPanelsForTab, resolveComponent } = useExtensions();
  const aiDataPanels = getSettingsPanelsForTab("ai-data");
  const [filters, setFilters] = useState<FilterDraft>(EMPTY_FILTERS);
  const [previewSelector, setPreviewSelector] = useState<AiDataSelector | null>(null);
  const [previewKey, setPreviewKey] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const selector = useMemo(() => buildSelector(filters), [filters]);
  const selectorKey = JSON.stringify(selector);

  const summaryQuery = useQuery({
    queryKey: ["ai-data", "summary", "overall"],
    queryFn: () => aiData.summary(),
  });

  const previewSummaryQuery = useQuery({
    queryKey: ["ai-data", "summary", "preview", previewKey],
    queryFn: () => aiData.summary(previewSelector ?? undefined),
    enabled: previewSelector !== null,
  });

  const previewPurgeQuery = useQuery({
    queryKey: ["ai-data", "purge-preview", previewKey],
    queryFn: () => aiData.purge(buildPurgeRequest(previewSelector!, true)),
    enabled: previewSelector !== null,
  });

  const purgeMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (payload: AiDataPurgeRequest) => aiData.purge(payload),
    onSuccess: async () => {
      setConfirmOpen(false);
      await queryClient.invalidateQueries();
      setPreviewSelector(selector);
      setPreviewKey(selectorKey);
    },
  });

  const overallSummary = summaryQuery.data;
  const previewSummary = previewSummaryQuery.data;
  const previewResult = previewPurgeQuery.data;
  const previewMatchesCurrent = previewKey === selectorKey;
  const activeSummary = previewSummary && previewMatchesCurrent ? previewSummary : overallSummary;
  const hasActiveFilters = selectorKey !== JSON.stringify({});
  const previewTotal = previewResult ? getPurgeTotal(previewResult) : 0;
  const canPurge = Boolean(
    previewResult &&
    previewMatchesCurrent &&
    previewTotal > 0 &&
    !previewPurgeQuery.isFetching &&
    !purgeMutation.isPending,
  );

  return (
    <div className="space-y-5">
      <PurgeConfirmDialog
        open={confirmOpen}
        previewResult={previewMatchesCurrent ? (previewResult ?? null) : null}
        previewTotal={previewMatchesCurrent ? previewTotal : 0}
        isPending={purgeMutation.isPending}
        error={
          purgeMutation.isError
            ? purgeMutation.error instanceof Error
              ? purgeMutation.error.message
              : "Purge failed."
            : null
        }
        onConfirm={() => {
          if (previewSelector) {
            purgeMutation.mutate(buildPurgeRequest(previewSelector, false));
          }
        }}
        onCancel={() => setConfirmOpen(false)}
      />

      <SectionCard
        title="AI Artifact Totals"
        description="Current counts across embeddings, detections, timeline segments, tag provenance, and face-owned AI state."
        actions={
          <button
            type="button"
            onClick={() => {
              queryClient.invalidateQueries({ queryKey: ["ai-data", "summary"] });
            }}
            className="inline-flex items-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-sm text-secondary transition hover:text-foreground"
          >
            <RefreshCw className={`h-4 w-4 ${summaryQuery.isFetching ? "animate-spin" : ""}`} />
            Refresh
          </button>
        }
      >
        <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
          {KIND_OPTIONS.map((option) => (
            <SettingsMetricCard
              key={option.value}
              label={option.label}
              value={(overallSummary?.totals?.[option.value] ?? 0).toLocaleString()}
            />
          ))}
        </div>
      </SectionCard>

      {aiDataPanels.map((panel) => {
        const Component = resolveComponent(panel.extensionId, panel.componentName);
        if (!Component) {
          return null;
        }

        return (
          <SectionCard
            key={panel.id}
            title={panel.label}
            description={`Provided by the ${panel.extensionId} extension.`}
          >
            <Component />
          </SectionCard>
        );
      })}

      <SectionCard
        title="Selector"
        description="Preview a selector before running a destructive purge. Leaving fields empty broadens the match."
        headerClassName="flex-col lg:flex-row lg:items-start"
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => {
                setPreviewSelector(selector);
                setPreviewKey(selectorKey);
              }}
              disabled={previewSummaryQuery.isFetching || previewPurgeQuery.isFetching}
              className="inline-flex items-center gap-2 rounded-xl bg-accent px-3 py-2 text-sm font-medium text-white transition hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              {previewSummaryQuery.isFetching || previewPurgeQuery.isFetching ? (
                <RefreshCw className="h-4 w-4 animate-spin" />
              ) : (
                <Search className="h-4 w-4" />
              )}
              {previewSummaryQuery.isFetching || previewPurgeQuery.isFetching ? "Previewing..." : "Preview"}
            </button>
            <button
              type="button"
              onClick={() => {
                setFilters(EMPTY_FILTERS);
                setPreviewSelector(null);
                setPreviewKey(null);
              }}
              className="inline-flex items-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-sm text-secondary transition hover:text-foreground"
            >
              <RefreshCw className="h-4 w-4" />
              Reset
            </button>
            <button
              type="button"
              onClick={() => setConfirmOpen(true)}
              disabled={!canPurge}
              className="inline-flex items-center gap-2 rounded-xl bg-red-600 px-3 py-2 text-sm font-medium text-white transition enabled:hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Trash2 className="h-4 w-4" />
              Purge
            </button>
          </div>
        }
      >
        <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          <TextField
            label="Source key"
            value={filters.sourceKey}
            onChange={(value) => setFilters((current) => ({ ...current, sourceKey: value }))}
            placeholder="ext:your.extension"
          />
          <TextField
            label="Source run id"
            value={filters.sourceRunId}
            onChange={(value) => setFilters((current) => ({ ...current, sourceRunId: value }))}
            placeholder="run-1234"
          />
          <TextField
            label="Model"
            value={filters.model}
            onChange={(value) => setFilters((current) => ({ ...current, model: value }))}
            placeholder="tagger-v1"
          />
          <SelectField
            label="Modality"
            value={filters.modality}
            onChange={(value) => setFilters((current) => ({ ...current, modality: value }))}
            options={buildAnyOptions(MODALITY_OPTIONS)}
          />
          <SelectField
            label="Host type"
            value={filters.hostType}
            onChange={(value) => setFilters((current) => ({ ...current, hostType: value }))}
            options={buildAnyOptions(HOST_TYPE_OPTIONS)}
          />
          <TextField
            label="Host id"
            value={filters.hostId}
            onChange={(value) => setFilters((current) => ({ ...current, hostId: value }))}
            placeholder="42"
          />
        </div>

        <div className="mt-4">
          <div className="mb-2 text-xs font-semibold uppercase tracking-[0.16em] text-muted">Kinds</div>
          <div className="flex flex-wrap gap-2">
            {KIND_OPTIONS.map((option) => {
              const selected = filters.kinds.includes(option.value);
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => {
                    setFilters((current) => ({
                      ...current,
                      kinds: selected
                        ? current.kinds.filter((kind) => kind !== option.value)
                        : [...current.kinds, option.value],
                    }));
                  }}
                  className={`rounded-full border px-3 py-1.5 text-sm transition ${selected ? "border-accent bg-accent/15 text-foreground" : "border-border bg-card text-secondary hover:text-foreground"}`}
                >
                  {option.label}
                </button>
              );
            })}
          </div>
        </div>

        {!previewMatchesCurrent && (previewSummary || previewResult) ? (
          <div className="mt-4 rounded-xl border border-amber-500/40 bg-amber-500/10 px-4 py-3 text-sm text-amber-100">
            Filters changed after the last preview. Run Preview again before purging.
          </div>
        ) : null}

        {previewMatchesCurrent && (previewSummaryQuery.isFetching || previewPurgeQuery.isFetching) ? (
          <div className="mt-4 rounded-xl border border-border bg-card p-4 text-sm text-secondary">
            Calculating dry-run preview...
          </div>
        ) : null}

        {previewPurgeQuery.isError && previewMatchesCurrent ? (
          <div className="mt-4 rounded-xl border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-100">
            {previewPurgeQuery.error instanceof Error ? previewPurgeQuery.error.message : "Preview failed."}
          </div>
        ) : null}

        {previewResult && previewMatchesCurrent ? (
          <div className="mt-4 rounded-xl border border-border bg-card p-4">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <Database className="h-4 w-4 text-accent" />
              Dry-run preview would remove {previewTotal.toLocaleString()} row(s)
            </div>
            <p className="mt-1 text-sm text-secondary">
              {hasActiveFilters
                ? "This preview is scoped to the current selector."
                : "No filters are set, so the preview spans all AI-managed artifacts."}
            </p>
            <PurgeKindCounts result={previewResult} />
            {previewTotal === 0 ? (
              <p className="mt-3 text-sm text-secondary">
                Nothing matches the current selector, so deletion stays disabled.
              </p>
            ) : (
              <p className="mt-3 text-sm text-secondary">Open Purge and type purge to enable the destructive action.</p>
            )}
          </div>
        ) : null}
      </SectionCard>

      <SectionCard
        title={previewSummary && previewMatchesCurrent ? "Preview Results" : "Summary Table"}
        description="Grouped by artifact kind, detail, provenance source, model, and host type."
      >
        {summaryQuery.isLoading || previewSummaryQuery.isFetching ? (
          <div className="mt-6 flex items-center justify-center py-10 text-secondary">
            <RefreshCw className="mr-2 h-4 w-4 animate-spin" />
            Loading AI data summary...
          </div>
        ) : activeSummary && activeSummary.items.length > 0 ? (
          <div className="mt-4 overflow-x-auto">
            <table className="min-w-full text-left text-sm">
              <thead className="text-xs uppercase tracking-[0.12em] text-muted">
                <tr>
                  <th className="px-3 py-2">Kind</th>
                  <th className="px-3 py-2">Detail</th>
                  <th className="px-3 py-2">Source</th>
                  <th className="px-3 py-2">Run</th>
                  <th className="px-3 py-2">Model</th>
                  <th className="px-3 py-2">Host</th>
                  <th className="px-3 py-2 text-right">Count</th>
                </tr>
              </thead>
              <tbody>
                {activeSummary.items.map((item: AiDataSummaryItem) => (
                  <tr key={buildRowKey(item)} className="border-t border-border/70 text-secondary">
                    <td className="px-3 py-2 font-medium text-foreground">{formatKind(item.kind)}</td>
                    <td className="px-3 py-2">{item.detail ?? "-"}</td>
                    <td className="px-3 py-2 break-all">{item.sourceKey}</td>
                    <td className="px-3 py-2 break-all">{item.sourceRunId ?? "-"}</td>
                    <td className="px-3 py-2 break-all">{item.model ?? "-"}</td>
                    <td className="px-3 py-2">{formatKind(item.hostType)}</td>
                    <td className="px-3 py-2 text-right font-medium text-foreground">{item.count.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="mt-6 rounded-xl border border-dashed border-border px-4 py-10 text-center text-secondary">
            No AI artifacts matched the current selector.
          </div>
        )}
      </SectionCard>
    </div>
  );
}

function buildSelector(filters: FilterDraft): AiDataSelector {
  const sourceKey = filters.sourceKey.trim();
  const sourceRunId = filters.sourceRunId.trim();
  const model = filters.model.trim();
  const modality = filters.modality.trim();
  const hostType = filters.hostType.trim();
  const hostId = Number.parseInt(filters.hostId, 10);

  return {
    sourceKey: sourceKey || undefined,
    sourceRunId: sourceRunId || undefined,
    model: model || undefined,
    modality: modality || undefined,
    hostType: hostType || undefined,
    hostId: Number.isFinite(hostId) ? hostId : undefined,
    kinds: filters.kinds.length > 0 ? filters.kinds : undefined,
  };
}

function buildPurgeRequest(selector: AiDataSelector, dryRun: boolean): AiDataPurgeRequest {
  return {
    ...selector,
    dryRun,
  };
}

function getPurgeTotal(result: AiDataPurgeResult) {
  return Object.values(result.removedCounts).reduce((sum, count) => sum + count, 0);
}

function PurgeKindCounts({ result }: { result: AiDataPurgeResult }) {
  return (
    <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
      {KIND_OPTIONS.map((option) => (
        <SettingsMetricCard
          key={option.value}
          label={option.label}
          value={(result.removedCounts[option.value] ?? 0).toLocaleString()}
          valueClassName="text-xl"
        />
      ))}
    </div>
  );
}

function PurgeConfirmDialog({
  open,
  previewResult,
  previewTotal,
  isPending,
  error,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  previewResult: AiDataPurgeResult | null;
  previewTotal: number;
  isPending: boolean;
  error: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState("");

  useEffect(() => {
    if (!open) {
      setValue("");
    }
  }, [open]);

  if (!open) {
    return null;
  }

  const canConfirm = value === "purge" && !isPending && previewResult !== null && previewTotal > 0;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/60" onClick={onCancel} />
      <div className="relative mx-4 w-full max-w-2xl rounded-lg border border-border bg-surface p-6 shadow-xl">
        <h3 className="text-lg font-semibold text-foreground">Purge AI Data</h3>
        <p className="mt-2 text-sm text-secondary">
          This permanently deletes the AI artifacts from the current dry-run preview.
        </p>

        {previewResult ? (
          <div className="mt-4 rounded-xl border border-red-500/30 bg-red-500/10 p-4">
            <div className="text-sm font-medium text-foreground">
              This delete will remove {previewTotal.toLocaleString()} row(s).
            </div>
            <PurgeKindCounts result={previewResult} />
          </div>
        ) : (
          <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
            Run Preview before opening the destructive confirm.
          </div>
        )}

        <div className="mt-4 flex flex-col gap-2">
          <p className="text-sm font-medium text-red-300">
            Final confirmation: type purge to enable permanent deletion.
          </p>
          <input
            type="text"
            value={value}
            onChange={(event) => setValue(event.target.value)}
            placeholder="Type purge"
            className="w-48 rounded border border-red-800 bg-card px-3 py-1.5 text-sm text-foreground focus:border-red-500 focus:outline-none"
          />
          {error ? <p className="text-xs text-red-400">{error}</p> : null}
        </div>

        <div className="mt-6 flex justify-end gap-3">
          <button
            type="button"
            onClick={onCancel}
            className="px-4 py-2 text-sm text-secondary transition-colors hover:text-white"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={!canConfirm}
            className="flex items-center gap-1.5 rounded-md bg-red-700 px-4 py-2 text-sm text-white transition-colors hover:bg-red-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? (
              <span className="inline-block h-3 w-3 animate-spin rounded-full border border-white border-t-transparent" />
            ) : null}
            Delete AI Data
          </button>
        </div>
      </div>
    </div>
  );
}

function buildAnyOptions(options: readonly string[]) {
  return [{ value: "", label: "Any" }, ...options.map((option) => ({ value: option, label: formatKind(option) }))];
}

function formatKind(value: string) {
  return value
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .split(/[^a-zA-Z0-9]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function buildRowKey(item: AiDataSummaryItem) {
  return [item.kind, item.detail ?? "", item.sourceKey, item.sourceRunId ?? "", item.model ?? "", item.hostType].join(
    "::",
  );
}
