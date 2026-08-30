import { ChevronDown, ChevronUp, Loader2, PlayCircle } from "lucide-react";
import type { ButtonHTMLAttributes, ReactNode } from "react";

const fieldDescriptionFallbacks: Record<string, string> = {
  "Max parallel tasks (-1 = all CPU threads)": "Caps concurrent background work. Use -1 to let Cove scale to available CPU threads.",
  // Scan & generate task options (shared labels)
  "Thumbnails / screenshots": "A still cover frame captured from the video, used as its poster image on cards, lists, and the detail page.",
  "Video previews": "A short looping clip stitched from a few moments across the video, played when you hover a card so you can preview it without opening it.",
  "Sprite sheets": "A grid of small thumbnails sampled across the timeline. Powers the scrubbing preview that appears when you hover the seek bar during playback.",
  "Perceptual hashes (phash)": "A fingerprint of the video's visual content (not the exact bytes). Used to match it against entries on metadata servers and to find near-duplicate or re-encoded copies in your library.",
  "MD5 checksums": "An exact fingerprint of the file's bytes. Used to match against metadata servers and to detect identical duplicate files in your library.",
  "Image thumbnails": "A smaller resized copy of each image for fast loading in grids and lists.",
  "Image phashes": "A visual fingerprint of each image. Used to match against metadata servers and to find near-duplicate images in your library.",
  "Gallery cover thumbnails": "A thumbnail of each gallery's cover image, shown on gallery cards and lists.",
  "Audio perceptual hashes": "An acoustic fingerprint of each audio file. Used to match against metadata servers and to find duplicate or re-encoded audio in your library.",
  "Text perceptual hashes": "A content fingerprint of each text document. Used to match against metadata servers and to find duplicate text in your library.",
  "Overwrite existing generated files": "Regenerate and replace assets that already exist, instead of skipping files that are already done.",
  "Force rescan (ignore mtime)": "Re-examine every file even if its modified time hasn't changed, instead of skipping files that look unchanged.",
  "Exclude videos": "Skip video files under this library path during scans.",
  "Exclude images": "Skip image files under this library path during scans.",
  "Exclude audio": "Skip audio files under this library path during scans.",
  "Generated path": "Directory where Cove writes generated covers, thumbnails, previews, sprites, and similar assets.",
  "Cache path": "Directory for transient cache files that can be regenerated.",
  "Preview preset": "FFmpeg preset used when Cove creates generated video previews.",
  "Max concurrent downloads": "Limits how many downloader jobs can actively fetch media at the same time.",
  "Site override (optional)": "Restricts this downloader path override to a specific normalized site key.",
  "Save path": "Destination folder used by matching downloader jobs.",
  "Video extensions": "File extensions treated as videos during library scans.",
  "Image extensions": "File extensions treated as images during library scans.",
  "Gallery extensions": "Archive or gallery file extensions discovered during scans.",
  "Audio extensions": "File extensions treated as audio during library scans.",
  "Text extensions": "File extensions treated as text documents during library scans.",
  "Calculate MD5 checksums during scan": "Computes MD5 hashes while scanning so exact duplicate checks have stable file fingerprints.",
  "Exclude patterns": "Path fragments or glob patterns ignored during library scans. Filename globs apply at any depth; use * within one folder, ** across folders, or ? for one character.",
  "Excluded image patterns": "Image-specific path fragments or glob patterns. Filename globs apply at any depth; use * within one folder, ** across folders, or ? for one character.",
  "Excluded gallery patterns": "Gallery-specific path fragments or glob patterns. Filename globs apply at any depth; use * within one folder, ** across folders, or ? for one character.",
  "Create galleries from folders": "Treat image folders as gallery entities when scans discover grouped image sets.",
  "Write image thumbnails": "Generate thumbnail files for images while scanning or generating assets.",
  "Create image clips from videos": "Allow scans to create still-image clip records derived from video files.",
  "Delete file default": "Default state for delete dialogs that can also remove source media files.",
  "Delete generated default": "Default state for delete dialogs that can also remove generated Cove assets.",
  "Gallery cover regex": "Regular expression used to pick a preferred gallery cover image from gallery file names.",
  "Rating system": "Controls whether ratings are shown as stars or decimal values.",
  "Star precision": "Controls how finely star ratings can be adjusted.",
  "Default player start (%)": "Starts video playback at this percentage for long enough videos.",
  "Use default start only for videos longer than (seconds)": "Keeps short videos starting from the beginning even when a start percentage is set.",
  "Wall show title": "Shows item titles on wall cards.",
  "Wall playback": "Controls how wall cards start or preview video playback.",
  "Playback source": "Chooses whether feed-style cards use generated previews or original video playback.",
  "Play sound by default in Feed and Vertical Viewer": "Controls whether feed and vertical-viewer videos start muted or with audio.",
  "Full video start (%)": "Starts full-video feed playback at this percentage for long enough videos.",
  "Use start % only for videos longer than (seconds)": "Keeps shorter feed videos starting at the beginning.",
  "Slideshow delay (ms)": "Delay between images while slideshow mode advances automatically.",
  "Enable CSS customization": "Allows custom CSS from settings to be injected into the app shell.",
  "Custom CSS": "CSS injected into the app when CSS customization is enabled.",
  "Enable JavaScript customization": "Allows custom JavaScript from settings to run in the app shell.",
  "Custom JavaScript": "JavaScript injected into the app when JavaScript customization is enabled.",
  "Authentication required": "Requires users to sign in before accessing protected Cove APIs and pages.",
  "Allow anonymous share links": "Allows generated share links to grant anonymous read-only access when valid.",
  "Name": "Friendly display name for this entry.",
  "Endpoint": "Base URL used when Cove connects to this service.",
  "Max req/min": "Per-minute request cap used to avoid overwhelming this metadata server.",
  "Existing linked entities": "Controls whether batch metadata operations keep or overwrite existing linked entities.",
  "Max auto-apply duration difference (seconds)": "Maximum duration mismatch allowed when Identify auto-applies a match.",
  "Max auto-apply pHash distance": "Maximum perceptual-hash distance allowed when Identify auto-applies a match.",
  "Allow Identify to create new performers": "Lets Identify create performer records when applying metadata.",
  "Allow Identify to create new studios": "Lets Identify create studio records when applying metadata.",
  "Allow Identify to create new tags": "Lets Identify create tag records when applying metadata.",
  "Host": "Network interface the Cove API binds to after restart.",
  "Port": "HTTP port the Cove API listens on after restart.",
  "Enable hardware acceleration (FFmpeg in-process)": "Allows Cove's in-process FFmpeg work to use configured hardware acceleration when available.",
  "FFmpeg path": "Optional absolute path to the FFmpeg executable.",
  "FFprobe path": "Optional absolute path to the FFprobe executable.",
  "Max transcode size": "Maximum output size used for generated transcodes.",
  "Max streaming transcode size": "Maximum output size used for live streaming transcodes.",
  "Hardware acceleration": "Hardware acceleration backend passed to FFmpeg transcode jobs.",
  "Transcode input args": "Additional FFmpeg input arguments for generated transcodes.",
  "Transcode output args": "Additional FFmpeg output arguments for generated transcodes.",
  "Live transcode input args": "Additional FFmpeg input arguments for live streaming transcodes.",
  "Live transcode output args": "Additional FFmpeg output arguments for live streaming transcodes.",
  "Enable engagement history": "Records viewing and engagement events for history, activity, and derived recommendations.",
  "Minimum video view seconds": "Minimum video watch time before Cove records a view.",
  "Video completion ratio": "Fraction of a video that must be watched before Cove records completion.",
  "Minimum image view seconds": "Minimum image detail-view time before Cove records a view.",
  "Minimum session length for derived likes": "Minimum viewing-session duration before Cove derives engagement from it.",
  "Session idle timeout seconds": "Idle time after which Cove starts a new engagement session.",
  "Segment thumbnails": "A still frame captured at the start of each segment, used as that segment's thumbnail.",
  "Animated segment previews": "A short looping clip for each segment, played on hover like video previews but scoped to the segment.",
};

