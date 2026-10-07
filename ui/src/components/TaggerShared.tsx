import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  AlertCircle,
  Check,
  ChevronDown,
  CloudDownload,
  Eye,
  EyeOff,
  Loader2,
  RefreshCw,
  Settings2,
  Undo2,
  X,
} from "lucide-react";
import type { CollectionMode } from "./videoScrapeUtils";

// Reduce an endpoint to its registrable domain (last two labels, "www." dropped) so a remote id stored
// under a pack/source endpoint (e.g. api.theporndb.net) matches a configured server (theporndb.net).
// Mirrors the host-side EndpointsMatch behavior.
function registrableDomain(endpoint?: string | null): string {
  if (!endpoint) return "";
  let host = endpoint.trim();
  try {
    host = new URL(host.includes("://") ? host : `https://${host}`).host;
  } catch {
    host = host.replace(/^.*:\/\//, "").split("/")[0];
  }
  host = host.toLowerCase().replace(/^www\./, "");
  const labels = host.split(".").filter(Boolean);
  return labels.length <= 2 ? host : labels.slice(-2).join(".");
}

// "Refresh from <server>" buttons: one per remote id whose endpoint maps (by registrable domain) to a
// configured metadata server, so the tagger can rescrape from a known remote entry without a name search.
export function RemoteRefreshButtons({
  remoteIds,
  servers,
  busyEndpoint,
  onRefresh,
}: {
  remoteIds?: { endpoint: string; remoteId: string }[];
  servers: { endpoint: string; name?: string }[];
  busyEndpoint?: string | null;
  onRefresh: (endpoint: string, remoteId: string) => void;
}) {
  const serverByDomain = new Map(servers.map((server) => [registrableDomain(server.endpoint), server]));
  const matches = (remoteIds ?? [])
    .map((remote) => ({ remote, server: serverByDomain.get(registrableDomain(remote.endpoint)) }))
    .filter(
      (
        entry,
      ): entry is { remote: { endpoint: string; remoteId: string }; server: { endpoint: string; name?: string } } =>
        !!entry.server,
    );
  if (matches.length === 0) return null;

  return (
    <div className="mb-2 flex flex-wrap gap-1.5">
      {matches.map(({ remote, server }) => (
        <button
          key={`${remote.endpoint}-${remote.remoteId}`}
          onClick={() => onRefresh(remote.endpoint, remote.remoteId)}
          disabled={busyEndpoint === remote.endpoint}
          className="inline-flex items-center gap-1 rounded border border-border bg-surface px-2 py-1 text-xs text-secondary transition-colors hover:border-accent hover:text-foreground disabled:opacity-60"
          title={`Fetch the existing ${server.name || server.endpoint} entry (${remote.remoteId}) for this item`}
        >
          {busyEndpoint === remote.endpoint ? (
            <Loader2 className="h-3 w-3 animate-spin" />
          ) : (
            <RefreshCw className="h-3 w-3" />
          )}
          Refresh from {server.name || server.endpoint}
        </button>
      ))}
    </div>
  );
}

export type TaggerQueryMode = "auto" | "filename" | "dir" | "path" | "metadata";

export interface TaggerSourceOption {
  value: string;
  label: string;
}

export interface TaggerRunAllOption {
  value: string;
  label: string;
  description: string;
}

export const DEFAULT_TAGGER_DENYLIST = ["\\sXXX\\s", "1080p", "720p", "2160p", "4K", "KTR", "RARBG", "\\smp4\\s"];

const months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
const ddmmyyRegex = /\.(\d\d)\.(\d\d)\.(\d\d)\./;
const yyyymmddRegex = /(\d{4})[-.](\d{2})[-.](\d{2})/;
const mmddyyRegex = /(\d{2})[-.](\d{2})[-.](\d{4})/;
const ddMMyyRegex = new RegExp(`(\\d{1,2}).(${months.join("|")})\\.?.(\\d{4})`, "i");
const MMddyyRegex = new RegExp(`(${months.join("|")})\\.?.(\\d{1,2}),?.(\\d{4})`, "i");
const javcodeRegex = /([a-zA-Z|tT28|tT38]+-\d+[zZeE]?)/;

function handleSpecialQueryStrings(input: string): string {
  let output = input;
  const ddmmyy = output.match(ddmmyyRegex);
  if (ddmmyy) output = output.replace(ddmmyy[0], ` 20${ddmmyy[1]}-${ddmmyy[2]}-${ddmmyy[3]} `);
  const mmddyy = output.match(mmddyyRegex);
  if (mmddyy) output = output.replace(mmddyy[0], ` ${mmddyy[1]}-${mmddyy[2]}-${mmddyy[3]} `);
  const ddMMyy = output.match(ddMMyyRegex);
  if (ddMMyy) {
    const month = (months.indexOf(ddMMyy[2].toLowerCase()) + 1).toString().padStart(2, "0");
    output = output.replace(ddMMyy[0], ` ${ddMMyy[3]}-${month}-${ddMMyy[1].padStart(2, "0")} `);
  }
  const MMddyy = output.match(MMddyyRegex);
  if (MMddyy) {
    const month = (months.indexOf(MMddyy[1].toLowerCase()) + 1).toString().padStart(2, "0");
    output = output.replace(MMddyy[0], ` ${MMddyy[3]}-${month}-${MMddyy[2].padStart(2, "0")} `);
  }
  const yyyymmdd = output.search(yyyymmddRegex);
  if (yyyymmdd !== -1) {
    return (
      output.slice(0, yyyymmdd).replace(/-/g, " ") +
      output.slice(yyyymmdd, yyyymmdd + 10).replace(/\./g, "-") +
      output.slice(yyyymmdd + 10).replace(/-/g, " ")
    );
  }
  const javcodeIndex = output.search(javcodeRegex);
  if (javcodeIndex !== -1) {
    const javcodeLength = output.match(javcodeRegex)![1].length;
    return (
      output.slice(0, javcodeIndex).replace(/-/g, " ") +
      output.slice(javcodeIndex, javcodeIndex + javcodeLength) +
      output.slice(javcodeIndex + javcodeLength).replace(/-/g, " ")
    );
  }
  return output.replace(/-/g, " ");
}

export function cleanTaggerQueryString(input: string, denylist: string[]): string {
  let cleaned = input.replace(/[._]/g, " ");
  for (const pattern of denylist) {
    try {
      cleaned = cleaned.replace(new RegExp(pattern, "gi"), "");
    } catch {
      // Invalid denylist regexes are ignored so one bad entry does not break tagging.
    }
  }
  cleaned = handleSpecialQueryStrings(cleaned);
  return cleaned.replace(/ +/g, " ").trim();
}

/**
 * A `<details>` menu that also closes on a click outside it or on Escape, the way a menu is expected
 * to, instead of staying open until its summary is clicked again.
 */
export function DismissibleMenu({ className, children }: { className?: string; children: ReactNode }) {
  const ref = useRef<HTMLDetailsElement>(null);
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const close = () => element.removeAttribute("open");
    const onPointerDown = (event: PointerEvent) => {
      if (element.open && !element.contains(event.target as Node)) close();
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && element.open) close();
    };
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, []);
  return (
    <details ref={ref} className={className}>
      {children}
    </details>
  );
}

