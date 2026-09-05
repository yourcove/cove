import type { ResolvedSpan, Video } from "../../../api/types";
import { TextInput } from "../../../components/EditModal";
import { formatSeconds } from "./types";

interface PreviewCardData {
  title: string;
  spans: ResolvedSpan[];
  loading: boolean;
}

interface Props {
  title: string;
  description: string;
  previewVideo?: Video;
  previewVideoSearch?: string;
  onPreviewVideoSearchChange?: (value: string) => void;
  previewVideoResults?: Video[];
  onSelectPreviewVideo?: (videoId: number) => void;
  cards: PreviewCardData[];
  emptyMessage: string;
}

export function RulesPreviewPane({
  title,
  description,
  previewVideo,
  previewVideoSearch,
  onPreviewVideoSearchChange,
  previewVideoResults = [],
  onSelectPreviewVideo,
  cards,
  emptyMessage,
}: Props) {
  const trimmedSearch = previewVideoSearch?.trim() ?? "";

  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h4 className="text-sm font-semibold uppercase tracking-wide text-muted">{title}</h4>
          <p className="mt-1 text-sm text-secondary">{description}</p>
        </div>
        {previewVideo ? (
          <div className="rounded-full bg-surface px-3 py-1 text-xs text-secondary">
            {previewVideo.title || `Video #${previewVideo.id}`}
          </div>
        ) : null}
      </div>

      {onPreviewVideoSearchChange ? (
        <div className="mt-4 space-y-3">
          <TextInput
            value={previewVideoSearch ?? ""}
            onChange={onPreviewVideoSearchChange}
            placeholder="Search videos for preview..."
          />
          {trimmedSearch ? (
            <div className="max-h-44 overflow-y-auto rounded-xl border border-border bg-surface/40">
              {previewVideoResults.length === 0 ? (
                <div className="px-4 py-3 text-sm text-secondary">No videos found.</div>
              ) : (
                previewVideoResults.map((video) => (
                  <button
                    key={video.id}
                    type="button"
                    onClick={() => onSelectPreviewVideo?.(video.id)}
                    className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left text-sm text-foreground transition-colors hover:bg-card"
                  >
                    <span>{video.title || `Video #${video.id}`}</span>
                    <span className="text-xs text-muted">#{video.id}</span>
                  </button>
                ))
              )}
            </div>
          ) : null}
        </div>
      ) : null}

      {previewVideo ? (
        <div className={`mt-4 grid gap-4 ${cards.length > 1 ? "md:grid-cols-2" : ""}`}>
          {cards.map((card) => (
            <SpanPreviewCard
              key={card.title}
              title={card.title}
              video={previewVideo}
              spans={card.spans}
              loading={card.loading}
            />
          ))}
        </div>
      ) : (
        <div className="mt-4 rounded-xl border border-dashed border-border bg-surface/30 px-4 py-5 text-sm text-secondary">
          {emptyMessage}
        </div>
      )}
    </div>
  );
}

function SpanPreviewCard({
  title,
  video,
  spans,
  loading,
}: {
  title: string;
  video?: Video;
  spans: ResolvedSpan[];
  loading: boolean;
}) {
  const maxEnd = spans.reduce((max, span) => Math.max(max, span.endSec), 0);

  return (
    <div className="rounded-xl border border-border bg-surface/40 p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className="text-sm font-medium text-foreground">{title}</div>
          <div className="mt-1 text-xs text-secondary">
            {video ? video.title || `Video #${video.id}` : "No preview video selected"}
          </div>
        </div>
        <div className="text-xs text-muted">
          {spans.length} span{spans.length === 1 ? "" : "s"}
        </div>
      </div>

      {loading ? <div className="mt-4 text-sm text-secondary">Loading preview...</div> : null}
      {!loading && spans.length === 0 ? (
        <div className="mt-4 text-sm text-secondary">No spans would be shown for this video.</div>
      ) : null}
      {!loading && spans.length > 0 ? (
        <div className="mt-4 space-y-3">
          {spans.slice(0, 8).map((span) => {
            const left = maxEnd > 0 ? (span.startSec / maxEnd) * 100 : 0;
            const width = maxEnd > 0 ? Math.max(((span.endSec - span.startSec) / maxEnd) * 100, 2) : 100;
            return (
              <div key={span.spanKey} className="space-y-1">
                <div className="relative h-3 overflow-hidden rounded-full bg-card">
                  <div
                    className="absolute top-0 h-full rounded-full"
                    style={{
                      left: `${left}%`,
                      width: `${width}%`,
                      backgroundColor: span.colorHint ?? "#60a5fa",
                    }}
                  />
                </div>
                <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-secondary">
                  <span>{span.tagName || span.kind || span.sourceKey || span.spanKey}</span>
                  <span>
                    {formatSeconds(span.startSec)} - {formatSeconds(span.endSec)}
                  </span>
                </div>
              </div>
            );
          })}
          {spans.length > 8 ? <div className="text-xs text-muted">Showing the first 8 spans.</div> : null}
        </div>
      ) : null}
    </div>
  );
}
