import { forwardRef, useMemo, type MouseEvent, type ReactNode } from "react";
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  Columns2,
  Crown,
  EyeOff,
  ExternalLink,
  GitMerge,
  Loader2,
  Trash2,
  Undo2,
} from "lucide-react";
import type { DuplicateSearchGroup, EntityEngagement, Video } from "../../api/types";
import { createRouteLinkProps } from "../cardNavigation";
import { formatFileSize } from "../shared";
import { VideoPreviewThumbnail } from "../VideoPreviewThumbnail";
import {
  COMPARISON_ROWS,
  describeDecision,
  describeSimilarity,
  displayTitle,
  folderOf,
  primaryFile,
  rowTones,
  totalSize,
  type ResolutionPreferences,
} from "./duplicateModel";

interface Props {
  group: DuplicateSearchGroup;
  keepVideoIds: Set<number>;
  engagement: Map<number, EntityEngagement>;
  resolution: ResolutionPreferences;
  focused: boolean;
  busy: boolean;
  showIdenticalRows: boolean;
  canResolve: boolean;
  canIgnore: boolean;
  onFocus: () => void;
  onKeepOnly: (videoId: number) => void;
  onToggleKeep: (videoId: number) => void;
  onResolve: () => void;
  onIgnore: () => void;
  onRestore: () => void;
  onCompare: (videoIds?: [number, number]) => void;
  onQuickView: (videoId: number) => void;
  onNavigate: (route: any) => void;
}

export const DuplicateGroupCard = forwardRef<HTMLElement, Props>(function DuplicateGroupCard(props, ref) {
  const { group } = props;
  if (group.status === "resolved") return <ResolvedGroup ref={ref} {...props} />;
  if (group.status === "ignored") return <IgnoredGroup ref={ref} {...props} />;
  return <ReviewGroup ref={ref} {...props} />;
});