export function TaggerToolbar({
  sources,
  selectedSource,
  onSourceChange,
  showToggle,
  batchSearching,
  onCancelBatch,
  onRunAll,
  runAllOptions,
  runAllLabel = "Search all",
  showRunAll = true,
  countLabel,
  dismissed,
  applyAll,
  settingsOpen,
  onToggleSettings,
}: {
  sources: TaggerSourceOption[];
  selectedSource: string;
  onSourceChange: (value: string) => void;
  showToggle?: {
    value: boolean;
    onChange: (value: boolean) => void;
    enabledLabel: string;
    disabledLabel: string;
  };
  batchSearching: boolean;
  onCancelBatch: () => void;
  onRunAll: (option?: string) => void;
  runAllOptions?: TaggerRunAllOption[];
  runAllLabel?: string;
  showRunAll?: boolean;
  countLabel: string;
  /** Rows the user took off this pass, with a way back that does not need a page reload. */
  dismissed?: {
    count: number;
    onRestore: () => void;
  };
  /** Bulk apply for rows that already have a match, alongside the bulk search. */
  applyAll?: {
    onApply: () => void;
    onCancel: () => void;
    busy: boolean;
    /** How many rows would be applied; the button is disabled at zero. */
    count: number;
  };
  settingsOpen?: boolean;
  onToggleSettings?: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 bg-surface border-b border-border px-4 py-2">
      <div className="flex items-center gap-2">
        <label className="text-xs text-muted whitespace-nowrap">Source:</label>
        <select
          value={selectedSource}
          onChange={(event) => onSourceChange(event.target.value)}
          className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
        >
          {sources.map((source) => (
            <option key={source.value} value={source.value}>
              {source.label}
            </option>
          ))}
        </select>
      </div>

      <span className="ml-auto text-xs text-muted">{countLabel}</span>

      {dismissed && dismissed.count > 0 && (
        <button
          type="button"
          onClick={dismissed.onRestore}
          title="Put the videos dismissed in this search session back on the list"
          className="flex cursor-pointer items-center gap-1.5 rounded border border-border bg-input px-2 py-1 text-xs text-secondary hover:text-foreground"
        >
          <Undo2 className="w-3.5 h-3.5" />
          Restore {dismissed.count} dismissed
        </button>
      )}

      {showToggle && (
        <button
          type="button"
          onClick={() => showToggle.onChange(!showToggle.value)}
          aria-pressed={!showToggle.value}
          className="flex cursor-pointer items-center gap-1.5 rounded border border-border bg-input px-2 py-1 text-xs text-secondary hover:text-foreground"
        >
          {showToggle.value ? <Eye className="w-3.5 h-3.5" /> : <EyeOff className="w-3.5 h-3.5" />}
          {showToggle.value ? showToggle.enabledLabel : showToggle.disabledLabel}
        </button>
      )}

      {showRunAll &&
        (batchSearching ? (
          <button
            type="button"
            onClick={onCancelBatch}
            className="flex items-center gap-1.5 px-3 py-1 rounded text-xs font-medium bg-red-600 text-white hover:bg-red-500"
          >
            <X className="w-3.5 h-3.5" />
            Cancel
          </button>
        ) : (
          <div className="flex items-stretch">
            <button
              type="button"
              onClick={() => onRunAll()}
              className={`flex items-center gap-1.5 px-3 py-1 text-xs font-medium bg-accent text-white hover:bg-accent-hover ${runAllOptions?.length ? "rounded-l" : "rounded"}`}
            >
              <CloudDownload className="w-3.5 h-3.5" />
              {runAllLabel}
            </button>
            {runAllOptions?.length ? (
              <DismissibleMenu className="relative">
                <summary
                  role="button"
                  className="flex h-full list-none items-center rounded-r border-l border-white/20 bg-accent px-1.5 text-white hover:bg-accent-hover cursor-pointer"
                  aria-label="Choose search strategy"
                >
                  <ChevronDown className="w-3.5 h-3.5" />
                </summary>
                {/* Anchored to the right edge: the button now sits near the end of the toolbar, so a
                    left-anchored panel would run off the viewport. */}
                <div className="absolute right-0 z-30 mt-1 w-72 overflow-hidden rounded border border-border bg-card shadow-xl">
                  {runAllOptions.map((option) => (
                    <button
                      key={option.value}
                      type="button"
                      onClick={(event) => {
                        event.currentTarget.closest("details")?.removeAttribute("open");
                        onRunAll(option.value);
                      }}
                      className="block w-full px-3 py-2 text-left hover:bg-surface"
                    >
                      <span className="block text-xs font-medium text-foreground">{option.label}</span>
                      <span className="block text-[10px] text-muted">{option.description}</span>
                    </button>
                  ))}
                </div>
              </DismissibleMenu>
            ) : null}
          </div>
        ))}

      {applyAll &&
        (applyAll.busy ? (
          <button
            type="button"
            onClick={applyAll.onCancel}
            className="flex items-center gap-1.5 rounded bg-red-600 px-3 py-1 text-xs font-medium text-white hover:bg-red-500"
          >
            <X className="w-3.5 h-3.5" />
            Cancel
          </button>
        ) : (
          <button
            type="button"
            onClick={applyAll.onApply}
            disabled={applyAll.count === 0}
            title={
              applyAll.count === 0
                ? "No matched videos to apply"
                : `Apply the selected match on ${applyAll.count} video${applyAll.count === 1 ? "" : "s"}`
            }
            className="flex items-center gap-1.5 rounded bg-green-600 px-3 py-1 text-xs font-medium text-white hover:bg-green-500 disabled:opacity-50 disabled:hover:bg-green-600"
          >
            <Check className="w-3.5 h-3.5" />
            Apply all{applyAll.count > 0 ? ` (${applyAll.count})` : ""}
          </button>
        ))}

      {onToggleSettings && (
        <button
          type="button"
          onClick={onToggleSettings}
          title="Tagger settings"
          aria-label="Tagger settings"
          aria-expanded={settingsOpen}
          className={`flex cursor-pointer items-center rounded border px-1.5 py-1 ${
            settingsOpen
              ? "border-accent bg-input text-accent"
              : "border-border bg-input text-secondary hover:text-foreground"
          }`}
        >
          <Settings2 className="w-3.5 h-3.5" />
        </button>
      )}
    </div>
  );
}

