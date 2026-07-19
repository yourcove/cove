import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  ExternalLink,
  FileText,
  Image as ImageIcon,
  Info,
  ListMusic,
  Merge,
  Music,
  Repeat,
  RotateCcw,
  Settings2,
  SkipBack,
  SkipForward,
  Video,
} from "lucide-react";
import { audios, images, videos, texts } from "../api/client";
import type { GroupPlaybackManifestItem } from "../api/types";
import { AudioPlayer } from "./AudioPlayer";
import { MediaDetailLayout } from "./MediaDetailLayout/MediaDetailLayout";
import { TextViewer } from "./TextViewer";
import { VideoPlayer } from "./VideoPlayer";
import { useAppConfig } from "../state/AppConfigContext";

interface Props {
  groupId: number;
  groupName: string;
  items: GroupPlaybackManifestItem[];
  onNavigate: (r: any) => void;
  embedded?: boolean;
  backLabel?: string;
  onGoBack?: () => void;
}

type CompilationTab = "playlist" | "current" | "filters";
type MediaKind = "video" | "audio" | "image" | "text" | "unknown";
type TypeFilterKey = "videos" | "segments" | "images" | "texts" | "audios";

const DEFAULT_IMAGE_DISPLAY_DURATION_SEC = 6;
const DEFAULT_TEXT_DISPLAY_DURATION_SEC = 12;

