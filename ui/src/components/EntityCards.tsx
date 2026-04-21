import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import { scenes, images, performers, galleries, studios, groups, entityImages } from "../api/client";
import type { Gallery, Group, Image, Performer, Scene, Studio } from "../api/types";
import { formatDuration, formatFileSize, getResolutionLabel } from "./shared";
import { RatingBanner, RatingBadge } from "./Rating";
import { Building2, FolderOpen, Layers, Tag, User, Film, MapPin, Box, Images as ImagesIcon, Heart, Eye } from "lucide-react";
import { createCardNavigationHandlers, createNestedCardNavigationHandlers } from "./cardNavigation";

function createNestedEntityNavigationHandlers<T extends HTMLElement>(route: { page: string; id: number }, onNavigate?: (route: any) => void) {
  return createNestedCardNavigationHandlers<T>(route, () => onNavigate?.(route));
}

function FavoriteCounter({ count }: { count: number }) {
  return (
    <span className="flex items-center gap-1 p-1 text-muted" title={`Favorites: ${count}`}>
      <Heart className="h-3.5 w-3.5 fill-accent text-accent" />
      <span className="text-xs">{count}</span>
    </span>
  );
}

// ===== PopoverButton (shared hover popover) =====

export function PopoverButton({ icon, count, title, children, wide, preferBelow }: { icon: React.ReactNode; count: number; title: string; children?: React.ReactNode; wide?: boolean; preferBelow?: boolean }) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const enterTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const leaveTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [popoverStyle, setPopoverStyle] = useState<React.CSSProperties>({});

  const handleMouseEnter = useCallback(() => {
    clearTimeout(leaveTimer.current);
    enterTimer.current = setTimeout(() => {
      if (buttonRef.current) {
        const rect = buttonRef.current.getBoundingClientRect();
        const spaceBelow = window.innerHeight - rect.bottom;
        const showBelow = preferBelow ? (spaceBelow > 100) : (rect.top < 220);
        const style: React.CSSProperties = { position: "fixed", zIndex: 9999 };
        if (showBelow) { style.top = rect.bottom + 4; } else { style.bottom = window.innerHeight - rect.top + 4; }
        const centerX = rect.left + rect.width / 2;
        const popWidth = wide ? 300 : 220;
        let left = centerX - popWidth / 2;
        if (left < 8) left = 8;
        if (left + popWidth > window.innerWidth - 8) left = window.innerWidth - 8 - popWidth;
        style.left = left;
        setPopoverStyle(style);
      }
      setOpen(true);
    }, 200);
  }, [preferBelow, wide]);

  const handleMouseLeave = useCallback(() => {
    clearTimeout(enterTimer.current);
    leaveTimer.current = setTimeout(() => setOpen(false), 200);
  }, []);

  useEffect(() => () => { clearTimeout(enterTimer.current); clearTimeout(leaveTimer.current); }, []);

  return (
    <div className="relative" ref={buttonRef} onMouseEnter={handleMouseEnter} onMouseLeave={handleMouseLeave}>
      <button
        className="flex items-center gap-1 px-1.5 py-1 text-secondary hover:text-accent rounded text-xs transition-colors"
        title={title}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
        onAuxClick={(e) => e.stopPropagation()}
      >
        {icon}
        <span className="font-medium">{count}</span>
      </button>
      {open && children && createPortal(
        <div
          ref={popoverRef}
          style={popoverStyle}
          className={`bg-surface border border-border rounded-lg shadow-2xl shadow-black/40 p-2.5 ${wide ? "min-w-[280px] max-w-[360px]" : "min-w-[180px] max-w-[min(280px,calc(100vw-1rem))]"} max-h-[320px] overflow-y-auto`}
          onClick={(e) => e.stopPropagation()}
          onMouseEnter={() => { clearTimeout(leaveTimer.current); }}
          onMouseLeave={handleMouseLeave}
        >
          <div className="text-xs uppercase tracking-wider text-muted font-semibold mb-1.5 px-1">{title}</div>
          {children}
        </div>,
        document.body
      )}
    </div>
  );
}

// ===== Lazy scene list popover content =====