export function TaggerSettingsPanel({
  children,
  denylist,
  onDenylistChange,
}: {
  children?: ReactNode;
  denylist?: string[];
  onDenylistChange?: (items: string[]) => void;
}) {
  const hasConfiguration = Boolean(children);
  const hasDenylist = Boolean(denylist && onDenylistChange);

  return (
    <div className="bg-card border-b border-border px-4 py-3 space-y-4">
      <div className={hasConfiguration && hasDenylist ? "grid grid-cols-1 lg:grid-cols-2 gap-6" : "space-y-3"}>
        {hasConfiguration && (
          <div className="space-y-3">
            <h3 className="text-sm font-bold text-foreground italic">Configuration</h3>
            {children}
          </div>
        )}
        {denylist && onDenylistChange && (
          <div className={hasConfiguration ? "space-y-2" : "max-w-3xl space-y-2"}>
            <h3 className="text-sm font-bold text-foreground italic">Denylist</h3>
            <DenylistEditor items={denylist} onChange={onDenylistChange} />
            <p className="text-[10px] text-muted">
              Denylist items are excluded from queries. They are case-insensitive regular expressions. Escape special
              characters with a backslash: <code className="text-pink-400">{`[\\.^$.|?*+()`}</code>
            </p>
          </div>
        )}
      </div>
    </div>
  );
}

