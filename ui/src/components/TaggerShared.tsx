import { useState, type ReactNode } from "react";
import { Check, ChevronDown, CloudDownload, Eye, EyeOff, Loader2, RefreshCw, Settings2, X } from "lucide-react";
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
    .filter((entry): entry is { remote: { endpoint: string; remoteId: string }; server: { endpoint: string; name?: string } } => !!entry.server);
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
          {busyEndpoint === remote.endpoint ? <Loader2 className="h-3 w-3 animate-spin" /> : <RefreshCw className="h-3 w-3" />}
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

export const DEFAULT_TAGGER_BLACKLIST = ["\\sXXX\\s", "1080p", "720p", "2160p", "4K", "KTR", "RARBG", "\\smp4\\s"];

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
    return output.slice(0, yyyymmdd).replace(/-/g, " ")
      + output.slice(yyyymmdd, yyyymmdd + 10).replace(/\./g, "-")
      + output.slice(yyyymmdd + 10).replace(/-/g, " ");
  }
  const javcodeIndex = output.search(javcodeRegex);
  if (javcodeIndex !== -1) {
    const javcodeLength = output.match(javcodeRegex)![1].length;
    return output.slice(0, javcodeIndex).replace(/-/g, " ")
      + output.slice(javcodeIndex, javcodeIndex + javcodeLength)
      + output.slice(javcodeIndex + javcodeLength).replace(/-/g, " ");
  }
  return output.replace(/-/g, " ");
}

export function cleanTaggerQueryString(input: string, blacklist: string[]): string {
  let cleaned = input.replace(/[._]/g, " ");
  for (const pattern of blacklist) {
    try {
      cleaned = cleaned.replace(new RegExp(pattern, "gi"), "");
    } catch {
      // Invalid blacklist regexes are ignored so one bad entry does not break tagging.
    }
  }
  cleaned = handleSpecialQueryStrings(cleaned);
  return cleaned.replace(/ +/g, " ").trim();
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
  runAllLabel = "Scrape All",
  showRunAll = true,
  countLabel,
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
            <option key={source.value} value={source.value}>{source.label}</option>
          ))}
        </select>
      </div>

      {showToggle && (
        <button
          type="button"
          onClick={() => showToggle.onChange(!showToggle.value)}
          className="flex items-center gap-1 px-2 py-1 rounded text-xs border border-border bg-input text-secondary hover:text-foreground"
        >
          {showToggle.value ? <Eye className="w-3.5 h-3.5" /> : <EyeOff className="w-3.5 h-3.5" />}
          {showToggle.value ? showToggle.enabledLabel : showToggle.disabledLabel}
        </button>
      )}

      {showRunAll && (
        batchSearching ? (
          <button type="button" onClick={onCancelBatch} className="flex items-center gap-1.5 px-3 py-1 rounded text-xs font-medium bg-red-600 text-white hover:bg-red-500">
            <X className="w-3.5 h-3.5" />
            Cancel
          </button>
        ) : (
          <div className="flex items-stretch">
            <button type="button" onClick={() => onRunAll()} className={`flex items-center gap-1.5 px-3 py-1 text-xs font-medium bg-accent text-white hover:bg-accent-hover ${runAllOptions?.length ? "rounded-l" : "rounded"}`}>
              <CloudDownload className="w-3.5 h-3.5" />
              {runAllLabel}
            </button>
            {runAllOptions?.length ? (
              <details className="relative">
                <summary role="button" className="flex h-full list-none items-center rounded-r border-l border-white/20 bg-accent px-1.5 text-white hover:bg-accent-hover cursor-pointer" aria-label="Choose scrape strategy">
                  <ChevronDown className="w-3.5 h-3.5" />
                </summary>
                <div className="absolute left-0 z-30 mt-1 w-72 overflow-hidden rounded border border-border bg-card shadow-xl">
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
              </details>
            ) : null}
          </div>
        )
      )}

      <span className="ml-auto text-xs text-muted">{countLabel}</span>

      {onToggleSettings && (
        <button
          type="button"
          onClick={onToggleSettings}
          className={`flex items-center gap-1 px-2 py-1 rounded text-xs border bg-input ${settingsOpen ? "border-accent text-accent" : "border-border text-secondary hover:text-foreground"}`}
          title="Tagger settings"
        >
          <Settings2 className="w-3.5 h-3.5" />
        </button>
      )}
    </div>
  );
}