export function getSettingHelpText(label: string, description?: string) {
  return description ?? fieldDescriptionFallbacks[label] ?? `Changes the ${label.toLowerCase()} setting.`;
}

export interface SettingsSectionProps {
  title: string;
  description?: string;
  children: ReactNode;
  actions?: ReactNode;
  className?: string;
  headerClassName?: string;
}

export function SettingsSection({ title, description, children, actions, className = "", headerClassName = "" }: SettingsSectionProps) {
  return (
    <section className={`rounded-2xl border border-border bg-surface p-5 shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)] ${className}`.trim()}>
      <header className={`mb-4 flex items-start justify-between gap-4 ${headerClassName}`.trim()}>
        <div>
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
          {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
        </div>
        {actions ? <div className="shrink-0">{actions}</div> : null}
      </header>
      {children}
    </section>
  );
}

export function SectionCard(props: SettingsSectionProps) {
  return <SettingsSection {...props} />;
}

export function CollapsibleSection({ title, subtitle, expanded, onToggle, children }: { title: string; subtitle?: string; expanded: boolean; onToggle: () => void; children: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-xl border border-border">
      <button type="button" onClick={onToggle} className="flex w-full items-center justify-between bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover">
        <div className="min-w-0">
          <span className="text-sm font-medium text-foreground">{title}</span>
          {subtitle ? <span className="ml-2 text-xs text-muted">({subtitle})</span> : null}
        </div>
        {expanded ? <ChevronUp className="h-4 w-4 shrink-0 text-muted" /> : <ChevronDown className="h-4 w-4 shrink-0 text-muted" />}
      </button>
      {expanded ? <div className="border-t border-border px-4 py-3">{children}</div> : null}
    </div>
  );
}

export function SettingsField({ label, description, children, className = "" }: { label: string; description?: string; children: ReactNode; className?: string }) {
  return (
    <label className={`block text-sm ${className}`.trim()} title={getSettingHelpText(label, description)}>
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      {children}
    </label>
  );
}

export function TextField({
  label,
  value,
  onChange,
  onBlur,
  placeholder,
  type = "text",
  description,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  placeholder?: string;
  type?: string;
  description?: string;
}) {
  return (
    <SettingsField label={label} description={description}>
      <input
        type={type}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onBlur={onBlur}
        placeholder={placeholder}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </SettingsField>
  );
}

export function NumberField({
  label,
  value,
  onChange,
  min,
  max,
  description,
}: {
  label: string;
  value?: number;
  onChange: (value: number | undefined) => void;
  min?: number;
  max?: number;
  description?: string;
}) {
  return (
    <SettingsField label={label} description={description}>
      <input
        type="number"
        value={value ?? ""}
        min={min}
        max={max}
        onChange={(event) => onChange(event.target.value ? Number(event.target.value) : undefined)}
        className="themed-number-input w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </SettingsField>
  );
}

export function TextAreaField({
  label,
  value,
  onChange,
  onBlur,
  rows,
  placeholder,
  description,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  rows: number;
  placeholder?: string;
  description?: string;
}) {
  return (
    <SettingsField label={label} description={description}>
      <textarea
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onBlur={onBlur}
        rows={rows}
        placeholder={placeholder}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </SettingsField>
  );
}

export function SelectField({
  label,
  value,
  onChange,
  onBlur,
  options,
  disabled = false,
  description,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  options: ReadonlyArray<{ value: string; label: string }>;
  disabled?: boolean;
  description?: string;
}) {
  return (
    <SettingsField label={label} description={description}>
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onBlur={onBlur}
        disabled={disabled}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </SettingsField>
  );
}

export function CheckboxLabel({ label, checked, onChange, description, disabled = false }: { label: string; checked: boolean; onChange: (checked: boolean) => void; description?: string; disabled?: boolean }) {
  return (
    <label className="flex items-center gap-2 text-sm text-secondary" title={getSettingHelpText(label, description)}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
        className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0 disabled:cursor-not-allowed disabled:opacity-60"
      />
      <span>{label}</span>
    </label>
  );
}

export function InfoPair({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-border bg-card p-3">
      <dt className="text-xs font-medium uppercase tracking-wide text-muted">{label}</dt>
      <dd className="mt-1 break-all text-sm text-foreground">{value}</dd>
    </div>
  );
}

export function SettingsMetricCard({ label, value, valueClassName = "text-2xl" }: { label: string; value: ReactNode; valueClassName?: string }) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="text-xs uppercase tracking-[0.16em] text-muted">{label}</div>
      <div className={`mt-2 font-semibold text-foreground ${valueClassName}`.trim()}>{value}</div>
    </div>
  );
}