export function DenylistEditor({ items, onChange }: { items: string[]; onChange: (items: string[]) => void }) {
  const [input, setInput] = useState("");

  const addItem = () => {
    const trimmed = input.trim();
    if (trimmed && !items.includes(trimmed)) {
      onChange([...items, trimmed]);
      setInput("");
    }
  };

  const removeItem = (index: number) => {
    onChange(items.filter((_, itemIndex) => itemIndex !== index));
  };

  return (
    <div className="space-y-2">
      <div className="flex gap-1.5">
        <input
          type="text"
          value={input}
          onChange={(event) => setInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              addItem();
            }
          }}
          className="flex-1 bg-input border border-border rounded px-2 py-1.5 text-xs text-foreground outline-none focus:border-accent font-mono"
        />
        <button
          type="button"
          onClick={addItem}
          disabled={!input.trim()}
          className="px-3 py-1.5 text-xs rounded border border-border bg-surface text-foreground hover:bg-card disabled:opacity-40"
        >
          Add
        </button>
      </div>
      <div className="flex flex-wrap gap-1.5">
        {items.map((item, index) => (
          <span
            key={`${item}-${index}`}
            className="inline-flex items-center gap-1 bg-surface text-foreground text-xs px-2 py-1 rounded border border-border font-mono"
          >
            {item}
            <button type="button" onClick={() => removeItem(index)} className="text-muted hover:text-red-400 ml-0.5">
              <X className="w-3 h-3" />
            </button>
          </span>
        ))}
      </div>
    </div>
  );
}

export function CompactScalarDecision({
  label,
  current,
  scraped,
  multiline = false,
  replacing,
  onChange,
}: {
  label: string;
  current?: string | number | null;
  scraped?: string | number | null;
  multiline?: boolean;
  replacing: boolean;
  onChange: (shouldReplace: boolean) => void;
}) {
  return (
    <div className="flex items-start gap-2">
      <CompactFieldLabel>{label}</CompactFieldLabel>
      <div className="grid min-w-0 flex-1 gap-1.5 md:grid-cols-2">
        <CompactDecisionPane label="Current" selected={!replacing} tone="current" onClick={() => onChange(false)}>
          <CompactValue value={current} multiline={multiline} />
        </CompactDecisionPane>
        <CompactDecisionPane label="Scraped" selected={replacing} tone="scraped" onClick={() => onChange(true)}>
          <CompactValue value={scraped} multiline={multiline} />
        </CompactDecisionPane>
      </div>
    </div>
  );
}