const ReviewGroup = forwardRef<HTMLElement, Props>(function ReviewGroup(
  {
    group,
    keepVideoIds,
    engagement,
    resolution,
    focused,
    busy,
    showIdenticalRows,
    canResolve,
    canIgnore,
    onFocus,
    onKeepOnly,
    onToggleKeep,
    onResolve,
    onIgnore,
    onCompare,
    onQuickView,
    onNavigate,
  },
  ref,
) {
  const inFlight = group.status === "queued" || group.status === "processing";
  const locked = inFlight || busy;
  const videos = group.videos;
  const removeCount = videos.filter((video) => !keepVideoIds.has(video.id)).length;
  const reclaimable = videos
    .filter((video) => !keepVideoIds.has(video.id))
    .reduce((sum, video) => sum + totalSize(video), 0);
  const similarity = useMemo(() => describeSimilarity(videos), [videos]);
  const rows = useMemo(
    () => COMPARISON_ROWS.map((row) => ({ row, ...rowTones(row, videos, engagement) })),
    [videos, engagement],
  );
  const differing = rows.filter((entry) => !entry.allSame);
  const identical = rows.filter((entry) => entry.allSame);
  const visibleRows = showIdenticalRows ? rows : differing;
  const decision = describeDecision(group.decisionRule, group.decisionSource);
  const firstKeeper = videos.find((video) => keepVideoIds.has(video.id));
  const firstRemoval = videos.find((video) => !keepVideoIds.has(video.id));
  // Bounded tracks: copies stay side by side at a comfortable size and scroll horizontally on narrow
  // screens, without the grid growing to a cover image's intrinsic width.
  const gridStyle = {
    gridTemplateColumns: `clamp(5.5rem, 16vw, 8rem) repeat(${videos.length}, minmax(17rem, 26rem))`,
    minWidth: `calc(5.5rem + ${17 * videos.length}rem)`,
    maxWidth: `calc(8rem + ${26 * videos.length}rem)`,
  };
  const resolveLabel = resolution.action === "merge" ? `Merge & remove ${removeCount}` : `Remove ${removeCount}`;

  return (
    <article
      ref={ref}
      data-duplicate-group={group.id}
      onClick={onFocus}
      className={`overflow-hidden rounded-xl border bg-card transition-shadow ${
        focused ? "border-accent/70 shadow-lg shadow-accent/10 ring-1 ring-accent/40" : "border-border"
      }`}
    >
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold text-foreground">Group {group.position + 1}</span>
            <span className="text-sm text-muted">· {videos.length} copies</span>
            <SimilarityBadges similarity={similarity} />
            {inFlight ? (
              <span className="inline-flex items-center gap-1 rounded-full bg-accent/15 px-2 py-0.5 text-xs text-accent">
                <Loader2 className="h-3 w-3 animate-spin" />
                {group.status === "queued"
                  ? "Queued"
                  : group.resolutionAction === "merge"
                    ? "Merging & removing…"
                    : "Removing…"}
              </span>
            ) : null}
          </div>
          {decision ? <div className="mt-0.5 text-xs text-muted">{decision}</div> : null}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <HeaderButton
            onClick={(event) => {
              event.stopPropagation();
              onCompare(firstKeeper && firstRemoval ? [firstKeeper.id, firstRemoval.id] : undefined);
            }}
            title="Compare side by side (c)"
          >
            <Columns2 className="h-4 w-4" />
            Compare
          </HeaderButton>
          {canIgnore ? (
            <HeaderButton
              onClick={(event) => {
                event.stopPropagation();
                onIgnore();
              }}
              disabled={locked}
              title="These are different videos. They won't be grouped again. (x)"
            >
              <EyeOff className="h-4 w-4" />
              Not duplicates
            </HeaderButton>
          ) : null}
          {canResolve ? (
            <button
              type="button"
              onClick={(event) => {
                event.stopPropagation();
                onResolve();
              }}
              disabled={locked || removeCount === 0}
              title={removeCount === 0 ? "Every copy is marked to keep" : `${resolveLabel} (r)`}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-40 ${
                resolution.deleteFiles ? "bg-red-600 hover:bg-red-500" : "bg-accent hover:bg-accent-hover"
              }`}
            >
              {resolution.action === "merge" ? <GitMerge className="h-4 w-4" /> : <Trash2 className="h-4 w-4" />}
              {resolveLabel}
              {reclaimable > 0 && resolution.deleteFiles ? (
                <span className="font-normal opacity-80">· {formatFileSize(reclaimable)}</span>
              ) : null}
            </button>
          ) : null}
        </div>
      </header>

      {group.status === "failed" && group.error ? (
        <div className="flex items-start gap-2 border-b border-red-900/60 bg-red-950/40 px-4 py-2 text-sm text-red-200">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{group.error}</span>
        </div>
      ) : null}

      <div className={`relative overflow-x-auto ${inFlight ? "pointer-events-none opacity-60" : ""}`}>
        <div className="grid w-full" style={gridStyle}>
          <div className="border-b border-r border-border/60 bg-surface/30" />
          {videos.map((video, index) => (
            <MemberHeader
              key={video.id}
              index={index}
              video={video}
              keep={keepVideoIds.has(video.id)}
              soleKeeper={keepVideoIds.has(video.id) && keepVideoIds.size === 1}
              locked={locked}
              onKeepOnly={() => onKeepOnly(video.id)}
              onToggleKeep={() => onToggleKeep(video.id)}
              onQuickView={() => onQuickView(video.id)}
              onNavigate={onNavigate}
            />
          ))}

          {visibleRows.map(({ row, tones }) => (
            <ComparisonRowView key={row.key} label={row.label}>
              {videos.map((video, index) => {
                const tone = tones[index];
                const value = row.render(video, engagement.get(video.id));
                return (
                  <div
                    key={video.id}
                    title={row.title?.(video)}
                    className={`truncate border-b border-l border-border/40 px-3 py-1.5 text-sm tabular-nums ${cellTone(tone, keepVideoIds.has(video.id))}`}
                  >
                    {tone === "best" ? <Check className="mr-1 inline h-3.5 w-3.5 -translate-y-px" /> : null}
                    {value}
                  </div>
                );
              })}
            </ComparisonRowView>
          ))}

          <ComparisonRowView label="Location">
            {videos.map((video) => {
              const file = primaryFile(video);
              const folder = folderOf(file?.path);
              return (
                <div
                  key={video.id}
                  title={file?.path || file?.basename}
                  className={`min-w-0 border-l border-border/40 px-3 py-2 text-xs ${keepVideoIds.has(video.id) ? "" : "opacity-80"}`}
                >
                  <div className="truncate font-medium text-foreground">{file?.basename ?? "No file"}</div>
                  {folder ? <div className="truncate text-muted">{folder}</div> : null}
                  {video.files.length > 1 ? (
                    <div className="text-muted">
                      +{video.files.length - 1} more file{video.files.length > 2 ? "s" : ""}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </ComparisonRowView>
        </div>
      </div>

      {!showIdenticalRows && identical.length > 0 ? (
        <div className="border-t border-border/60 bg-surface/20 px-4 py-1.5 text-xs text-muted">
          Identical: {identical.map((entry) => entry.row.label.toLowerCase()).join(", ")}
        </div>
      ) : null}
    </article>
  );
});

function MemberHeader({
  index,
  video,
  keep,
  soleKeeper,
  locked,
  onKeepOnly,
  onToggleKeep,
  onQuickView,
  onNavigate,
}: {
  index: number;
  video: Video;
  keep: boolean;
  soleKeeper: boolean;
  locked: boolean;
  onKeepOnly: () => void;
  onToggleKeep: () => void;
  onQuickView: () => void;
  onNavigate: (route: any) => void;
}) {
  const route = { page: "video", id: video.id };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <div
      className={`relative border-b border-l border-border/60 p-3 ${
        keep ? "bg-emerald-500/[0.07] shadow-[inset_0_3px_0_0_rgb(16_185_129)]" : "bg-red-500/[0.03]"
      }`}
    >
      <button
        type="button"
        onClick={(event) => {
          event.stopPropagation();
          onQuickView();
        }}
        className="block w-full overflow-hidden rounded-lg text-left focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        aria-label={`Preview ${displayTitle(video)}`}
      >
        <VideoPreviewThumbnail video={video} fit="cover" coverWidth={640} className={keep ? "" : "opacity-75"}>
          <span className="absolute left-2 top-2 z-[8] rounded bg-black/70 px-1.5 py-0.5 text-[11px] font-semibold text-white">
            {index + 1}
          </span>
          <span
            className={`absolute right-2 top-2 z-[8] inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide text-white shadow ${
              keep ? "bg-emerald-600" : "bg-red-600/90"
            }`}
          >
            {keep ? <Crown className="h-3 w-3" /> : <Trash2 className="h-3 w-3" />}
            {keep ? "Keep" : "Remove"}
          </span>
        </VideoPreviewThumbnail>
      </button>
      <div className="mt-2 flex items-start gap-1.5">
        <a
          {...linkProps}
          onClick={(event) => {
            event.stopPropagation();
            linkProps.onClick?.(event);
          }}
          className="line-clamp-2 min-w-0 flex-1 text-sm font-medium leading-snug text-foreground hover:text-accent"
          title={displayTitle(video)}
        >
          {displayTitle(video)}
        </a>
        <a
          href={linkProps.href}
          target="_blank"
          rel="noreferrer"
          onClick={(event) => event.stopPropagation()}
          aria-label="Open in a new tab"
          title="Open in a new tab"
          className="mt-0.5 shrink-0 text-muted hover:text-foreground"
        >
          <ExternalLink className="h-3.5 w-3.5" />
        </a>
      </div>
      <div className="mt-2 flex items-center gap-1.5">
        <button
          type="button"
          disabled={locked || soleKeeper}
          onClick={(event) => {
            event.stopPropagation();
            onKeepOnly();
          }}
          title={soleKeeper ? "This is the copy being kept" : `Keep only this copy (${index + 1})`}
          className={`inline-flex flex-1 items-center justify-center gap-1.5 rounded-md border px-2 py-1 text-xs font-medium transition-colors disabled:cursor-default ${
            keep
              ? "border-emerald-600/60 bg-emerald-600/15 text-emerald-300"
              : "border-border bg-surface text-secondary hover:border-emerald-600/60 hover:text-emerald-300"
          } ${locked && !soleKeeper ? "opacity-50" : ""}`}
        >
          <Crown className="h-3.5 w-3.5" />
          {soleKeeper ? "Keeping this copy" : keep ? "Keep only this" : "Keep this instead"}
        </button>
        <button
          type="button"
          disabled={locked || soleKeeper}
          onClick={(event) => {
            event.stopPropagation();
            onToggleKeep();
          }}
          title={keep ? "Remove this copy" : "Keep this copy as well"}
          className="rounded-md border border-border bg-surface px-2 py-1 text-xs text-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
        >
          {keep ? "Remove" : "Also keep"}
        </button>
      </div>
    </div>
  );
}

function ComparisonRowView({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <div className="sticky left-0 z-[1] border-b border-r border-border/60 bg-background px-3 py-1.5 text-xs font-medium uppercase tracking-wide text-muted">
        {label}
      </div>
      {children}
    </>
  );
}

function cellTone(tone: string, keep: boolean) {
  if (tone === "best") return "text-emerald-300";
  if (tone === "worse") return keep ? "text-amber-300" : "text-secondary";
  if (tone === "same") return "text-muted";
  return "text-foreground";
}

function SimilarityBadges({ similarity }: { similarity: ReturnType<typeof describeSimilarity> }) {
  const badges: Array<{ label: string; title: string; tone: "good" | "warn" | "info" }> = [];
  if (similarity.identicalFiles) badges.push({ label: "Identical files", title: "Same MD5 or OSHash", tone: "good" });
  else if (similarity.maxPhashDistance != null) {
    badges.push({
      label:
        similarity.maxPhashDistance === 0 ? "Visually identical" : `Visual distance ${similarity.maxPhashDistance}`,
      title: "Largest perceptual-hash difference between any two copies (0 = identical frames)",
      tone: similarity.maxPhashDistance <= 4 ? "good" : similarity.maxPhashDistance <= 8 ? "info" : "warn",
    });
  }
  if (similarity.durationSpread >= 1) {
    badges.push({
      label: `Lengths differ by ${Math.round(similarity.durationSpread)}s`,
      title: "Difference between the longest and shortest copy",
      tone: similarity.durationSpread > 30 ? "warn" : "info",
    });
  }
  return (
    <>
      {badges.map((badge) => (
        <span
          key={badge.label}
          title={badge.title}
          className={`rounded-full px-2 py-0.5 text-xs ${
            badge.tone === "good"
              ? "bg-emerald-500/15 text-emerald-300"
              : badge.tone === "warn"
                ? "bg-amber-500/15 text-amber-300"
                : "bg-surface text-secondary"
          }`}
        >
          {badge.label}
        </span>
      ))}
    </>
  );
}

function HeaderButton({
  children,
  onClick,
  disabled,
  title,
}: {
  children: ReactNode;
  onClick: (event: MouseEvent<HTMLButtonElement>) => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="inline-flex items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 py-1.5 text-sm text-secondary hover:border-accent/60 hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
    >
      {children}
    </button>
  );
}

const ResolvedGroup = forwardRef<HTMLElement, Props>(function ResolvedGroup(
  { group, focused, onFocus, onQuickView },
  ref,
) {
  const keeper = group.videos.find((video) => group.keepVideoIds.includes(video.id)) ?? group.videos[0];
  return (
    <article
      ref={ref}
      data-duplicate-group={group.id}
      onClick={onFocus}
      className={`flex items-center gap-4 rounded-xl border bg-card px-4 py-3 ${focused ? "border-accent/70" : "border-border"}`}
    >
      {keeper ? (
        <button
          type="button"
          onClick={() => onQuickView(keeper.id)}
          className="w-32 shrink-0 overflow-hidden rounded-md"
          aria-label={`Preview ${displayTitle(keeper)}`}
        >
          <VideoPreviewThumbnail video={keeper} fit="cover" coverWidth={320} enableScrubbing={false} />
        </button>
      ) : null}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <CheckCircle2 className="h-4 w-4 text-emerald-400" />
          Group {group.position + 1} resolved
        </div>
        <div className="mt-0.5 truncate text-sm text-secondary">
          {keeper ? (
            <>
              Kept <span className="text-foreground">{displayTitle(keeper)}</span>
            </>
          ) : (
            "No copies remain"
          )}
          {group.removedVideoCount > 0
            ? ` · removed ${group.removedVideoCount} ${group.removedVideoCount === 1 ? "copy" : "copies"}`
            : ""}
          {group.removedBytes > 0 && group.deleteFiles ? ` · freed ${formatFileSize(group.removedBytes)}` : ""}
          {group.resolutionAction === "merge" ? " · metadata merged" : ""}
        </div>
        {group.error ? <div className="mt-0.5 text-xs text-amber-300">{group.error}</div> : null}
        {!group.resolvedAt ? (
          <div className="mt-0.5 text-xs text-muted">The other copies were removed outside the duplicate finder.</div>
        ) : null}
      </div>
    </article>
  );
});

const IgnoredGroup = forwardRef<HTMLElement, Props>(function IgnoredGroup(
  { group, focused, busy, canIgnore, onFocus, onRestore, onQuickView },
  ref,
) {
  return (
    <article
      ref={ref}
      data-duplicate-group={group.id}
      onClick={onFocus}
      className={`flex flex-wrap items-center gap-4 rounded-xl border bg-card px-4 py-3 ${focused ? "border-accent/70" : "border-border"}`}
    >
      <div className="flex -space-x-6">
        {group.videos.slice(0, 4).map((video) => (
          <button
            key={video.id}
            type="button"
            onClick={() => onQuickView(video.id)}
            className="w-28 overflow-hidden rounded-md border-2 border-card"
            aria-label={`Preview ${displayTitle(video)}`}
          >
            <VideoPreviewThumbnail video={video} fit="cover" coverWidth={320} enableScrubbing={false} />
          </button>
        ))}
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <EyeOff className="h-4 w-4 text-muted" />
          Group {group.position + 1} · marked as not duplicates
        </div>
        <div className="mt-0.5 truncate text-sm text-muted">{group.videos.map(displayTitle).join(" · ")}</div>
      </div>
      {canIgnore ? (
        <button
          type="button"
          onClick={(event) => {
            event.stopPropagation();
            onRestore();
          }}
          disabled={busy}
          className="inline-flex items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 py-1.5 text-sm text-secondary hover:border-accent/60 hover:text-foreground disabled:opacity-40"
        >
          <Undo2 className="h-4 w-4" />
          Review again
        </button>
      ) : null}
    </article>
  );
});