export function ScenesPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["scenes-popover", filter],
    queryFn: () => scenes.find({ perPage: 10, sort: "date", direction: "desc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No scenes</p>;
  return (
    <div className="space-y-1">
      {items.map((s) => (
        <div key={s.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          <img src={scenes.screenshotUrl(s.id)} alt="" className="w-12 h-7 rounded object-cover flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
          <span className="text-[11px] text-foreground truncate">{s.title || "Untitled"}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy image list popover content =====

export function ImagesPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["images-popover", filter],
    queryFn: () => images.find({ perPage: 10, sort: "created_at", direction: "desc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No images</p>;
  return (
    <div className="grid grid-cols-3 gap-1">
      {items.map((img) => (
        <div key={img.id} className="aspect-square rounded overflow-hidden bg-surface">
          <img src={images.thumbnailUrl(img.id)} alt="" className="w-full h-full object-cover" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="col-span-3 text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy performer list popover content =====

export function PerformersPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["performers-popover", filter],
    queryFn: () => performers.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No performers</p>;
  return (
    <div className="grid grid-cols-2 gap-2">
      {items.map((p) => (
        <div key={p.id} className="flex flex-col items-center gap-1 text-center p-1.5 rounded hover:bg-card-hover transition-colors">
          <div className="w-12 h-16 rounded overflow-hidden bg-surface flex-shrink-0">
            {p.imagePath ? <img src={p.imagePath} alt="" className="w-full h-full object-cover" loading="lazy" /> : <div className="w-full h-full flex items-center justify-center"><User className="w-5 h-5 text-muted" /></div>}
          </div>
          <span className="text-[11px] text-foreground truncate w-full font-medium">{p.name}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="col-span-2 text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy gallery list popover content =====

export function GalleriesPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["galleries-popover", filter],
    queryFn: () => galleries.find({ perPage: 10, sort: "title", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No galleries</p>;
  return (
    <div className="space-y-1">
      {items.map((g) => (
        <div key={g.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          {g.coverPath ? <img src={g.coverPath} alt="" className="w-10 h-7 rounded object-cover flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} /> : <FolderOpen className="w-4 h-4 text-muted flex-shrink-0" />}
          <span className="text-[11px] text-foreground truncate">{g.title || "Untitled"}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy studio list popover content =====

export function StudiosPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["studios-popover", filter],
    queryFn: () => studios.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No studios</p>;
  return (
    <div className="space-y-1">
      {items.map((s) => (
        <div key={s.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          {s.imagePath ? <img src={s.imagePath} alt="" className="w-10 h-7 rounded object-contain flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} /> : <Building2 className="w-4 h-4 text-muted flex-shrink-0" />}
          <span className="text-[11px] text-foreground truncate">{s.name}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy group list popover content =====

export function GroupsPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["groups-popover", filter],
    queryFn: () => groups.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading…</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No groups</p>;
  return (
    <div className="space-y-1">
      {items.map((g) => (
        <div key={g.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          {g.frontImagePath ? <img src={g.frontImagePath} alt="" className="w-7 h-10 rounded object-cover flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} /> : <Layers className="w-4 h-4 text-muted flex-shrink-0" />}
          <span className="text-[11px] text-foreground truncate">{g.name}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== SceneCardPopovers =====

export function SceneCardPopovers({ scene, onNavigate }: { scene: Scene; onNavigate?: (r: any) => void }) {
  const hasPopovers =
    scene.tags.length > 0 || scene.performers.length > 0 || scene.groups.length > 0 ||
    scene.galleries.length > 0 || scene.markers.length > 0 || scene.oCounter > 0 || scene.organized;
  return (
    <>
      <hr className="border-border/50 my-0" />
      <div className="flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
        {!hasPopovers && <span className="text-[10px] text-muted/30 select-none">&nbsp;</span>}
        {scene.performers.length > 0 && (
          <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={scene.performers.length} title="Performers" wide preferBelow>
            <div className="grid grid-cols-2 gap-2">
              {scene.performers.map((p: any) => {
                const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "performer", id: p.id }, onNavigate);

                return (
                <button key={p.id} type="button" {...navigationHandlers}
                  className="flex flex-col items-center gap-1.5 text-center cursor-pointer rounded hover:bg-card-hover p-1.5 group/perf transition-colors">
                  <div className="w-20 h-28 rounded overflow-hidden bg-surface flex-shrink-0">
                    {p.imagePath ? <img src={p.imagePath} alt="" className="w-full h-full object-cover" /> : <div className="w-full h-full flex items-center justify-center"><User className="w-8 h-8 text-muted" /></div>}
                  </div>
                  <span className="text-xs text-accent group-hover/perf:underline truncate w-full font-medium">{p.name}</span>
                </button>
              );})}
            </div>
          </PopoverButton>
        )}
        {scene.tags.length > 0 && (
          <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={scene.tags.length} title="Tags" preferBelow>
            <div className="flex flex-wrap gap-1">
              {scene.tags.map((t: any) => {
                const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "tag", id: t.id }, onNavigate);

                return (
                <button key={t.id} type="button" {...navigationHandlers}
                  className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                  {t.name}
                </button>
              );})}
            </div>
          </PopoverButton>
        )}
        {scene.oCounter > 0 && (
          <FavoriteCounter count={scene.oCounter} />
        )}
        {scene.groups.length > 0 && (
          <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={scene.groups.length} title="Groups" preferBelow>
            <div className="flex flex-col gap-0.5">
              {scene.groups.map((g: any) => {
                const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "group", id: g.id }, onNavigate);

                return (
                <button key={g.id} type="button" {...navigationHandlers}
                  className="text-xs text-accent hover:underline cursor-pointer truncate text-left px-2 py-1 rounded hover:bg-card-hover transition-colors">{g.name}</button>
              );})}
            </div>
          </PopoverButton>
        )}
        {scene.galleries.length > 0 && (
          <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={scene.galleries.length} title="Galleries" preferBelow>
            <div className="flex flex-col gap-0.5">
              {scene.galleries.map((g: any) => {
                const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "gallery", id: g.id }, onNavigate);

                return (
                <button key={g.id} type="button" {...navigationHandlers}
                  className="text-xs text-accent hover:underline cursor-pointer truncate text-left px-2 py-1 rounded hover:bg-card-hover transition-colors">{g.title || "Untitled"}</button>
              );})}
            </div>
          </PopoverButton>
        )}
        {scene.markers.length > 0 && (
          <PopoverButton icon={<MapPin className="w-3.5 h-3.5" />} count={scene.markers.length} title="Markers" preferBelow>
            <div className="flex flex-col gap-0.5">
              {scene.markers.map((m: any) => {
                const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "scene", id: scene.id }, onNavigate);

                return (
                <button key={m.id} type="button" {...navigationHandlers}
                  className="text-xs text-accent hover:underline cursor-pointer truncate text-left px-2 py-1 rounded hover:bg-card-hover transition-colors">{m.title} ({formatDuration(m.seconds)})</button>
              );})}
            </div>
          </PopoverButton>
        )}
        {scene.organized && (
          <span className="p-1 text-muted" title="Organized"><Box className="w-3.5 h-3.5" /></span>
        )}
      </div>
    </>
  );
}

// ===== PerformerBadge (hover popover with performer image) =====

function PerformerBadge({
  performer,
  navigationHandlers,
}: {
  performer: { id: number; name: string; imagePath?: string | null };
  navigationHandlers: ReturnType<typeof createNestedCardNavigationHandlers<HTMLButtonElement>>;
}) {
  const badgeRef = useRef<HTMLButtonElement>(null);
  const [hover, setHover] = useState(false);
  const [style, setStyle] = useState<React.CSSProperties>({});
  const enterTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const leaveTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

  const onEnter = useCallback(() => {
    clearTimeout(leaveTimer.current);
    enterTimer.current = setTimeout(() => {
      if (badgeRef.current) {
        const rect = badgeRef.current.getBoundingClientRect();
        const s: React.CSSProperties = { position: "fixed", zIndex: 9999 };
        const spaceBelow = window.innerHeight - rect.bottom;
        if (spaceBelow > 180) { s.top = rect.bottom + 4; } else { s.bottom = window.innerHeight - rect.top + 4; }
        let left = rect.left + rect.width / 2 - 64;
        if (left < 8) left = 8;
        if (left + 128 > window.innerWidth - 8) left = window.innerWidth - 136;
        s.left = left;
        setStyle(s);
      }
      setHover(true);
    }, 300);
  }, []);

  const onLeave = useCallback(() => {
    clearTimeout(enterTimer.current);
    leaveTimer.current = setTimeout(() => setHover(false), 200);
  }, []);

  useEffect(() => () => { clearTimeout(enterTimer.current); clearTimeout(leaveTimer.current); }, []);

  return (
    <>
      <button ref={badgeRef} type="button" {...navigationHandlers} onMouseEnter={onEnter} onMouseLeave={onLeave}
        className="performer-badge flex items-center gap-1 rounded-full border border-border bg-surface px-1.5 py-0.5 min-w-0 hover:border-accent/50 transition-colors">
        {performer.imagePath ? (
          <img src={performer.imagePath} alt="" className="h-4 w-4 rounded-full object-cover flex-shrink-0" loading="lazy" />
        ) : (
          <User className="h-3.5 w-3.5 text-muted flex-shrink-0" />
        )}
        <span className="max-w-[80px] truncate text-[10px] text-secondary hover:text-accent">{performer.name}</span>
      </button>
      {hover && createPortal(
        <div style={style}
          className="bg-surface border border-border rounded-lg shadow-2xl shadow-black/40 p-2 w-[128px]"
          onClick={(e) => e.stopPropagation()}
          onMouseEnter={() => clearTimeout(leaveTimer.current)}
          onMouseLeave={onLeave}
        >
          <div className="w-full aspect-[2/3] rounded overflow-hidden bg-card mb-1.5">
            {performer.imagePath ? (
              <img src={performer.imagePath} alt="" className="w-full h-full object-cover" loading="lazy" />
            ) : (
              <div className="w-full h-full flex items-center justify-center"><User className="w-8 h-8 text-muted" /></div>
            )}
          </div>
          <p className="text-xs text-foreground font-medium text-center truncate">{performer.name}</p>
        </div>,
        document.body
      )}
    </>
  );
}

// ===== SceneCard (redesigned - cleaner, performer badges, 2-line title) =====

export function SceneCard({ scene, onClick, selected, onSelect, onNavigate, selecting, onQuickView }: { scene: Scene; onClick: () => void; selected?: boolean; onSelect?: () => void; selecting?: boolean; onNavigate?: (r: any) => void; onQuickView?: () => void }) {
  const file = scene.files[0];
  const duration = file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const screenshotUrl = scenes.screenshotUrl(scene.id, scene.updatedAt);
  const previewUrl = scenes.previewUrl(scene.id);
  const videoRef = useRef<HTMLVideoElement>(null);
  const progressPercent = duration > 0 && scene.resumeTime ? Math.min(100, (scene.resumeTime / duration) * 100) : 0;
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "scene", id: scene.id }, onClick);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.intersectionRatio > 0) video.play().catch(() => {});
        else video.pause();
      });
    });
    observer.observe(video);
    return () => observer.disconnect();
  }, []);

  return (
    <div {...navigationHandlers} className={`scene-card cursor-pointer group rounded border bg-card overflow-hidden flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border"}`}>
      <div className="scene-card-preview relative aspect-video bg-black overflow-hidden">
        <img src={screenshotUrl} alt={scene.title || ""} className="scene-card-preview-image w-full h-full object-cover" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        <video ref={videoRef} disableRemotePlayback playsInline muted loop preload="none" src={previewUrl} className="scene-card-preview-video" />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
        {scene.studioName && scene.studioId && !selecting && (
          <div className="absolute top-0 right-0 p-1 z-[5]">
            <img src={entityImages.studioImageUrl(scene.studioId)} alt={scene.studioName} className="max-h-8 max-w-[120px] object-contain drop-shadow-md"
              onError={(e) => { const el = e.target as HTMLImageElement; el.style.display = "none"; if (el.nextElementSibling) (el.nextElementSibling as HTMLElement).style.display = ""; }} />
            <span className="text-xs font-medium text-white bg-black/60 px-1.5 py-0.5 rounded" style={{ display: "none" }}>{scene.studioName}</span>
          </div>
        )}
        {(duration > 0 || resLabel) && (
          <div className="scene-specs-overlay absolute bottom-0 right-0 flex items-center gap-0.5 px-1.5 py-1 text-xs text-white z-[5] transition-opacity">
            {file && <span className="bg-black/70 px-1 py-0.5 rounded extra-scene-info hidden">{formatFileSize(file.size)}</span>}
            {resLabel && <span className="bg-black/70 px-1 py-0.5 rounded font-black uppercase">{resLabel}</span>}
            {duration > 0 && <span className="bg-black/70 px-1 py-0.5 rounded">{formatDuration(duration)}</span>}
          </div>
        )}
        {onQuickView && (
          <button
            onClick={(e) => { e.stopPropagation(); onQuickView(); }}
            className="absolute bottom-1 left-1 z-10 opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded bg-black/60 text-white hover:bg-black/80"
            title="Quick View"
          >
            <Eye className="w-3.5 h-3.5" />
          </button>
        )}
        {progressPercent > 0 && (
          <div className="absolute bottom-0 left-0 right-0 h-[3px] bg-black/40 z-[6]"><div className="h-full bg-accent" style={{ width: `${progressPercent}%` }} /></div>
        )}
        <RatingBanner rating={scene.rating} />
      </div>
      <div className="card-body px-2.5 pt-2 pb-2 border-t border-border/50 flex-1 flex flex-col gap-1.5 min-h-0">
        <div>
          <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent transition-colors leading-snug" title={scene.title || file?.basename || "Untitled"}>
            {scene.title || file?.basename || "Untitled"}
          </p>
          <div className="mt-1 flex items-center gap-2 text-[11px] text-muted">
            {scene.date && <span>{scene.date}</span>}
            {scene.studioName && <span className="truncate">{scene.studioName}</span>}
          </div>
        </div>
        {scene.performers.length > 0 && (
          <div className="flex items-center gap-1.5 overflow-hidden flex-wrap">
            {scene.performers.slice(0, 4).map((performer) => {
              const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "performer", id: performer.id }, onNavigate);

              return <PerformerBadge key={performer.id} performer={performer} navigationHandlers={navigationHandlers} />;
            })}
            {scene.performers.length > 4 && <span className="text-[10px] text-muted">+{scene.performers.length - 4}</span>}
          </div>
        )}
        {scene.details && <p className="text-xs text-secondary line-clamp-2 leading-snug">{scene.details}</p>}
      </div>
      <SceneCardPopovers scene={scene} onNavigate={onNavigate} />
    </div>
  );
}

// ===== SceneTile =====

interface SceneTileProps {
  scene: Scene;
  onClick: () => void;
}

export function SceneTile({ scene, onClick }: SceneTileProps) {
  const file = scene.files[0];
  const duration = file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "scene", id: scene.id }, onClick);

  return (
    <button type="button" {...navigationHandlers} className="group text-left">
      <div className="relative aspect-video overflow-hidden rounded-lg border border-border bg-card shadow-md shadow-black/30">
        <img src={scenes.screenshotUrl(scene.id, scene.updatedAt)} alt={scene.title || ""} className="h-full w-full object-cover" loading="lazy" />
        {duration > 0 && <span className="absolute bottom-1.5 right-1.5 rounded bg-black/75 px-1.5 py-0.5 text-[11px] text-white">{formatDuration(duration)}</span>}
        {resLabel && <span className="absolute top-1.5 right-1.5 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-bold uppercase text-accent">{resLabel}</span>}
        <RatingBanner rating={scene.rating} />
      </div>
      <div className="pt-2">
        <p className="card-title font-medium text-foreground line-clamp-2 group-hover:text-accent">{scene.title || "Untitled"}</p>
        <p className="mt-0.5 truncate text-xs text-secondary">{scene.date || scene.studioName || ""}</p>
      </div>
    </button>
  );
}

// ===== PerformerTile =====

interface PerformerTileProps {
  performer: Performer;
  onClick: () => void;
  onNavigate?: (r: any) => void;
  selected?: boolean;
  onSelect?: () => void;
  selecting?: boolean;
}

export function PerformerTile({ performer, onClick, onNavigate, selected, onSelect, selecting }: PerformerTileProps) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "performer", id: performer.id }, onClick);

  return (
    <div {...navigationHandlers} className={`entity-card group cursor-pointer overflow-hidden rounded-lg border bg-card text-left transition-colors flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      <div className="aspect-[2/3] overflow-hidden bg-gradient-to-b from-card to-surface relative">
        <img src={entityImages.performerImageUrl(performer.id)} alt={performer.name} className="h-full w-full object-cover" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        <RatingBanner rating={performer.rating} />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
        {performer.favorite && (
          <div className="absolute top-1.5 right-1.5 z-[5]">
            <Heart className="w-4 h-4 fill-red-500 text-red-500 drop-shadow-md" />
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2.5 flex-1 flex flex-col gap-1.5">
        <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{performer.name}</p>
        <div className="flex items-center gap-2 text-[11px] text-muted">
          {performer.country && <span>{performer.country}</span>}
          {performer.birthdate && <span>{performer.birthdate}</span>}
        </div>
        <p className="text-xs text-secondary">{performer.sceneCount} scene{performer.sceneCount !== 1 ? "s" : ""}</p>
      </div>
      {(performer.tags?.length > 0 || performer.sceneCount > 0 || performer.imageCount > 0) && (
        <>
          <hr className="border-border/50 my-0" />
          <div className="flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
            {performer.tags?.length > 0 && (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={performer.tags.length} title="Tags" preferBelow>
                <div className="flex flex-wrap gap-1">
                  {performer.tags.map((t: any) => {
                    const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "tag", id: t.id }, onNavigate);

                    return (
                    <button key={t.id} type="button" {...navigationHandlers}
                      className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                      {t.name}
                    </button>
                  );})}
                </div>
              </PopoverButton>
            )}
            {performer.sceneCount > 0 && (
              <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={performer.sceneCount} title="Scenes" wide preferBelow>
                <ScenesPopoverContent filter={{ performerIds: String(performer.id) }} />
              </PopoverButton>
            )}
            {performer.imageCount > 0 && (
              <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={performer.imageCount} title="Images" wide preferBelow>
                <ImagesPopoverContent filter={{ performerIds: String(performer.id) }} />
              </PopoverButton>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// ===== StudioTile =====

interface StudioTileProps {
  studio: Studio;
  onClick: () => void;
  onNavigate?: (r: any) => void;
  selected?: boolean;
  onSelect?: () => void;
  selecting?: boolean;
}

export function StudioTile({ studio, onClick, onNavigate, selected, onSelect, selecting }: StudioTileProps) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "studio", id: studio.id }, onClick);

  return (
    <div {...navigationHandlers} className={`entity-card group cursor-pointer overflow-hidden rounded-lg border bg-card text-left transition-colors flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-surface to-card relative">
        {studio.imagePath ? (
          <img src={studio.imagePath} alt={studio.name} className="h-full w-full object-contain p-4" loading="lazy" onError={(e) => { const el = e.target as HTMLImageElement; el.style.display = "none"; el.parentElement!.innerHTML = '<div class="flex items-center justify-center h-full w-full"><svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="text-muted"><path d="M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z"/><path d="M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2"/><path d="M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2"/><path d="M10 6h4"/><path d="M10 10h4"/><path d="M10 14h4"/><path d="M10 18h4"/></svg></div>'; }} />
        ) : (
          <div className="flex items-center justify-center h-full w-full">
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="text-muted"><path d="M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z"/><path d="M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2"/><path d="M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2"/><path d="M10 6h4"/><path d="M10 10h4"/><path d="M10 14h4"/><path d="M10 18h4"/></svg>
          </div>
        )}
        <RatingBanner rating={studio.rating} />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
        {studio.favorite && (
          <div className="absolute top-1.5 right-1.5 z-[5]">
            <Heart className="w-4 h-4 fill-red-500 text-red-500 drop-shadow-md" />
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2.5 flex-1 flex flex-col gap-1">
        <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{studio.name}</p>
        <p className="text-xs text-secondary">{studio.sceneCount} scene{studio.sceneCount !== 1 ? "s" : ""}</p>
      </div>
      {(studio.sceneCount > 0 || studio.performerCount > 0 || studio.imageCount > 0) && (
        <>
          <hr className="border-border/50 my-0" />
          <div className="flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
            {studio.sceneCount > 0 && (
              <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={studio.sceneCount} title="Scenes" wide preferBelow>
                <ScenesPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.performerCount > 0 && (
              <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={studio.performerCount} title="Performers" wide preferBelow>
                <PerformersPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.imageCount > 0 && (
              <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={studio.imageCount} title="Images" wide preferBelow>
                <ImagesPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// ===== ImageTile =====

interface ImageTileProps {
  image: Image;
  onClick: () => void;
  onNavigate?: (r: any) => void;
  onQuickView?: () => void;
  selected?: boolean;
  onSelect?: () => void;
  selecting?: boolean;
}

export function ImageTile({ image, onClick, onNavigate, onQuickView, selected, onSelect, selecting }: ImageTileProps) {
  const hasFooter = (image.tags?.length ?? 0) > 0 || (image.performers?.length ?? 0) > 0 || image.oCounter > 0 || image.organized;
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "image", id: image.id }, onClick);
  return (
    <div {...navigationHandlers} className={`entity-card group cursor-pointer overflow-hidden rounded-lg border bg-card text-left shadow-md shadow-black/20 flex flex-col h-full transition-colors ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      <div className="aspect-square overflow-hidden bg-surface relative">
        <img src={images.thumbnailUrl(image.id)} alt={image.title || ""} className="h-full w-full object-cover" loading="lazy" />
        <RatingBanner rating={image.rating} />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
        {image.studioName && (
          <div className="absolute top-1 right-1 text-[10px] bg-black/70 px-1 py-0.5 rounded text-white truncate max-w-[80%]">{image.studioName}</div>
        )}
        {onQuickView && (
          <button
            onClick={(e) => { e.stopPropagation(); onQuickView(); }}
            className="absolute bottom-1 left-1 z-10 opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded bg-black/60 text-white hover:bg-black/80"
            title="Quick View"
          >
            <Eye className="w-3.5 h-3.5" />
          </button>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2 flex-1 flex flex-col gap-1">
        <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{image.title || "Untitled"}</p>
      </div>
      {hasFooter && (
        <>
          <hr className="border-border/50 my-0" />
          <div className="flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
            {(image.tags?.length ?? 0) > 0 && (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={image.tags.length} title="Tags" preferBelow>
                <div className="flex flex-wrap gap-1">
                  {image.tags.map((t: any) => {
                    const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "tag", id: t.id }, onNavigate);

                    return (
                    <button key={t.id} type="button" {...navigationHandlers}
                      className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                      {t.name}
                    </button>
                  );})}
                </div>
              </PopoverButton>
            )}
            {(image.performers?.length ?? 0) > 0 && (
              <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={image.performers.length} title="Performers" wide preferBelow>
                <div className="grid grid-cols-2 gap-2">
                  {image.performers.map((p: any) => {
                    const navigationHandlers = createNestedEntityNavigationHandlers<HTMLButtonElement>({ page: "performer", id: p.id }, onNavigate);

                    return (
                    <button key={p.id} type="button" {...navigationHandlers}
                      className="flex flex-col items-center gap-1 text-center cursor-pointer rounded hover:bg-card-hover p-1.5 transition-colors">
                      <span className="text-xs text-accent hover:underline truncate w-full">{p.name}</span>
                    </button>
                  );})}
                </div>
              </PopoverButton>
            )}
            {image.oCounter > 0 && (
              <FavoriteCounter count={image.oCounter} />
            )}
            {image.organized && (
              <span className="p-1 text-muted" title="Organized"><Box className="w-3.5 h-3.5" /></span>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// ===== GalleryTile =====

interface GalleryTileProps {
  gallery: Gallery;
  onClick: () => void;
  selected?: boolean;
  onSelect?: () => void;
  selecting?: boolean;
}

export function GalleryTile({ gallery, onClick, selected, onSelect, selecting }: GalleryTileProps) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "gallery", id: gallery.id }, onClick);

  return (
    <div {...navigationHandlers} className={`entity-card group cursor-pointer overflow-hidden rounded-lg border bg-card text-left transition-colors flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-surface to-card relative overflow-hidden">
        {gallery.coverPath ? (
          <img src={gallery.coverPath} alt={gallery.title || ""} className="h-full w-full object-cover" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        ) : (
          <FolderOpen className="h-10 w-10 text-muted" />
        )}
        <RatingBanner rating={gallery.rating} />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2.5 flex-1 flex flex-col gap-1">
        <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{gallery.title || "Untitled"}</p>
        <p className="text-xs text-secondary">{gallery.imageCount} image{gallery.imageCount !== 1 ? "s" : ""}</p>
      </div>
    </div>
  );
}

// ===== GroupTile =====

interface GroupTileProps {
  group: Group;
  onClick: () => void;
  selected?: boolean;
  onSelect?: () => void;
  selecting?: boolean;
}

export function GroupTile({ group, onClick, selected, onSelect, selecting }: GroupTileProps) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "group", id: group.id }, onClick);

  return (
    <div {...navigationHandlers} className={`entity-card group cursor-pointer overflow-hidden rounded-lg border bg-card text-left transition-colors flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-surface to-card relative">
        <Layers className="h-10 w-10 text-muted" />
        <RatingBanner rating={group.rating} />
        {(selected !== undefined || selecting) && (
          <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
            <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2.5 flex-1 flex flex-col gap-1">
        <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{group.name}</p>
        <p className="text-xs text-secondary">{group.sceneCount} scene{group.sceneCount !== 1 ? "s" : ""}</p>
      </div>
    </div>
  );
}