export function CompactCollectionDecision({
  label,
  current,
  scraped,
  mode,
  onModeChange,
}: {
  label: string;
  current: string[];
  scraped: ReactNode;
  mode: CollectionMode;
  onModeChange: (mode: CollectionMode) => void;
}) {
  const currentSelected = mode === "skip" || mode === "merge";
  const scrapedSelected = mode === "replace" || mode === "merge";

  return (
    <div className="flex items-start gap-2">
      <CompactFieldLabel>{label}</CompactFieldLabel>
      <div className="min-w-0 flex-1 space-y-1.5">
        <div className="flex flex-wrap items-center gap-1.5">
          <CompactModeButton mode="merge" selected={mode === "merge"} onModeChange={onModeChange}>
            Merge current + scraped
          </CompactModeButton>
        </div>
        <div className="grid gap-1.5 md:grid-cols-2">
          <CompactDecisionPane
            label="Current"
            selected={currentSelected}
            tone="current"
            onClick={() => onModeChange("skip")}
          >
            <CompactListValue values={current} />
          </CompactDecisionPane>
          <CompactDecisionPane
            label="Scraped"
            selected={scrapedSelected}
            tone="scraped"
            onClick={() => onModeChange("replace")}
          >
            {scraped}
          </CompactDecisionPane>
        </div>
      </div>
    </div>
  );
}

function CompactFieldLabel({ children }: { children: ReactNode }) {
  return <span className="w-20 shrink-0 pt-2 text-[10px] uppercase tracking-wider text-muted">{children}</span>;
}

function CompactDecisionPane({
  label,
  selected,
  tone,
  onClick,
  children,
}: {
  label: string;
  selected: boolean;
  tone: "current" | "scraped";
  onClick: () => void;
  children: ReactNode;
}) {
  const selectedClass =
    tone === "current"
      ? "border-green-600/25 bg-green-600/10 text-foreground"
      : "border-accent/40 bg-accent/10 text-foreground";

  return (
    <div
      role="button"
      tabIndex={0}
      onClick={(event) => {
        event.stopPropagation();
        onClick();
      }}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          event.stopPropagation();
          onClick();
        }
      }}
      className={`min-w-0 cursor-pointer rounded border px-2.5 py-2 transition-colors ${selected ? selectedClass : "border-border bg-surface/70 text-secondary hover:border-accent/40"}`}
    >
      <div
        className={`mb-1 flex items-center gap-1 text-[9px] font-semibold uppercase tracking-[0.16em] ${selected ? (tone === "current" ? "text-green-300" : "text-accent") : "text-muted"}`}
      >
        {selected && <Check className="h-2.5 w-2.5" />}
        {label}
      </div>
      {children}
    </div>
  );
}

function CompactModeButton({
  children,
  mode,
  selected,
  onModeChange,
}: {
  children: ReactNode;
  mode: CollectionMode;
  selected: boolean;
  onModeChange: (mode: CollectionMode) => void;
}) {
  return (
    <button
      type="button"
      onClick={(event) => {
        event.stopPropagation();
        onModeChange(mode);
      }}
      className={`rounded-full border px-2.5 py-0.5 text-[10px] font-medium transition-colors ${selected ? "border-accent/40 bg-accent/10 text-accent" : "border-border bg-surface text-muted hover:border-accent/40 hover:text-secondary"}`}
    >
      {children}
    </button>
  );
}

function CompactValue({ value, multiline = false }: { value?: string | number | null; multiline?: boolean }) {
  if (value === undefined || value === null || value === "") return <span className="text-xs text-muted">Empty</span>;
  return <div className={`text-xs leading-relaxed ${multiline ? "line-clamp-2" : "truncate"}`}>{String(value)}</div>;
}

export function CompactListValue({ values, breakAll = false }: { values: string[]; breakAll?: boolean }) {
  if (values.length === 0) return <span className="text-xs text-muted">Empty</span>;
  return (
    <div className={`text-xs leading-relaxed line-clamp-2 ${breakAll ? "break-all" : ""}`}>{values.join(", ")}</div>
  );
}