export function CompilationPlayer({
  groupId,
  groupName,
  items,
  onNavigate,
  embedded = false,
  backLabel,
  onGoBack,
}: Props) {
  const { config, configLoading } = useAppConfig();
  const automaticPlayback = config?.ui.autostartVideo ?? false;
  const playbackIntentSetRef = useRef(false);
  const seekRef = useRef<((time: number) => void) | null>(null);
  const [currentItemIndex, setCurrentItemIndex] = useState(0);
  const [loopCompilation, setLoopCompilation] = useState(false);
  const [autostart, setAutostart] = useState(automaticPlayback);
  const [autostartToken, setAutostartToken] = useState(0);
  const [activeTab, setActiveTab] = useState<CompilationTab>("playlist");
  const [enabledTypes, setEnabledTypes] = useState<Record<TypeFilterKey, boolean>>({
    videos: true,
    segments: true,
    images: true,
    texts: true,
    audios: true,
  });
  const [imageDisplayDurationSec, setImageDisplayDurationSec] = useState(DEFAULT_IMAGE_DISPLAY_DURATION_SEC);
  const [textDisplayDurationSec, setTextDisplayDurationSec] = useState(DEFAULT_TEXT_DISPLAY_DURATION_SEC);

  useEffect(() => {
    if (configLoading || playbackIntentSetRef.current) return;
    setAutostart(automaticPlayback);
  }, [automaticPlayback, configLoading]);

  const visibleItems = useMemo(
    () => items.filter((manifestItem) => enabledTypes[getTypeFilterKey(manifestItem)]),
    [enabledTypes, items],
  );

  useEffect(() => {
    if (visibleItems.length === 0) {
      setCurrentItemIndex(0);
      return;
    }

    setCurrentItemIndex((index) => Math.min(index, visibleItems.length - 1));
  }, [visibleItems.length]);

  const item = visibleItems[currentItemIndex];
  const nextItem = visibleItems[currentItemIndex + 1] ?? (loopCompilation ? visibleItems[0] : undefined);
  const currentVideoId = getVideoId(item);
  const currentAudioId = getAudioId(item);
  const currentImageId = getImageId(item);
  const currentTextId = getTextId(item);
  const nextVideoId = getVideoId(nextItem);
  const nextAudioId = getAudioId(nextItem);
  const nextImageId = getImageId(nextItem);
  const nextTextId = getTextId(nextItem);

  const { data: currentVideo, isLoading: currentVideoLoading } = useQuery({
    queryKey: ["video", currentVideoId],
    queryFn: () => videos.get(currentVideoId!),
    enabled: currentVideoId != null,
  });
  const { data: currentAudio, isLoading: currentAudioLoading } = useQuery({
    queryKey: ["audio", currentAudioId],
    queryFn: () => audios.get(currentAudioId!),
    enabled: currentAudioId != null,
  });
  const { data: currentTextContent, isLoading: currentTextLoading, isError: currentTextError } = useQuery({
    queryKey: ["text-content", currentTextId],
    queryFn: () => texts.content(currentTextId!),
    enabled: currentTextId != null,
  });
  useQuery({
    queryKey: ["video", nextVideoId],
    queryFn: () => videos.get(nextVideoId!),
    enabled: nextVideoId != null,
    staleTime: 60_000,
  });
  useQuery({
    queryKey: ["audio", nextAudioId],
    queryFn: () => audios.get(nextAudioId!),
    enabled: nextAudioId != null,
    staleTime: 60_000,
  });
  useQuery({
    queryKey: ["text-content", nextTextId],
    queryFn: () => texts.content(nextTextId!),
    enabled: nextTextId != null,
    staleTime: 60_000,
  });

  const currentFile = currentVideo?.files[0];
  const currentAudioFile = currentAudio?.files
    .slice()
    .sort((left, right) => (right.duration - left.duration) || (left.id - right.id))[0];
  const mediaKind = getMediaKind(item);
  const itemIsVideo = mediaKind === "video";
  const itemIsAudio = mediaKind === "audio";
  const itemIsImage = mediaKind === "image";
  const itemIsText = mediaKind === "text";
  const itemLoading = itemIsAudio ? currentAudioLoading : itemIsVideo ? currentVideoLoading : itemIsText ? currentTextLoading : false;
  const currentPlayable = itemIsAudio
    ? currentAudioId != null
    : itemIsVideo
      ? !!currentFile
      : itemIsImage
        ? currentImageId != null
        : itemIsText
          ? currentTextId != null
          : false;
  const displayDurationSec = item ? getDisplayDurationSec(item, imageDisplayDurationSec, textDisplayDurationSec) : 0;
  const clipEnd = item
    ? itemIsImage || itemIsText
      ? displayDurationSec
      : item.endSec ?? currentFile?.duration ?? currentAudioFile?.duration ?? item.startSec + (item.durationSec ?? 0)
    : 0;
  const clipDuration = item
    ? itemIsImage || itemIsText
      ? displayDurationSec
      : Math.max(0, clipEnd - item.startSec)
    : 0;

  useEffect(() => {
    seekRef.current = null;
  }, [item?.groupItemId]);

  useEffect(() => {
    if (!autostart || !item || itemLoading || !currentPlayable) {
      return;
    }

    seekRef.current?.(item.startSec);
  }, [autostart, autostartToken, currentPlayable, item, itemLoading]);

  const moveToItem = useCallback((nextIndex: number, shouldAutoPlay = false) => {
    if (visibleItems.length === 0) {
      return;
    }

    const boundedIndex = Math.min(visibleItems.length - 1, Math.max(0, nextIndex));
    if (!shouldAutoPlay) playbackIntentSetRef.current = true;
    if (shouldAutoPlay) {
      setAutostart(true);
      setAutostartToken((value) => value + 1);
    } else {
      setAutostart(false);
    }
    setCurrentItemIndex(boundedIndex);
  }, [visibleItems.length]);

  const advanceToNextItem = useCallback(() => {
    if (currentItemIndex + 1 < visibleItems.length) {
      moveToItem(currentItemIndex + 1, true);
      return;
    }

    if (loopCompilation && visibleItems.length > 0) {
      moveToItem(0, true);
    }
  }, [currentItemIndex, loopCompilation, moveToItem, visibleItems.length]);

  useEffect(() => {
    if (!autostart || !item || itemLoading || (!itemIsImage && !itemIsText) || displayDurationSec <= 0) {
      return;
    }

    const timeoutId = window.setTimeout(() => advanceToNextItem(), displayDurationSec * 1000);
    return () => window.clearTimeout(timeoutId);
  }, [advanceToNextItem, autostart, displayDurationSec, item, itemIsImage, itemIsText, itemLoading]);

  const restartItem = useCallback(() => {
    if (!item) {
      return;
    }

    setAutostart(true);
    setAutostartToken((value) => value + 1);
    seekRef.current?.(item.startSec);
  }, [item]);

  const toggleType = useCallback((key: TypeFilterKey) => {
    setEnabledTypes((current) => ({ ...current, [key]: !current[key] }));
  }, []);

  const openCurrentItem = useCallback(() => {
    if (!item) {
      return;
    }

    if (item.segmentId != null) {
      onNavigate({ page: "segment", id: item.segmentId });
      return;
    }
    if (currentAudioId != null) {
      onNavigate({ page: "audio", id: currentAudioId });
      return;
    }
    if (currentImageId != null) {
      onNavigate({ page: "image", id: currentImageId });
      return;
    }
    if (currentTextId != null) {
      onNavigate({ page: "text", id: currentTextId });
      return;
    }
    if (currentVideoId != null) {
      onNavigate({ page: "video", id: currentVideoId, seekTo: item.startSec });
    }
  }, [currentAudioId, currentImageId, currentVideoId, currentTextId, item, onNavigate]);

  const tabs = useMemo(() => [
    { key: "playlist", label: "Playlist", icon: <ListMusic className="h-4 w-4" />, count: visibleItems.length },
    { key: "current", label: "Current", icon: <Info className="h-4 w-4" /> },
    { key: "filters", label: "Filters", icon: <Settings2 className="h-4 w-4" />, count: items.length - visibleItems.length },
  ], [items.length, visibleItems.length]);

  const currentTitle = item ? getItemTitle(item) : "No playable item";
  const currentPlaybackTracking = item ? {
    hostType: "group",
    hostId: groupId,
    surface: "compilation",
    scopeKey: `group:${groupId}`,
    parentHostType: "group",
    parentHostId: groupId,
    itemHostType: item.hostType,
    itemHostId: item.hostId,
    groupItemId: item.groupItemId,
    segmentId: item.segmentId ?? undefined,
    clipStartSec: item.startSec,
    clipEndSec: item.endSec ?? null,
    context: {
      videoId: item.videoId ?? undefined,
      audioId: item.audioId ?? undefined,
      imageId: item.imageId ?? undefined,
      textId: item.textId ?? undefined,
      segmentId: item.segmentId ?? undefined,
      itemIndex: currentItemIndex,
    },
  } : undefined;
  const playerMedia = (
    <div className="relative flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black">
      {renderPreloads(nextVideoId, nextAudioId, nextImageId)}
      {!item ? (
        <div className="flex flex-1 items-center justify-center px-6 text-center text-sm text-secondary">
          No compilation items match the active filters.
        </div>
      ) : itemLoading ? (
        <div className="flex flex-1 items-center justify-center text-sm text-secondary">
          Loading item playback...
        </div>
      ) : itemIsVideo && currentFile && currentVideoId != null ? (
        <div className="flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black">
          <VideoPlayer
            streamUrl={videos.streamUrl(currentVideoId)}
            posterUrl={item.posterPath ?? videos.screenshotUrl(currentVideoId)}
            format={currentFile.format}
            audioCodec={currentFile.audioCodec}
            duration={currentFile.duration}
            resumeTime={item.startSec}
            videoId={currentVideoId}
            detections={[]}
            captions={currentFile.captions}
            onPlay={() => setAutostart(false)}
            onSeekRegister={(fn) => {
              seekRef.current = fn;
              if (autostart && item && !itemLoading && currentPlayable) {
                fn(item.startSec);
              }
            }}
            autostart={autostart}
            autostartToken={autostartToken}
            playbackTracking={currentPlaybackTracking}
            onEnded={advanceToNextItem}
            clip={{ start: item.startSec, end: item.endSec ?? currentFile.duration, loop: false }}
          />
        </div>
      ) : itemIsAudio && currentAudioId != null ? (
        <div className="flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black p-4 sm:p-6">
          <AudioPlayer
            streamUrl={audios.streamUrl(currentAudioId)}
            format={currentAudioFile?.format ?? item.format ?? "audio"}
            duration={currentAudioFile?.duration ?? item.durationSec ?? 0}
            title={item.title || currentAudio?.title || currentAudioFile?.basename || `Audio ${currentAudioId}`}
            subtitle={[currentAudio?.performers?.map((performer) => performer.name).filter(Boolean).join(", "), currentAudio?.studioName].filter(Boolean).join(" • ") || undefined}
            hasVideoTrack={currentAudioFile?.hasVideoTrack ?? item.hasVideoTrack}
            resumeTime={item.startSec}
            onPlay={() => setAutostart(false)}
            onSeekRegister={(fn) => {
              seekRef.current = fn;
              if (autostart && item && !itemLoading && currentPlayable) {
                fn(item.startSec);
              }
            }}
            autostart={autostart}
            autostartToken={autostartToken}
            playbackTracking={currentPlaybackTracking}
            onEnded={advanceToNextItem}
            clip={{ start: item.startSec, end: item.endSec ?? null, loop: false }}
          />
        </div>
      ) : itemIsImage && currentImageId != null ? (
        <div className="flex min-h-0 flex-1 items-center justify-center bg-black p-4">
          <img
            src={item.src || images.imageUrl(currentImageId)}
            alt={currentTitle}
            className="max-h-full max-w-full object-contain"
          />
        </div>
      ) : itemIsText && currentTextId != null ? (
        <div className="flex min-h-0 flex-1 bg-background p-3 sm:p-4">
          {currentTextError || currentTextContent?.format?.trim().toLowerCase() === "pdf" ? (
            <iframe
              title={currentTitle}
              src={item.src || texts.fileUrl(currentTextId)}
              className="h-full min-h-[28rem] w-full rounded-lg border border-border bg-surface"
            />
          ) : (
            <TextViewer
              content={currentTextContent?.content}
              renderMode={currentTextContent?.renderMode}
              className="min-h-0 flex-1 rounded-lg"
            />
          )}
        </div>
      ) : (
        <div className="flex flex-1 items-center justify-center text-sm text-secondary">
          No playable media is available for this group item.
        </div>
      )}
    </div>
  );

  const playbackControls = (
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
      <IconButton
        label="Previous item"
        title="Previous item"
        onClick={() => moveToItem(currentItemIndex - 1, true)}
        disabled={!item || (currentItemIndex === 0 && !loopCompilation)}
        icon={<SkipBack className="h-4 w-4" />}
      />
      <IconButton
        label="Next item"
        title="Next item"
        onClick={() => moveToItem(currentItemIndex + 1, true)}
        disabled={!item || (currentItemIndex >= visibleItems.length - 1 && !loopCompilation)}
        icon={<SkipForward className="h-4 w-4" />}
      />
      <IconButton
        label="Loop compilation"
        title="Loop compilation"
        onClick={() => setLoopCompilation((value) => !value)}
        active={loopCompilation}
        icon={<Repeat className="h-4 w-4" />}
      />
    </div>
  );

  const sidebarContent = (
    <div className="space-y-4">
      {playbackControls}
      {activeTab === "playlist" ? (
        <div className="space-y-2">
          {visibleItems.map((manifestItem, index) => {
            const active = index === currentItemIndex;
            return (
              <button
                key={manifestItem.groupItemId}
                type="button"
                onClick={() => moveToItem(index)}
                className={`flex w-full min-w-0 items-center gap-3 rounded-lg border px-3 py-2 text-left transition-colors ${active ? "border-accent bg-accent/10 text-accent" : "border-border bg-card/60 text-secondary hover:border-accent hover:text-foreground"}`}
              >
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-current/20 bg-background/70">
                  {getItemIcon(manifestItem)}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium">{index + 1}. {getItemTitle(manifestItem)}</span>
                  <span className="mt-0.5 block text-xs text-muted">{getItemTypeLabel(manifestItem)}</span>
                </span>
              </button>
            );
          })}
        </div>
      ) : null}
      {activeTab === "current" ? (
        <div className="space-y-4">
          <section className="rounded-lg border border-border bg-card/70 p-4">
            <div className="flex items-center gap-3">
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-md border border-border bg-background text-accent">
                {item ? getItemIcon(item) : <Info className="h-4 w-4" />}
              </span>
              <div className="min-w-0">
                <div className="truncate text-sm font-semibold text-foreground">{currentTitle}</div>
                <div className="text-xs text-muted">{item ? getItemTypeLabel(item) : "Filtered"}</div>
              </div>
            </div>
            <dl className="mt-4 grid gap-3 text-sm">
              <MetadataRow label="Position" value={item ? `${currentItemIndex + 1}/${visibleItems.length}` : "-"} />
              <MetadataRow label="Start" value={item && !itemIsImage && !itemIsText ? formatTime(item.startSec) : "-"} />
              <MetadataRow label="Duration" value={clipDuration > 0 ? formatTime(clipDuration) : "-"} />
              <MetadataRow label="Format" value={item?.format || "-"} />
            </dl>
          </section>
          <div className="grid gap-2">
            <button
              type="button"
              onClick={openCurrentItem}
              disabled={!item}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              <ExternalLink className="h-4 w-4" />
              Open item
            </button>
            <button
              type="button"
              onClick={() => onNavigate({ page: "group", id: groupId })}
              className="rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
            >
              Back to group
            </button>
          </div>
        </div>
      ) : null}
      {activeTab === "filters" ? (
        <div className="space-y-4">
          <section className="rounded-lg border border-border bg-card/70 p-4">
            <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-muted">Item Types</div>
            <div className="space-y-2">
              <TypeToggle label="Videos" checked={enabledTypes.videos} onChange={() => toggleType("videos")} count={countItems(items, "videos")} />
              <TypeToggle label="Segments" checked={enabledTypes.segments} onChange={() => toggleType("segments")} count={countItems(items, "segments")} />
              <TypeToggle label="Images" checked={enabledTypes.images} onChange={() => toggleType("images")} count={countItems(items, "images")} />
              <TypeToggle label="Texts" checked={enabledTypes.texts} onChange={() => toggleType("texts")} count={countItems(items, "texts")} />
              <TypeToggle label="Audios" checked={enabledTypes.audios} onChange={() => toggleType("audios")} count={countItems(items, "audios")} />
            </div>
          </section>
          <section className="rounded-lg border border-border bg-card/70 p-4">
            <div className="mb-3 text-xs font-semibold uppercase tracking-wide text-muted">Display Duration</div>
            <NumberSetting label="Images" value={imageDisplayDurationSec} onChange={setImageDisplayDurationSec} min={0} max={300} />
            <NumberSetting label="Texts" value={textDisplayDurationSec} onChange={setTextDisplayDurationSec} min={0} max={600} />
          </section>
        </div>
      ) : null}
    </div>
  );

  if (!embedded) {
    return (
      <MediaDetailLayout
        title={groupName}
        subtitle={
          <div className="flex flex-wrap items-center gap-3 text-sm text-secondary">
            <span>{visibleItems.length}/{items.length} item{items.length === 1 ? "" : "s"}</span>
            {item ? <span>Now playing {currentItemIndex + 1}/{visibleItems.length}</span> : null}
            {clipDuration > 0 ? <span>{formatTime(clipDuration)}</span> : null}
          </div>
        }
        backLabel={backLabel}
        onGoBack={onGoBack}
        media={playerMedia}
        mediaAspectRatio="auto"
        mediaFullBleed
        mediaSticky={false}
        tabs={tabs}
        activeTab={activeTab}
        onTabChange={(key) => setActiveTab(key as CompilationTab)}
      >
        <MediaDetailLayout.Content>{sidebarContent}</MediaDetailLayout.Content>
      </MediaDetailLayout>
    );
  }

  return (
    <article className="flex h-full flex-col bg-card/80">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-border px-5 py-4">
        <div>
          <div className="text-xs font-semibold uppercase tracking-wide text-muted">Compilation Playback</div>
          <h2 className="mt-2 text-xl font-semibold text-foreground">{groupName}</h2>
        </div>
        <div className="flex flex-wrap gap-2 text-xs text-secondary">
          <span className="rounded-full border border-border bg-surface px-2 py-1">{visibleItems.length}/{items.length} item{items.length === 1 ? "" : "s"}</span>
          {item ? <span className="rounded-full border border-border bg-surface px-2 py-1">Now playing {currentItemIndex + 1}/{visibleItems.length}</span> : null}
        </div>
      </div>

      {playerMedia}

      <div className="space-y-4 p-5">{sidebarContent}</div>
    </article>
  );
}