export function TaskCard({
  label,
  description,
  onRun,
  isPending,
  expandable,
  expanded,
  onToggleExpand,
  runLabel = "Run",
  statusMessage,
  children,
}: {
  label: string;
  description: string;
  onRun: () => void;
  isPending: boolean;
  expandable?: boolean;
  expanded?: boolean;
  onToggleExpand?: () => void;
  runLabel?: string;
  statusMessage?: { type: "success" | "error"; text: string } | null;
  children?: ReactNode;
}) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="flex items-center justify-between">
        <div className="flex min-w-0 flex-1 items-center gap-2">
          {expandable && onToggleExpand ? (
            <button type="button" onClick={onToggleExpand} className="shrink-0 text-muted hover:text-foreground">
              {expanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
            </button>
          ) : null}
          <div>
            <h4 className="text-sm font-medium text-foreground">{label}</h4>
            <p className="mt-0.5 text-xs text-secondary">{description}</p>
          </div>
        </div>
        <button
          type="button"
          onClick={onRun}
          disabled={isPending}
          className="ml-3 inline-flex shrink-0 items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
        >
          {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <PlayCircle className="h-4 w-4" />}
          {runLabel}
        </button>
      </div>
      {statusMessage ? (
        <p className={`mt-2 text-xs ${statusMessage.type === "success" ? "text-green-400" : "text-red-400"}`}>
          {statusMessage.text}
        </p>
      ) : null}
      {children && (!expandable || expanded) ? <div className="mt-3">{children}</div> : null}
    </div>
  );
}

export function SettingsButton(props: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "danger" | "ghost" }) {
  const { variant = "ghost", className = "", type = "button", ...rest } = props;
  const base = "inline-flex min-h-10 items-center justify-center gap-1.5 rounded-lg px-3 py-2 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 sm:min-h-0 sm:py-1.5";
  const variantClass =
    variant === "primary" ? "bg-accent text-white hover:bg-accent-hover" :
    variant === "danger" ? "bg-red-600 text-white hover:bg-red-500" :
    "border border-border bg-card text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground";

  return <button {...rest} type={type} className={`${base} ${variantClass} ${className}`} />;
}