/**
 * A row's failed library lookup and its Retry. Not a live region: the page announces how its checks went
 * once, rather than every row at once. While the lookup is asked again the line stays, its Retry focusable
 * but marked unavailable, so keyboard focus is kept; when the line goes while Retry has focus,
 * `onFocusedRemoval` lets the row move focus on.
 */
export function LookupFailureLine({
  retrying,
  onRetry,
  onFocusedRemoval,
}: {
  retrying: boolean;
  onRetry: () => void;
  onFocusedRemoval: () => void;
}) {
  const buttonRef = useRef<HTMLButtonElement>(null);
  const onFocusedRemovalRef = useRef(onFocusedRemoval);
  useLayoutEffect(() => {
    onFocusedRemovalRef.current = onFocusedRemoval;
  });
  // A layout cleanup runs before the button leaves the document, so it can still tell whether it has focus.
  useLayoutEffect(() => {
    const button = buttonRef.current;
    return () => {
      if (button && document.activeElement === button) onFocusedRemovalRef.current();
    };
  }, []);
  return (
    <span className="flex w-full flex-wrap items-center gap-x-2 text-[11px] text-red-400">
      <AlertCircle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      {retrying ? "Checking your library again…" : "Couldn't check which of these are in your library."}
      <button
        ref={buttonRef}
        type="button"
        aria-disabled={retrying || undefined}
        onClick={() => {
          if (!retrying) onRetry();
        }}
        className="text-accent hover:underline aria-disabled:cursor-default aria-disabled:opacity-60 aria-disabled:no-underline"
      >
        Retry
      </button>
    </span>
  );
}

/** How long the lookups must stay quiet before the page says how checking went, so rows finishing one after another are one message. */
const LOOKUP_ANNOUNCEMENT_DELAY_MS = 500;

/**
 * What the page says once its rows have stopped waiting on the library lookup: that checking finished, or
 * that some of its items could not be checked. Rows wait on the lookup together, so one page-wide message
 * replaces each row announcing its own, said once the lookups have been quiet for a moment. Only a
 * lookup a row waited on counts: a refetch that keeps a previous answer (after a link or an apply) held
 * nothing up, and a lookup whose row went away was never checked. Each message gets a new id so that a
 * repeat (a Retry that fails again) is announced again.
 */
export function useRelationLookupAnnouncement(queryKey: string, itemsLabel: string) {
  const queryClient = useQueryClient();
  const [announcement, setAnnouncement] = useState<{ id: number; text: string } | null>(null);
  useEffect(() => {
    const cache = queryClient.getQueryCache();
    const waitedOn = new Set<string>();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const activeLookups = () => cache.findAll({ queryKey: [queryKey], type: "active" });
    const settle = () => {
      timer = undefined;
      const active = new Map(activeLookups().map((query) => [query.queryHash, query]));
      const settled = [...waitedOn].flatMap((hash) => active.get(hash) ?? []);
      waitedOn.clear();
      if (settled.length === 0) return;
      const failed = settled.some((query) => query.state.data === undefined && query.state.status === "error");
      setAnnouncement((current) => ({
        id: (current?.id ?? 0) + 1,
        text: failed
          ? `Couldn't check some ${itemsLabel} against your library. Their rows have a Retry.`
          : "Finished checking your library.",
      }));
    };
    const update = () => {
      const waiting = activeLookups().filter(
        (query) => query.state.data === undefined && query.state.fetchStatus !== "idle",
      );
      for (const query of waiting) waitedOn.add(query.queryHash);
      clearTimeout(timer);
      timer = waiting.length === 0 && waitedOn.size > 0 ? setTimeout(settle, LOOKUP_ANNOUNCEMENT_DELAY_MS) : undefined;
    };
    const unsubscribe = cache.subscribe(update);
    return () => {
      unsubscribe();
      clearTimeout(timer);
    };
  }, [queryClient, queryKey, itemsLabel]);
  return announcement;
}

/** The page's announcement of how its lookups went: mounted empty, so screen readers announce what it later says. */
export function LookupAnnouncementRegion({ queryKey, itemsLabel }: { queryKey: string; itemsLabel: string }) {
  const announcement = useRelationLookupAnnouncement(queryKey, itemsLabel);
  return (
    <div role="status" className="sr-only">
      {announcement ? <span key={announcement.id}>{announcement.text}</span> : null}
    </div>
  );
}