export function TaggerSettingsPanel({
  children,
  blacklist,
  onBlacklistChange,
}: {
  children?: ReactNode;
  blacklist?: string[];
  onBlacklistChange?: (items: string[]) => void;
}) {
  const hasConfiguration = Boolean(children);
  const hasBlacklist = Boolean(blacklist && onBlacklistChange);

  return (
    <div className="bg-card border-b border-border px-4 py-3 space-y-4">
      <div className={hasConfiguration && hasBlacklist ? "grid grid-cols-1 lg:grid-cols-2 gap-6" : "space-y-3"}>
        {hasConfiguration && (
          <div className="space-y-3">
            <h3 className="text-sm font-bold text-foreground italic">Configuration</h3>
            {children}
          </div>
        )}
        {blacklist && onBlacklistChange && (
          <div className={hasConfiguration ? "space-y-2" : "max-w-3xl space-y-2"}>
            <h3 className="text-sm font-bold text-foreground italic">Blacklist</h3>
            <BlacklistEditor items={blacklist} onChange={onBlacklistChange} />
            <p className="text-[10px] text-muted">
              Blacklist items are excluded from queries. They are case-insensitive regular expressions. Escape special characters with a backslash: <code className="text-pink-400">{`[\\.^$.|?*+()`}</code>
            </p>
          </div>
        )}
      </div>
    </div>
  );
}

export function BlacklistEditor({ items, onChange }: { items: string[]; onChange: (items: string[]) => void }) {
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
          onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); addItem(); } }}
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
          <span key={`${item}-${index}`} className="inline-flex items-center gap-1 bg-surface text-foreground text-xs px-2 py-1 rounded border border-border font-mono">
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
          <CompactModeButton mode="merge" selected={mode === "merge"} onModeChange={onModeChange}>Merge current + scraped</CompactModeButton>
        </div>
        <div className="grid gap-1.5 md:grid-cols-2">
          <CompactDecisionPane label="Current" selected={currentSelected} tone="current" onClick={() => onModeChange("skip")}>
            <CompactListValue values={current} />
          </CompactDecisionPane>
          <CompactDecisionPane label="Scraped" selected={scrapedSelected} tone="scraped" onClick={() => onModeChange("replace")}>
            {scraped}
          </CompactDecisionPane>
        </div>
      </div>
    </div>
  );
}

// Cover-image equivalent of CompactScalarDecision: current cover thumbnail vs the scraped/match cover,
// with a Keep / Replace choice. Render only when a scraped cover exists. `replacing` should default to
// true when the entity has no current cover (replace-if-empty) and false when it already has one.
export function CompactImageDecision({
  label = "Cover",
  currentImageUrl,
  scrapedImageUrl,
  replacing,
  onChange,
}: {
  label?: string;
  currentImageUrl?: string | null;
  scrapedImageUrl?: string | null;
  replacing: boolean;
  onChange: (shouldReplace: boolean) => void;
}) {
  return (
    <div className="flex items-start gap-2">
      <CompactFieldLabel>{label}</CompactFieldLabel>
      <div className="grid min-w-0 flex-1 gap-1.5 md:grid-cols-2">
        <CompactDecisionPane label="Current" selected={!replacing} tone="current" onClick={() => onChange(false)}>
          <CompactImageValue url={currentImageUrl} />
        </CompactDecisionPane>
        <CompactDecisionPane label="Scraped" selected={replacing} tone="scraped" onClick={() => onChange(true)}>
          <CompactImageValue url={scrapedImageUrl} />
        </CompactDecisionPane>
      </div>
    </div>
  );
}

function CompactImageValue({ url }: { url?: string | null }) {
  if (!url) return <span className="text-xs text-muted">No image</span>;
  return <img src={url} alt="" className="h-24 w-auto max-w-full rounded object-cover object-top" loading="lazy" />;
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
  const selectedClass = tone === "current"
    ? "border-green-600/25 bg-green-600/10 text-foreground"
    : "border-accent/40 bg-accent/10 text-foreground";

  return (
    <div
      role="button"
      tabIndex={0}
      onClick={(event) => { event.stopPropagation(); onClick(); }}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          event.stopPropagation();
          onClick();
        }
      }}
      className={`min-w-0 cursor-pointer rounded border px-2.5 py-2 transition-colors ${selected ? selectedClass : "border-border bg-surface/70 text-secondary hover:border-accent/40"}`}
    >
      <div className={`mb-1 flex items-center gap-1 text-[9px] font-semibold uppercase tracking-[0.16em] ${selected ? tone === "current" ? "text-green-300" : "text-accent" : "text-muted"}`}>
        {selected && <Check className="h-2.5 w-2.5" />}
        {label}
      </div>
      {children}
    </div>
  );
}

function CompactModeButton({ children, mode, selected, onModeChange }: { children: ReactNode; mode: CollectionMode; selected: boolean; onModeChange: (mode: CollectionMode) => void }) {
  return (
    <button
      type="button"
      onClick={(event) => { event.stopPropagation(); onModeChange(mode); }}
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
  return <div className={`text-xs leading-relaxed line-clamp-2 ${breakAll ? "break-all" : ""}`}>{values.join(", ")}</div>;
}