function IconButton({
  label,
  title,
  icon,
  onClick,
  disabled,
  active,
}: {
  label: string;
  title: string;
  icon: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  active?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      aria-label={label}
      className={`inline-flex h-10 items-center justify-center gap-2 rounded-lg border px-2 text-sm transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${active ? "border-accent bg-accent/10 text-accent" : "border-border text-foreground hover:border-accent"}`}
    >
      {icon}
      <span className="hidden sm:inline">{label}</span>
    </button>
  );
}

function MetadataRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <dt className="text-xs uppercase tracking-wide text-muted">{label}</dt>
      <dd className="min-w-0 truncate text-right text-secondary">{value}</dd>
    </div>
  );
}

function TypeToggle({ label, checked, onChange, count }: { label: string; checked: boolean; onChange: () => void; count: number }) {
  return (
    <label className="flex items-center justify-between gap-3 rounded-md border border-border bg-background/60 px-3 py-2 text-sm text-foreground">
      <span className="inline-flex items-center gap-2">
        <input type="checkbox" checked={checked} onChange={onChange} className="h-4 w-4 accent-[var(--accent)]" />
        {label}
      </span>
      <span className="text-xs text-muted">{count}</span>
    </label>
  );
}

function NumberSetting({ label, value, onChange, min, max }: { label: string; value: number; onChange: (value: number) => void; min: number; max: number }) {
  return (
    <label className="mt-3 flex items-center justify-between gap-3 text-sm first:mt-0">
      <span className="text-secondary">{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        step={1}
        value={value}
        onChange={(event) => onChange(clampNumber(Number(event.target.value), min, max))}
        className="h-9 w-24 rounded-md border border-border bg-background px-2 text-right text-foreground outline-none focus:border-accent"
      />
    </label>
  );
}

function renderPreloads(nextVideoId?: number, nextAudioId?: number, nextImageId?: number) {
  if (nextVideoId == null && nextAudioId == null && nextImageId == null) {
    return null;
  }

  return (
    <div className="hidden" aria-hidden="true">
      {nextVideoId != null ? <video preload="auto" src={videos.streamUrl(nextVideoId)} /> : null}
      {nextAudioId != null ? <audio preload="auto" src={audios.streamUrl(nextAudioId)} /> : null}
      {nextImageId != null ? <img src={images.imageUrl(nextImageId)} alt="" /> : null}
    </div>
  );
}

function formatTime(value: number) {
  const totalHundredths = Math.max(0, Math.round(value * 100));
  const hours = Math.floor(totalHundredths / 360000);
  const minutes = Math.floor((totalHundredths % 360000) / 6000);
  const seconds = Math.floor((totalHundredths % 6000) / 100);
  const hundredths = totalHundredths % 100;

  if (hundredths === 0) {
    if (hours > 0) {
      return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    }

    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  }

  const fractional = hundredths % 10 === 0
    ? String(Math.floor(hundredths / 10))
    : String(hundredths).padStart(2, "0");

  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${fractional}`;
  }

  return `${minutes}:${String(seconds).padStart(2, "0")}.${fractional}`;
}

function getMediaKind(item?: GroupPlaybackManifestItem): MediaKind {
  if (!item) return "unknown";
  if (getImageId(item) != null) return "image";
  if (getTextId(item) != null) return "text";
  if (getAudioId(item) != null) return "audio";
  if (getVideoId(item) != null) return "video";
  return "unknown";
}

function getTypeFilterKey(item: GroupPlaybackManifestItem): TypeFilterKey {
  if (isSegmentItem(item)) return "segments";
  switch (getMediaKind(item)) {
    case "audio": return "audios";
    case "image": return "images";
    case "text": return "texts";
    case "video": return "videos";
    default: return "videos";
  }
}

function countItems(items: GroupPlaybackManifestItem[], key: TypeFilterKey) {
  return items.filter((item) => getTypeFilterKey(item) === key).length;
}

function getDisplayDurationSec(item: GroupPlaybackManifestItem, imageDurationSec: number, textDurationSec: number) {
  if (getImageId(item) != null) {
    return item.displayDurationSec ?? imageDurationSec;
  }

  if (getTextId(item) != null) {
    return item.displayDurationSec ?? textDurationSec;
  }

  return item.durationSec ?? 0;
}

function isSegmentItem(item?: GroupPlaybackManifestItem) {
  return item?.hostType === "segment" || item?.segmentId != null;
}

function getVideoId(item?: GroupPlaybackManifestItem) {
  if (!item) {
    return undefined;
  }

  return item.videoId ?? (item.hostType === "video" ? item.hostId : undefined);
}

function getAudioId(item?: GroupPlaybackManifestItem) {
  if (!item) {
    return undefined;
  }

  return item.audioId ?? (item.hostType === "audio" ? item.hostId : undefined);
}

function getImageId(item?: GroupPlaybackManifestItem) {
  if (!item) {
    return undefined;
  }

  return item.imageId ?? (item.hostType === "image" ? item.hostId : undefined);
}

function getTextId(item?: GroupPlaybackManifestItem) {
  if (!item) {
    return undefined;
  }

  return item.textId ?? (item.hostType === "text" ? item.hostId : undefined);
}

function getItemTitle(item: GroupPlaybackManifestItem) {
  return item.title || item.videoTitle || `Untitled ${getItemTypeLabel(item).toLowerCase()}`;
}

function getItemTypeLabel(item: GroupPlaybackManifestItem) {
  if (isSegmentItem(item)) return "Segment";
  switch (getMediaKind(item)) {
    case "audio": return "Audio";
    case "image": return "Image";
    case "text": return "Text";
    case "video": return "Video";
    default: return "Item";
  }
}

function getItemIcon(item: GroupPlaybackManifestItem) {
  if (isSegmentItem(item)) return <Merge className="h-4 w-4" />;
  switch (getMediaKind(item)) {
    case "audio": return <Music className="h-4 w-4" />;
    case "image": return <ImageIcon className="h-4 w-4" />;
    case "text": return <FileText className="h-4 w-4" />;
    case "video": return <Video className="h-4 w-4" />;
    default: return <Info className="h-4 w-4" />;
  }
}

function clampNumber(value: number, min: number, max: number) {
  if (!Number.isFinite(value)) {
    return min;
  }

  return Math.min(max, Math.max(min, Math.round(value)));
}
