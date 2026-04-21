import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { scenes, tags, entityImages, performers as performersApi, studios as studiosApi, galleries as galleriesApi, groups as groupsApi, metadata } from "../api/client";
import { formatDuration, formatFileSize, formatDate, TagBadge, getResolutionLabel, CustomFieldsDisplay } from "../components/shared";
import { 
  Pencil, Plus, Trash2, Search, Eye, Heart, 
  Check, ChevronLeft, ChevronRight, MoreVertical, PanelLeftClose, PanelLeft,
  Play, Pause, Volume2, VolumeX, Maximize, Minimize,
  SkipBack, SkipForward, Gauge, Clapperboard, Monitor, FolderOpen, Layers,
  RefreshCw, Camera, Image, Merge, Upload, ExternalLink,
  PictureInPicture2, Repeat, Repeat1, Subtitles
} from "lucide-react";
import { useState, useRef, useEffect, useCallback, Fragment, useMemo } from "react";
import { SceneEditModal } from "./SceneEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { GenerateDialog } from "../components/GenerateDialog";
import { DetailMergeDialog } from "../components/DetailMergeDialog";
import { IdentifyDialog } from "../components/IdentifyDialog";
import type { Scene, SceneMarkerCreate, SceneUpdate } from "../api/types";
import { ExtensionSlot } from "../router/RouteRegistry";
import { InteractiveRating } from "../components/Rating";
import { useSceneQueue } from "../state/SceneQueueContext";
import { useAppConfig } from "../state/AppConfigContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import { createCardNavigationHandlers } from "../components/cardNavigation";
import { StringListEditor } from "../components/StringListEditor";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "details" | "groups" | "galleries" | "markers" | "filters" | "file-info" | "edit" | "history" | string;

export function SceneDetailPage({ id, onNavigate }: Props) {
  const { data: scene, isLoading } = useQuery({
    queryKey: ["scene", id],
    queryFn: () => scenes.get(id),
  });
  const { config } = useAppConfig();
  const { hasPrev, hasNext, prevId, nextId, currentPosition, queueLength } = useSceneQueue();
  const { getTabsForPage, resolveComponent: resolveExtComponent } = useExtensions();
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showGenerate, setShowGenerate] = useState(false);
  const [theaterMode, setTheaterMode] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [activeTab, setActiveTab] = useState<TabKey>("details");
  const queryClient = useQueryClient();
  const seekRef = useRef<((time: number) => void) | null>(null);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const [videoTime, setVideoTime] = useState(0);
  const [videoFilters, setVideoFilters] = useState({ brightness: 100, contrast: 100, gamma: 100, saturation: 100, hue: 0 });

  useEffect(() => {
    if (scene) document.title = `${scene.title || scene.files?.[0]?.basename || `Scene ${id}`} | Cove`;
    return () => { document.title = "Cove"; };
  }, [scene, id]);

  // Disable background animations on video player pages for GPU performance
  // Controlled by gradient > "Pause on Scene Player" setting (default: on)
  useEffect(() => {
    try {
      const opts = JSON.parse(localStorage.getItem("cove-style-options") ?? "{}");
      if (opts.gradient?.scenepause === "off") return;
    } catch { /* default to pausing */ }
    document.body.classList.add("has-video-player");
    return () => document.body.classList.remove("has-video-player");
  }, []);

  // Theater mode: hide navbar and expand layout
  useEffect(() => {
    if (theaterMode) {
      document.documentElement.classList.add("theater-mode");
    } else {
      document.documentElement.classList.remove("theater-mode");
    }
    return () => document.documentElement.classList.remove("theater-mode");
  }, [theaterMode]);

  // Keyboard shortcuts: "," for theater mode, a/e/k/i/h for tab navigation, o to increment favorites
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
      switch (e.key) {
        case ",": setTheaterMode((prev) => !prev); break;
        case "a": setActiveTab("details"); break;
        case "e": setActiveTab("edit"); break;
        case "k": setActiveTab("markers"); break;
        case "i": setActiveTab("file-info"); break;
        case "h": setActiveTab("history"); break;
        case "o": if (scene) incrementOMut.mutate(); break;
        case "[": if (hasPrev && prevId != null) onNavigate({ page: "scene", id: prevId }); break;
        case "]": if (hasNext && nextId != null) onNavigate({ page: "scene", id: nextId }); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  // Close ops menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) {
        setShowOpsMenu(false);
      }
    };
    if (showOpsMenu) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  // Apply CSS filters to video element when videoFilters change
  useEffect(() => {
    const video = document.querySelector('video');
    if (video) {
      const { brightness, contrast, saturation, hue } = videoFilters;
      video.style.filter = `brightness(${brightness}%) contrast(${contrast}%) saturate(${saturation}%) hue-rotate(${hue}deg)`;
    }
    return () => {
      const video = document.querySelector('video');
      if (video) video.style.filter = '';
    };
  }, [videoFilters]);

  const deleteMut = useMutation({
    mutationFn: (deleteFile?: boolean) => scenes.delete(id, deleteFile),
    onSuccess: () => { 
      queryClient.invalidateQueries({ queryKey: ["scenes"] }); 
      onNavigate({ page: "scenes" }); 
    },
  });

  const incrementPlayMut = useMutation({
    mutationFn: () => scenes.recordPlay(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["scene", id] }),
  });

  const incrementOMut = useMutation({
    mutationFn: () => scenes.incrementO(id),
    onSuccess: (newCount: number) => {
      queryClient.setQueryData<Scene>(["scene", id], (old) =>
        old ? { ...old, oCounter: newCount } : old
      );
    },
  });

  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean; rating?: number }) => scenes.update(id, data),
    onSuccess: (updatedScene) => {
      queryClient.setQueryData<Scene>(["scene", id], updatedScene);
    },
  });

  const generateScreenshotMut = useMutation({
    mutationFn: (atSeconds?: number) => scenes.generateScreenshot(id, atSeconds),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["scene", id] }),
  });

  const rescanMut = useMutation({
    mutationFn: () => scenes.rescan(id),
  });

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }

  if (!scene) return <div className="text-center text-secondary py-16">Scene not found</div>;

  const file = scene.files[0];
  const streamUrl = scenes.streamUrl(id);
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;

  const tabs: { key: TabKey; label: string }[] = [
    { key: "details", label: "Details" },
    { key: "markers", label: "Markers" },
    ...(scene.groups.length > 0 ? [{ key: "groups" as TabKey, label: "Groups" }] : []),
    ...(scene.galleries.length > 0 ? [{ key: "galleries" as TabKey, label: "Galleries" }] : []),
    { key: "filters", label: "Filters" },
    { key: "file-info", label: `File Info${scene.files.length > 1 ? ` (${scene.files.length})` : ""}` },
    { key: "history", label: "History" },
    // Extension-contributed tabs
    ...getTabsForPage("scene").map((t) => ({ key: `ext:${t.key}` as TabKey, label: t.label })),
    { key: "edit", label: "Edit" },
  ];

  const studioImageUrl = scene.studioId ? entityImages.studioImageUrl(scene.studioId) : null;

  return (
    <div className="-mx-6 -mt-5 -mb-5">
      {scene && <SceneEditModal scene={scene} open={editing} onClose={() => setEditing(false)} />}
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Scene"
        message={`Are you sure you want to delete "${scene.title || "Untitled"}"? This cannot be undone.`}
        onConfirm={(opts) => deleteMut.mutate(opts?.deleteFile)}
        onCancel={() => setConfirmDelete(false)}
        showDeleteFile
      />
      <GenerateDialog
        open={showGenerate}
        onClose={() => setShowGenerate(false)}
        sceneIds={[id]}
        title={`Generate for "${scene.title || "Untitled"}"`}
      />
      <DetailMergeDialog
        open={showMerge}
        onClose={() => setShowMerge(false)}
        entityType="scene"
        targetItem={{ id: scene.id, name: scene.title || file?.basename || `Scene ${scene.id}`, imagePath: scenes.screenshotUrl(scene.id, scene.updatedAt), subtitle: scene.studioName }}
        searchItems={async (term) => {
          const response = await scenes.find({ page: 1, perPage: 20, direction: "desc", q: term || undefined });
          return response.items.map((item) => ({
            id: item.id,
            name: item.title || item.files[0]?.basename || `Scene ${item.id}`,
            imagePath: scenes.screenshotUrl(item.id, item.updatedAt),
            subtitle: item.studioName,
          }));
        }}
        onMerge={(targetId, sourceIds) => scenes.merge(targetId, sourceIds)}
        invalidateQueryKeys={[["scene", id], ["scenes"]]}
      />
      <IdentifyDialog
        open={showIdentify}
        onClose={() => setShowIdentify(false)}
        sceneIds={[id]}
      />

      {/* Standard layout: left sidebar + right video */}
      <div className={theaterMode ? "flex flex-col" : "flex flex-col xl:flex-row xl:h-[calc(100vh-48px)]"}>
        {/* Left sidebar: metadata, tabs, tab content */}
        {!theaterMode && !sidebarCollapsed && (
          <div
            className="w-full xl:w-[400px] 2xl:w-[450px] xl:min-w-[350px] xl:max-w-[500px] xl:border-r border-b xl:border-b-0 border-border overflow-y-auto shrink-0 xl:max-h-[calc(100vh-48px)]"
          >
            <div className="px-6 pt-4 pb-2">
              {/* Studio logo */}
              {studioImageUrl && scene.studioId && (
                <div className="mb-3 flex items-start gap-4">
                  <button
                    onClick={() => onNavigate({ page: "studio", id: scene.studioId })}
                    className="flex-shrink-0"
                  >
                    <img
                      src={studioImageUrl}
                      alt={scene.studioName || "Studio"}
                      className="max-h-[5rem] max-w-full object-contain"
                      onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }}
                    />
                  </button>
                </div>
              )}

              {/* Queue navigation removed - will be replaced later */}

              {/* Title — large like original's h3 */}
              <h3 className="text-[1.5rem] font-semibold text-foreground leading-snug line-clamp-2 mt-1">
                {scene.title || file?.path.split(/[\\/]/).pop() || "Untitled"}
              </h3>

              {/* Subheader: date left, resolution+fps right */}
              <div className="flex items-center justify-between mt-2 text-sm text-secondary">
                <span>{scene.date ? new Date(scene.date + "T00:00:00").toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" }) : ""}</span>
                <span className="flex items-center gap-1.5">
                  {file && file.frameRate > 0 && <span>{file.frameRate.toFixed(0)} fps</span>}
                  {file && resLabel && <span className="text-accent font-bold">{resLabel}</span>}
                </span>
              </div>

              {/* Studio name text fallback (when no logo) */}
              {scene.studioName && scene.studioId && !studioImageUrl && (
                <button 
                  onClick={() => onNavigate({ page: "studio", id: scene.studioId })}
                  className="text-accent hover:underline text-sm mt-1 block"
                >
                  {scene.studioName}
                </button>
              )}

              {/* Toolbar: rating left, counters + ops right — single row */}
              <div className="flex items-center justify-between mt-3 gap-2">
                <InteractiveRating value={scene.rating} onChange={(value) => updateMut.mutate({ rating: value })} />
                <div className="flex items-center gap-2">
                  <button 
                    onClick={() => incrementPlayMut.mutate()}
                    className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"
                    title="Play count"
                  >
                    <Eye className="w-4 h-4" />
                    <span>{scene.playCount}</span>
                  </button>
                  <button 
                    onClick={() => incrementOMut.mutate()}
                    className="flex items-center gap-1 text-sm text-secondary hover:text-accent"
                    title="Favorite"
                  >
                    <Heart className={`w-4 h-4 ${scene.oCounter > 0 ? "fill-accent text-accent" : ""}`} />
                    <span>{scene.oCounter}</span>
                  </button>
                  <button 
                    onClick={() => { if (!updateMut.isPending) updateMut.mutate({ organized: !scene.organized }); }}
                    disabled={updateMut.isPending}
                    className={`p-1 rounded ${scene.organized ? "bg-green-600 text-white" : "bg-card text-muted hover:text-foreground"} ${updateMut.isPending ? "opacity-60 cursor-not-allowed" : ""}`}
                    title={scene.organized ? "Organized" : "Not organized"}
                  >
                    <Check className="w-4 h-4" />
                  </button>
                  {file && (
                    <a
                      href={streamUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="p-1 rounded text-secondary hover:text-foreground hover:bg-card"
                      title="Open in external player"
                    >
                      <ExternalLink className="w-4 h-4" />
                    </a>
                  )}
                  {/* Operations dropdown */}
                  <div className="relative" ref={opsMenuRef}>
                    <button
                      onClick={() => setShowOpsMenu(!showOpsMenu)}
                      className="p-1 rounded text-secondary hover:text-foreground hover:bg-card"
                      title="Operations"
                    >
                      <MoreVertical className="w-4 h-4" />
                    </button>
                    {showOpsMenu && (
                      <div className="absolute right-0 top-full mt-1 z-50 min-w-[220px] bg-card border border-border rounded shadow-lg py-1">
                        <button onClick={() => { setEditing(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Pencil className="w-3.5 h-3.5" /> Edit</button>
                        {file && (
                          <button onClick={() => { rescanMut.mutate(); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><RefreshCw className="w-3.5 h-3.5" /> Rescan</button>
                        )}
                        <button onClick={() => { setShowIdentify(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Search className="w-3.5 h-3.5" /> Identify…</button>
                        <div className="border-t border-border my-1" />
                        <button onClick={() => { setShowGenerate(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Clapperboard className="w-3.5 h-3.5" /> Generate…</button>
                        <button onClick={() => { generateScreenshotMut.mutate(videoTime); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Camera className="w-3.5 h-3.5" /> Screenshot from Current</button>
                        <button onClick={() => { generateScreenshotMut.mutate(undefined); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Image className="w-3.5 h-3.5" /> Screenshot Default</button>
                        <div className="border-t border-border my-1" />
                        <button onClick={() => { setShowMerge(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Merge className="w-3.5 h-3.5" /> Merge…</button>
                        <button onClick={() => { setTheaterMode(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Monitor className="w-3.5 h-3.5" /> Theater Mode</button>
                        <div className="border-t border-border my-1" />
                        <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-red-400 hover:bg-surface flex items-center gap-2"><Trash2 className="w-3.5 h-3.5" /> Delete</button>
                      </div>
                    )}
                  </div>
                  <ExtensionSlot slot="scene-detail-actions" context={{ scene, onNavigate }} />
                </div>
              </div>
            </div>

            {/* Tab Navigation */}
            <div className="px-6">
              <div className="flex flex-wrap border-b border-border">
                {tabs.map((tab) => (
                  <button
                    key={tab.key}
                    onClick={() => setActiveTab(tab.key)}
                    className={`px-2.5 py-2 text-sm transition-colors border-b-2 cursor-pointer ${
                      activeTab === tab.key 
                        ? "border-accent text-accent" 
                        : "border-transparent text-secondary hover:text-foreground"
                    }`}
                  >
                    {tab.label}
                  </button>
                ))}
              </div>
            </div>

            {/* Tab Content */}
            <div className="px-6 py-4">
              {activeTab === "details" && (
                <DetailsTab scene={scene} onNavigate={onNavigate} />
              )}
              {activeTab === "groups" && (
                <GroupsTab scene={scene} onNavigate={onNavigate} />
              )}
              {activeTab === "galleries" && (
                <GalleriesTab scene={scene} onNavigate={onNavigate} />
              )}
              {activeTab === "markers" && (
                <MarkersPanel sceneId={scene.id} markers={scene.markers} onSeek={(t) => seekRef.current?.(t)} />
              )}
              {activeTab === "filters" && (
                <VideoFiltersTab filters={videoFilters} onChange={setVideoFilters} />
              )}
              {activeTab === "file-info" && scene.files.length > 0 && (
                <FileInfoTab files={scene.files} />
              )}
              {activeTab === "history" && (
                <HistoryTab scene={scene} />
              )}
              {activeTab === "edit" && (
                <SceneEditPanel scene={scene} onSaved={() => setActiveTab("details")} />
              )}
              {/* Extension-contributed tab content */}
              {activeTab.startsWith("ext:") && (() => {
                const extTabKey = activeTab.replace("ext:", "");
                const extTab = getTabsForPage("scene").find((t) => t.key === extTabKey);
                if (!extTab) return null;
                const Component = resolveExtComponent(extTab.componentName);
                if (!Component) return <div className="p-4 text-muted">Extension component not found: {extTab.componentName}</div>;
                return <Component entityId={id} />;
              })()}
            </div>
          </div>
        )}

        {/* Sidebar collapse/expand divider */}
        {!theaterMode && (
          <button
            onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
            className="hidden xl:flex items-center justify-center bg-surface/50 hover:bg-surface border-r border-border transition-colors w-[15px] shrink-0"
            title={sidebarCollapsed ? "Show sidebar" : "Hide sidebar"}
          >
            {sidebarCollapsed ? <ChevronRight className="w-4 h-4 text-muted" /> : <ChevronLeft className="w-4 h-4 text-muted" />}
          </button>
        )}

        {/* Right side: video player + scrubber */}
        <div className="min-w-0 flex flex-col flex-1 min-h-0 overflow-hidden">
          <div className="bg-black flex-1 flex flex-col min-h-0 max-h-[70vh] xl:max-h-none">
            {file ? (
              <VideoPlayer
                streamUrl={streamUrl}
                posterUrl={scenes.screenshotUrl(id, scene.updatedAt)}
                format={file.format}
                duration={file.duration}
                resumeTime={scene.resumeTime}
                sceneId={id}
                captions={file.captions}
                onPlay={() => incrementPlayMut.mutate()}
                markers={scene.markers}
                onSeekRegister={(fn) => { seekRef.current = fn; }}
                onTimeUpdate={setVideoTime}
                autostart={config?.ui.autostartVideo}
                showAbLoop={config?.ui.showAbLoopControls}
                onEnded={() => { if (hasNext && nextId != null) onNavigate({ page: "scene", id: nextId }); }}
                onPrev={hasPrev && prevId != null ? () => onNavigate({ page: "scene", id: prevId }) : undefined}
                onNext={hasNext && nextId != null ? () => onNavigate({ page: "scene", id: nextId }) : undefined}
              />
            ) : (
              <div className="flex items-center justify-center h-48 text-muted">No video file available</div>
            )}
          </div>
          {/* Scene scrubber */}
          {file && <SceneScrubber sceneId={scene.id} duration={file.duration} markers={scene.markers} onSeek={(t) => seekRef.current?.(t)} currentTime={videoTime} />}

          {/* Theater mode: show metadata below video */}
          {theaterMode && (
            <div className="px-4 pt-3 max-w-5xl mx-auto">
              <h1 className="text-xl font-bold text-foreground">{scene.title || file?.path.split(/[\\/]/).pop() || "Untitled"}</h1>
              <div className="flex items-center gap-3 mt-2 flex-wrap">
                <InteractiveRating value={scene.rating} onChange={(value) => updateMut.mutate({ rating: value })} />
                <button onClick={() => incrementPlayMut.mutate()} className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"><Eye className="w-4 h-4" />{scene.playCount}</button>
                <button onClick={() => incrementOMut.mutate()} className="flex items-center gap-1 text-sm text-secondary hover:text-accent"><Heart className={`w-4 h-4 ${scene.oCounter > 0 ? "fill-accent text-accent" : ""}`} />{scene.oCounter}</button>
                <button onClick={() => setTheaterMode(false)} className="flex items-center gap-1 px-2 py-1 text-xs bg-accent text-white rounded"><Monitor className="w-3 h-3" /> Exit Theater</button>
              </div>
            </div>
          )}
        </div>
      </div>

      <ExtensionSlot slot="scene-detail-main-bottom" context={{ scene, onNavigate }} />
    </div>
  );
}

// Details Tab Content
export function DetailsTab({ scene, onNavigate }: { scene: Scene; onNavigate: (r: any) => void }) {
  return (
    <div className="space-y-4">
      {/* Created/Updated + Code/Director at top like original */}
      <dl className="grid gap-y-1.5 text-sm" style={{ gridTemplateColumns: "auto 1fr" }}>
        <dt className="text-muted pr-3">Created</dt>
        <dd className="text-foreground">{formatDate(scene.createdAt)}</dd>
        <dt className="text-muted pr-3">Updated</dt>
        <dd className="text-foreground">{formatDate(scene.updatedAt)}</dd>
        {scene.code && (
          <>
            <dt className="text-muted pr-3">Studio Code</dt>
            <dd className="text-foreground">{scene.code}</dd>
          </>
        )}
        {scene.director && (
          <>
            <dt className="text-muted pr-3">Director</dt>
            <dd>
              <button onClick={() => onNavigate({ page: "scenes", query: scene.director })} className="text-accent hover:underline">
                {scene.director}
              </button>
            </dd>
          </>
        )}
      </dl>

      {/* Details / Description */}
      {scene.details && (
        <div>
          <p className="text-sm text-foreground whitespace-pre-wrap">{scene.details}</p>
        </div>
      )}

      {/* Tags */}
      {scene.tags.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">Tags</h6>
          <div className="flex flex-wrap gap-1.5">
            {scene.tags.map((tag: any) => (
              <TagBadge 
                key={tag.id} 
                name={tag.name} 
                onClick={() => onNavigate({ page: "tag", id: tag.id })} 
              />
            ))}
          </div>
        </div>
      )}

      {/* Performers */}
      {scene.performers.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">Performer{scene.performers.length > 1 ? "s" : ""}</h6>
          <div className={scene.performers.length > 1 ? "grid grid-cols-2 gap-3" : "flex flex-wrap gap-3"}>
            {scene.performers.map((performer: any) => (
              <PerformerCard 
                key={performer.id} 
                performer={performer}
                sceneDate={scene.date}
                fullWidth={scene.performers.length > 1}
                onClick={() => onNavigate({ page: "performer", id: performer.id })}
              />
            ))}
          </div>
        </div>
      )}

      {/* URLs */}
      {scene.urls && scene.urls.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">URLs</h6>
          <div className="space-y-1">
            {scene.urls.map((url: string, i: number) => (
              <a
                key={i}
                href={url}
                target="_blank"
                rel="noopener noreferrer"
                className="text-accent hover:underline text-sm block truncate"
              >
                {url}
              </a>
            ))}
          </div>
        </div>
      )}

      {/* Remote IDs */}
      {scene.remoteIds && scene.remoteIds.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">Remote IDs</h6>
          <dl className="grid gap-y-1 text-sm" style={{ gridTemplateColumns: "auto 1fr" }}>
            {scene.remoteIds.map((sid, i) => (
              <Fragment key={i}>
                <dt className="text-muted pr-3 truncate">{sid.endpoint}</dt>
                <dd className="text-foreground font-mono text-xs break-all">{sid.remoteId}</dd>
              </Fragment>
            ))}
          </dl>
        </div>
      )}
      <CustomFieldsDisplay customFields={scene.customFields} />
    </div>
  );
}

function GroupsTab({ scene, onNavigate }: { scene: Scene; onNavigate: (r: any) => void }) {
  if (scene.groups.length === 0) {
    return (
      <div className="rounded-xl border border-dashed border-border bg-card/40 px-4 py-10 text-center text-sm text-secondary">
        <Layers className="mx-auto mb-3 h-8 w-8 text-muted" />
        No groups linked to this scene.
      </div>
    );
  }

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {scene.groups.map((group) => {
        const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "group", id: group.id }, () => onNavigate({ page: "group", id: group.id }));

        return (
          <button
            key={group.id}
            type="button"
            {...navigationHandlers}
            className="rounded-xl border border-border bg-card p-4 text-left transition-colors hover:border-accent/60"
          >
            <div className="flex items-center justify-between gap-3">
              <div>
                <div className="text-sm font-medium text-foreground">{group.name}</div>
                <div className="mt-1 text-xs text-secondary">Scene #{group.sceneIndex}</div>
              </div>
              <Layers className="h-5 w-5 text-muted" />
            </div>
          </button>
        );
      })}
    </div>
  );
}

function GalleriesTab({ scene, onNavigate }: { scene: Scene; onNavigate: (r: any) => void }) {
  if (scene.galleries.length === 0) {
    return (
      <div className="rounded-xl border border-dashed border-border bg-card/40 px-4 py-10 text-center text-sm text-secondary">
        <FolderOpen className="mx-auto mb-3 h-8 w-8 text-muted" />
        No galleries linked to this scene.
      </div>
    );
  }

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {scene.galleries.map((gallery) => {
        const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "gallery", id: gallery.id }, () => onNavigate({ page: "gallery", id: gallery.id }));

        return (
          <button
            key={gallery.id}
            type="button"
            {...navigationHandlers}
            className="group overflow-hidden rounded-xl border border-border bg-card text-left transition-colors hover:border-accent/60"
          >
            <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-surface to-card">
              <FolderOpen className="h-10 w-10 text-muted" />
            </div>
            <div className="p-3">
              <p className="truncate text-sm font-medium text-foreground group-hover:text-accent">
                {gallery.title || "Untitled"}
              </p>
              {gallery.date && (
                <p className="mt-1 text-xs text-secondary">{formatDate(gallery.date)}</p>
              )}
            </div>
          </button>
        );
      })}
    </div>
  );
}

function PerformerCard({ performer, sceneDate, fullWidth = false, onClick }: { performer: any; sceneDate?: string; fullWidth?: boolean; onClick: () => void }) {
  const imageUrl = performer.imagePath;
  const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "performer", id: performer.id }, onClick);
  // Calculate age at scene date
  const ageAtScene = (() => {
    if (!sceneDate || !performer.birthdate) return null;
    const scene = new Date(sceneDate);
    const birth = new Date(performer.birthdate);
    let age = scene.getFullYear() - birth.getFullYear();
    const m = scene.getMonth() - birth.getMonth();
    if (m < 0 || (m === 0 && scene.getDate() < birth.getDate())) age--;
    return age > 0 ? age : null;
  })();

  return (
    <button
      type="button"
      {...navigationHandlers}
      className={`bg-card border border-border rounded overflow-hidden hover:border-accent/60 transition-colors text-left ${fullWidth ? "w-full" : ""}`}
      style={fullWidth ? undefined : { width: "200px" }}
    >
      <div className="aspect-[2/3] bg-surface flex items-center justify-center relative">
        {imageUrl ? (
          <img src={imageUrl} alt={performer.name} className="w-full h-full object-cover" />
        ) : (
          <div className="w-full h-full flex items-center justify-center bg-gradient-to-b from-card to-surface">
            <svg viewBox="0 0 100 150" className="w-2/3 h-2/3 opacity-30">
              <ellipse cx="50" cy="35" rx="25" ry="30" fill="currentColor" className="text-muted"/>
              <ellipse cx="50" cy="120" rx="40" ry="45" fill="currentColor" className="text-muted"/>
            </svg>
          </div>
        )}
      </div>
      <div className="p-2 text-center">
        <div className="text-sm text-foreground font-medium truncate">{performer.name}</div>
        <div className="text-xs text-muted flex items-center justify-center gap-1 mt-0.5">
          {ageAtScene && <span>{ageAtScene} yrs old</span>}
          {ageAtScene && performer.sceneCount !== undefined && <span>·</span>}
          {performer.sceneCount !== undefined && (
            <span className="flex items-center gap-0.5"><Eye className="w-3 h-3" /> {performer.sceneCount}</span>
          )}
        </div>
      </div>
    </button>
  );
}

// File Info Tab — show every underlying scene file rather than only the first one.
export function FileInfoTab({ files }: { files: Scene["files"] }) {
  return (
    <div className="space-y-4 text-sm">
      {files.map((file, index) => {
        const sectionLabel = file.basename || file.path.split(/[\\/]/).pop() || `File ${index + 1}`;

        return (
          <section key={file.id ?? `${file.path}-${index}`} className="rounded-xl border border-border bg-card p-4 space-y-3">
            {files.length > 1 && (
              <div>
                <h6 className="text-sm font-semibold text-foreground">{sectionLabel}</h6>
                <p className="text-xs text-muted">File {index + 1} of {files.length}</p>
              </div>
            )}

            <dl className="grid gap-y-1.5" style={{ gridTemplateColumns: "minmax(100px, auto) 1fr" }}>
              <dt className="text-muted">Path</dt>
              <dd className="text-foreground break-all font-mono text-xs">{file.path}</dd>

              <dt className="text-muted">File Size</dt>
              <dd className="text-foreground">{formatFileSize(file.size)}</dd>

              <dt className="text-muted">Duration</dt>
              <dd className="text-foreground">{formatDuration(file.duration)}</dd>

              <dt className="text-muted">Dimensions</dt>
              <dd className="text-foreground">{file.width}×{file.height}</dd>

              <dt className="text-muted">Frame Rate</dt>
              <dd className="text-foreground">{file.frameRate.toFixed(2)} fps</dd>

              <dt className="text-muted">Bitrate</dt>
              <dd className="text-foreground">{Math.round(file.bitRate / 1000)} kbps</dd>

              <dt className="text-muted">Video Codec</dt>
              <dd className="text-foreground">{file.videoCodec}</dd>

              <dt className="text-muted">Audio Codec</dt>
              <dd className="text-foreground">{file.audioCodec}</dd>
            </dl>

            {file.fingerprints && file.fingerprints.length > 0 && (
              <div>
                <h6 className="text-sm text-muted mb-1 font-medium">Fingerprints</h6>
                <dl className="grid gap-y-1" style={{ gridTemplateColumns: "auto 1fr" }}>
                  {file.fingerprints.map((fp: any) => (
                    <Fragment key={`${file.id ?? index}-${fp.type}`}>
                      <dt className="text-muted text-xs pr-3">{fp.type}</dt>
                      <dd className="text-foreground font-mono text-xs break-all">{fp.value}</dd>
                    </Fragment>
                  ))}
                </dl>
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}

// History Tab
function HistoryTab({ scene }: { scene: Scene }) {
  const queryClient = useQueryClient();
  const { data: history } = useQuery({
    queryKey: ["scene-history", scene.id],
    queryFn: () => scenes.getHistory(scene.id),
  });

  const resetPlayMut = useMutation({
    mutationFn: () => scenes.resetPlay(scene.id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["scene", scene.id] }); queryClient.invalidateQueries({ queryKey: ["scene-history", scene.id] }); },
  });
  const deletePlayMut = useMutation({
    mutationFn: () => scenes.deletePlay(scene.id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["scene", scene.id] }); queryClient.invalidateQueries({ queryKey: ["scene-history", scene.id] }); },
  });
  const resetOMut = useMutation({
    mutationFn: () => scenes.resetO(scene.id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["scene", scene.id] }); queryClient.invalidateQueries({ queryKey: ["scene-history", scene.id] }); },
  });
  const decrementOMut = useMutation({
    mutationFn: () => scenes.decrementO(scene.id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["scene", scene.id] }); queryClient.invalidateQueries({ queryKey: ["scene-history", scene.id] }); },
  });

  const btnCls = "rounded border border-border bg-card px-2 py-0.5 text-xs text-secondary hover:text-foreground hover:bg-card-hover";

  return (
    <div className="space-y-4 text-sm">
      {/* Play History */}
      <div className="rounded-xl border border-border bg-card p-4">
        <div className="flex items-center justify-between mb-2">
          <h3 className="text-sm font-semibold text-muted uppercase tracking-wide">Play History</h3>
          <div className="flex gap-1">
            <button onClick={() => deletePlayMut.mutate()} className={btnCls} title="Remove last play">-1</button>
            <button onClick={() => resetPlayMut.mutate()} className={btnCls} title="Reset play count">Reset</button>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2 mb-2">
          <div><span className="text-muted">Play Count:</span> <span className="text-foreground">{scene.playCount}</span></div>
          <div><span className="text-muted">Duration:</span> <span className="text-foreground">{formatDuration(scene.playDuration)}</span></div>
        </div>
        {history?.playHistory && history.playHistory.length > 0 && (
          <div className="max-h-40 overflow-y-auto space-y-0.5 border-t border-border pt-2">
            {history.playHistory.map((date, i) => (
              <div key={i} className="text-xs text-secondary">{new Date(date).toLocaleString()}</div>
            ))}
          </div>
        )}
      </div>

      {/* Favorites History */}
      <div className="rounded-xl border border-border bg-card p-4">
        <div className="flex items-center justify-between mb-2">
          <h3 className="text-sm font-semibold text-muted uppercase tracking-wide">Favorites</h3>
          <div className="flex gap-1">
            <button onClick={() => decrementOMut.mutate()} className={btnCls} title="Remove favorite">-1</button>
            <button onClick={() => resetOMut.mutate()} className={btnCls} title="Reset favorites">Reset</button>
          </div>
        </div>
        <div className="mb-2">
          <span className="text-muted">Count:</span> <span className="text-foreground">{scene.oCounter}</span>
        </div>
        {history?.oHistory && history.oHistory.length > 0 && (
          <div className="max-h-40 overflow-y-auto space-y-0.5 border-t border-border pt-2">
            {history.oHistory.map((date, i) => (
              <div key={i} className="text-xs text-secondary">{new Date(date).toLocaleString()}</div>
            ))}
          </div>
        )}
      </div>

      {/* Timestamps */}
      <div className="grid grid-cols-2 gap-2">
        <div><span className="text-muted">Created:</span> <span className="text-foreground">{formatDate(scene.createdAt)}</span></div>
        <div><span className="text-muted">Updated:</span> <span className="text-foreground">{formatDate(scene.updatedAt)}</span></div>
      </div>
    </div>
  );
}

// Video Filters Tab — matches standard's brightness/contrast/gamma/saturation/hue
interface VideoFilters {
  brightness: number;
  contrast: number;
  gamma: number;
  saturation: number;
  hue: number;
}

function VideoFiltersTab({ filters, onChange }: { filters: VideoFilters; onChange: (f: VideoFilters) => void }) {
  const sliders: { key: keyof VideoFilters; label: string; min: number; max: number; default: number; unit: string; formatValue?: (v: number) => string }[] = [
    { key: "brightness", label: "Brightness", min: 0, max: 200, default: 100, unit: "%" },
    { key: "contrast", label: "Contrast", min: 0, max: 200, default: 100, unit: "%" },
    { key: "gamma", label: "Gamma", min: 0, max: 200, default: 100, unit: "", formatValue: (v) => String(v - 100) },
    { key: "saturation", label: "Saturation", min: 0, max: 200, default: 100, unit: "%" },
    { key: "hue", label: "Hue", min: -180, max: 180, default: 0, unit: "°" },
  ];

  const handleReset = () => onChange({ brightness: 100, contrast: 100, gamma: 100, saturation: 100, hue: 0 });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h5 className="text-sm font-medium text-foreground">Filters</h5>
        <button onClick={handleReset} className="text-xs text-accent hover:underline">Reset All</button>
      </div>
      {sliders.map(({ key, label, min, max, default: def, unit, formatValue }) => (
        <div key={key} className="flex items-center gap-3">
          <span className="text-sm text-muted w-24 flex-shrink-0">{label}</span>
          <input
            type="range"
            min={min}
            max={max}
            value={filters[key]}
            onChange={(e) => onChange({ ...filters, [key]: Number(e.target.value) })}
            className="flex-1 h-1 accent-accent cursor-pointer"
          />
          <button
            onClick={() => onChange({ ...filters, [key]: def })}
            className="text-xs text-secondary hover:text-foreground w-12 text-right cursor-pointer"
            title="Click to reset"
          >
            {formatValue ? formatValue(filters[key]) : `${filters[key]}${unit}`}
          </button>
        </div>
      ))}
    </div>
  );
}

/* ── Video Player with custom controls ── */
const PLAYBACK_RATES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
const VOLUME_KEY = "cove-player-volume";
const MUTED_KEY = "cove-player-muted";

function VideoPlayer({
  streamUrl,
  posterUrl,
  format,
  duration,
  resumeTime,
  sceneId,
  captions,
  onPlay,
  markers,
  onSeekRegister,
  onTimeUpdate: onTimeUpdateProp,
  autostart,
  showAbLoop,
  onEnded: onEndedProp,
  onPrev,
  onNext,
}: {
  streamUrl: string;
  posterUrl?: string;
  format: string;
  duration: number;
  resumeTime?: number;
  sceneId: number;
  captions?: { id: number; languageCode: string; captionType: string; filename: string }[];
  onPlay: () => void;
  markers: { id: number; title: string; seconds: number; primaryTagName: string }[];
  onSeekRegister?: (fn: (time: number) => void) => void;
  onTimeUpdate?: (time: number) => void;
  autostart?: boolean;
  showAbLoop?: boolean;
  onEnded?: () => void;
  onPrev?: () => void;
  onNext?: () => void;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurTime] = useState(0);
  const [buffered, setBuffered] = useState(0);
  const [vol, setVol] = useState(() => {
    const saved = localStorage.getItem(VOLUME_KEY);
    return saved ? Number(saved) : 1;
  });
  const [muted, setMuted] = useState(() => localStorage.getItem(MUTED_KEY) === "true");
  const [fullscreen, setFullscreen] = useState(false);
  const [showControls, setShowControls] = useState(true);
  const [showSpeed, setShowSpeed] = useState(false);
  const [rate, setRate] = useState(1);
  const [pip, setPip] = useState(false);
  const [loop, setLoop] = useState(false);
  const [abLoop, setAbLoop] = useState<{ a: number | null; b: number | null }>({ a: null, b: null });
  const [showCaptions, setShowCaptions] = useState(false);
  const [showQuality, setShowQuality] = useState(false);
  const [selectedQuality, setSelectedQuality] = useState<string>("Direct");
  const [availableQualities, setAvailableQualities] = useState<string[]>([]);
  const hideTimer = useRef<ReturnType<typeof setTimeout>>(null);
  const playTriggered = useRef(false);
  const activityTimer = useRef<ReturnType<typeof setTimeout>>(null);

  // Restore volume
  useEffect(() => {
    const v = videoRef.current;
    if (!v) return;
    v.volume = vol;
    v.muted = muted;
  }, []);

  // Register seek callback for external components (markers, scrubber)
  useEffect(() => {
    if (onSeekRegister) {
      onSeekRegister((time: number) => {
        const v = videoRef.current;
        if (v) {
          v.currentTime = time;
          v.play().catch(() => {});
        }
      });
    }
  }, [onSeekRegister]);

  // Resume from saved position
  useEffect(() => {
    const v = videoRef.current;
    if (v && resumeTime && resumeTime > 0) {
      v.currentTime = resumeTime;
    }
  }, [resumeTime]);

  // Autostart video
  useEffect(() => {
    if (autostart && videoRef.current) {
      videoRef.current.play().catch(() => {});
    }
  }, [autostart, streamUrl]);

  // PiP change listener
  useEffect(() => {
    const handler = () => setPip(document.pictureInPictureElement === videoRef.current);
    document.addEventListener("enterpictureinpicture", handler);
    document.addEventListener("leavepictureinpicture", handler);
    return () => {
      document.removeEventListener("enterpictureinpicture", handler);
      document.removeEventListener("leavepictureinpicture", handler);
    };
  }, []);

  // AirPlay: sync seek position when playback target changes (e.g. Apple TV)
  useEffect(() => {
    const v = videoRef.current as (HTMLVideoElement & { webkitShowPlaybackTargetPicker?: () => void }) | null;
    if (!v) return;
    const onTargetChanged = () => {
      // When switching to AirPlay target, re-apply current time after a brief delay
      const savedTime = v.currentTime;
      setTimeout(() => {
        if (v.currentTime < savedTime - 1) v.currentTime = savedTime;
      }, 500);
    };
    v.addEventListener("webkitcurrentplaybacktargetchanged" as any, onTargetChanged);
    return () => v.removeEventListener("webkitcurrentplaybacktargetchanged" as any, onTargetChanged);
  }, []);

  // A-B loop enforcement
  useEffect(() => {
    if (abLoop.a == null || abLoop.b == null) return;
    const v = videoRef.current;
    if (!v) return;
    const handler = () => {
      if (v.currentTime >= abLoop.b!) {
        v.currentTime = abLoop.a!;
      }
    };
    v.addEventListener("timeupdate", handler);
    return () => v.removeEventListener("timeupdate", handler);
  }, [abLoop]);

  // Save activity periodically
  useEffect(() => {
    const saveActivity = () => {
      const v = videoRef.current;
      if (v && !v.paused && v.currentTime > 0) {
        scenes.saveActivity(sceneId, { resumeTime: v.currentTime, playDuration: 5 }).catch(() => {});
      }
    };
    activityTimer.current = setInterval(saveActivity, 5000);
    return () => { if (activityTimer.current) clearInterval(activityTimer.current); };
  }, [sceneId]);

  // Fullscreen change listener
  useEffect(() => {
    const handler = () => setFullscreen(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", handler);
    return () => document.removeEventListener("fullscreenchange", handler);
  }, []);

  // Auto-hide controls
  const resetHideTimer = useCallback(() => {
    setShowControls(true);
    if (hideTimer.current) clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => {
      if (videoRef.current && !videoRef.current.paused) setShowControls(false);
    }, 3000);
  }, []);

  // Toggle text tracks when showCaptions state changes
  useEffect(() => {
    const v = videoRef.current;
    if (!v) return;
    for (let i = 0; i < v.textTracks.length; i++) {
      v.textTracks[i].mode = showCaptions ? "showing" : "hidden";
    }
  }, [showCaptions]);

  // Fetch available resolutions for quality selector
  useEffect(() => {
    scenes.getResolutions(sceneId).then((res) => setAvailableQualities(res ?? [])).catch(() => {});
  }, [sceneId]);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const v = videoRef.current;
      if (!v) return;
      const tag = (e.target as HTMLElement).tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;

      switch (e.key) {
        case " ":
        case "k":
          e.preventDefault();
          v.paused ? v.play() : v.pause();
          break;
        case "ArrowLeft":
          e.preventDefault();
          v.currentTime = Math.max(0, v.currentTime - (e.shiftKey ? 10 : 5));
          break;
        case "ArrowRight":
          e.preventDefault();
          v.currentTime = Math.min(v.duration, v.currentTime + (e.shiftKey ? 10 : 5));
          break;
        case "ArrowUp":
          e.preventDefault();
          v.volume = Math.min(1, v.volume + 0.1);
          setVol(v.volume);
          localStorage.setItem(VOLUME_KEY, String(v.volume));
          break;
        case "ArrowDown":
          e.preventDefault();
          v.volume = Math.max(0, v.volume - 0.1);
          setVol(v.volume);
          localStorage.setItem(VOLUME_KEY, String(v.volume));
          break;
        case "m":
          v.muted = !v.muted;
          setMuted(v.muted);
          localStorage.setItem(MUTED_KEY, String(v.muted));
          break;
        case "f":
          if (document.fullscreenElement) document.exitFullscreen();
          else containerRef.current?.requestFullscreen();
          break;
        case "0": case "1": case "2": case "3": case "4":
        case "5": case "6": case "7": case "8": case "9":
          e.preventDefault();
          v.currentTime = v.duration * (Number(e.key) / 10);
          break;
      }
      resetHideTimer();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [resetHideTimer]);

  const togglePlay = () => {
    const v = videoRef.current;
    if (!v) return;
    v.paused ? v.play() : v.pause();
  };

  const seekTo = (e: React.MouseEvent<HTMLDivElement>) => {
    const v = videoRef.current;
    if (!v) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    v.currentTime = pct * v.duration;
  };

  const changeVolume = (e: React.MouseEvent<HTMLDivElement>) => {
    const v = videoRef.current;
    if (!v) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    v.volume = pct;
    v.muted = false;
    setVol(pct);
    setMuted(false);
    localStorage.setItem(VOLUME_KEY, String(pct));
    localStorage.setItem(MUTED_KEY, "false");
  };

  const toggleFullscreen = () => {
    if (document.fullscreenElement) document.exitFullscreen();
    else containerRef.current?.requestFullscreen();
  };

  const changeRate = (r: number) => {
    const v = videoRef.current;
    if (v) v.playbackRate = r;
    setRate(r);
    setShowSpeed(false);
  };

  const changeQuality = (q: string) => {
    const v = videoRef.current;
    const curTime = v?.currentTime ?? 0;
    const wasPlaying = v ? !v.paused : false;
    setSelectedQuality(q);
    setShowQuality(false);
    // After source changes, the video element reloads via key; restore position
    setTimeout(() => {
      const v2 = videoRef.current;
      if (v2) {
        v2.currentTime = curTime;
        if (wasPlaying) v2.play().catch(() => {});
      }
    }, 100);
  };

  const effectiveStreamUrl = selectedQuality === "Direct" ? streamUrl : scenes.transcodeUrl(sceneId, selectedQuality);

  const togglePip = async () => {
    const v = videoRef.current;
    if (!v) return;
    try {
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      } else {
        await v.requestPictureInPicture();
      }
    } catch { /* PiP not supported or denied */ }
  };

  const cycleAbLoop = () => {
    const v = videoRef.current;
    if (!v) return;
    if (abLoop.a == null) {
      setAbLoop({ a: v.currentTime, b: null });
    } else if (abLoop.b == null) {
      setAbLoop({ a: abLoop.a, b: v.currentTime });
    } else {
      setAbLoop({ a: null, b: null });
    }
  };

  const fmtTime = (s: number) => {
    if (!isFinite(s)) return "0:00";
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = Math.floor(s % 60);
    return h > 0 ? `${h}:${m.toString().padStart(2, "0")}:${sec.toString().padStart(2, "0")}` : `${m}:${sec.toString().padStart(2, "0")}`;
  };

  return (
    <div
      ref={containerRef}
      className="relative group w-full h-full flex items-center justify-center bg-black"
      onMouseMove={resetHideTimer}
      onMouseLeave={() => playing && setShowControls(false)}
    >
      <video
        ref={videoRef}
        key={effectiveStreamUrl}
        className="w-full h-full object-contain cursor-pointer"
        preload="metadata"
        poster={posterUrl}
        {...{ "x-webkit-airplay": "allow" } as any}
        onClick={togglePlay}
        onDoubleClick={toggleFullscreen}
        onPlay={() => {
          setPlaying(true);
          if (!playTriggered.current) { playTriggered.current = true; onPlay(); }
        }}
        onPause={() => setPlaying(false)}
        onTimeUpdate={() => { const t = videoRef.current?.currentTime ?? 0; setCurTime(t); onTimeUpdateProp?.(t); }}
        onProgress={() => {
          const v = videoRef.current;
          if (v && v.buffered.length > 0) setBuffered(v.buffered.end(v.buffered.length - 1));
        }}
        onEnded={() => {
          if (loop) {
            const v = videoRef.current;
            if (v) { v.currentTime = 0; v.play().catch(() => {}); }
            return;
          }
          setPlaying(false);
          scenes.saveActivity(sceneId, { resumeTime: 0 }).catch(() => {});
          onEndedProp?.();
        }}
      >
        <source src={effectiveStreamUrl} type={`video/${format || "mp4"}`} />
        {captions?.map((cap, idx) => (
          <track
            key={cap.id}
            kind="captions"
            src={scenes.captionUrl(sceneId, cap.id)}
            srcLang={cap.languageCode === "00" ? "en" : cap.languageCode}
            label={cap.languageCode === "00" ? cap.filename : cap.languageCode.toUpperCase()}
            default={idx === 0 && showCaptions}
          />
        ))}
      </video>

      {/* Custom Controls Overlay */}
      <div
        className={`absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/90 via-black/50 to-transparent transition-opacity ${
          showControls ? "opacity-100" : "opacity-0 pointer-events-none"
        }`}
        style={{ padding: "40px 0 0 0" }}
      >
        {/* Seek bar */}
        <div className="px-3">
          <div className="relative h-4 flex items-center cursor-pointer group/seek" onClick={seekTo}>
            <div className="w-full h-1 bg-white/20 rounded-full group-hover/seek:h-1.5 transition-all relative">
              {/* Buffered */}
              <div className="absolute top-0 left-0 h-full bg-white/30 rounded-full" style={{ width: `${(buffered / (duration || 1)) * 100}%` }} />
              {/* Progress */}
              <div className="absolute top-0 left-0 h-full bg-accent rounded-full" style={{ width: `${(currentTime / (duration || 1)) * 100}%` }} />
              {/* Marker dots */}
              {markers.map((m) => (
                <div
                  key={m.id}
                  className="absolute top-1/2 -translate-y-1/2 w-2 h-2 bg-yellow-400 rounded-full cursor-pointer hover:scale-150 transition-transform z-10"
                  style={{ left: `${(m.seconds / (duration || 1)) * 100}%` }}
                  title={m.title || m.primaryTagName}
                  onClick={(e) => {
                    e.stopPropagation();
                    const v = videoRef.current;
                    if (v) { v.currentTime = m.seconds; v.play().catch(() => {}); }
                  }}
                />
              ))}
              {/* A-B loop range indicator */}
              {abLoop.a != null && (
                <div
                  className="absolute top-0 h-full bg-accent/25 pointer-events-none"
                  style={{
                    left: `${(abLoop.a / (duration || 1)) * 100}%`,
                    width: abLoop.b != null ? `${((abLoop.b - abLoop.a) / (duration || 1)) * 100}%` : "2px",
                  }}
                />
              )}
            </div>
            {/* Seek thumb */}
            <div
              className="absolute top-1/2 -translate-y-1/2 w-3 h-3 bg-accent rounded-full opacity-0 group-hover/seek:opacity-100 transition-opacity"
              style={{ left: `${(currentTime / (duration || 1)) * 100}%`, transform: "translate(-50%, -50%)" }}
            />
          </div>
        </div>

        {/* Controls row */}
        <div className="flex items-center gap-2 px-3 py-2 text-white">
          {/* Previous scene */}
          {onPrev && (
            <button onClick={onPrev} className="hover:text-accent p-1" title="Previous scene">
              <SkipBack className="w-4 h-4 fill-current" />
            </button>
          )}

          <button onClick={togglePlay} className="hover:text-accent p-1">
            {playing ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5" />}
          </button>

          {/* Next scene */}
          {onNext && (
            <button onClick={onNext} className="hover:text-accent p-1" title="Next scene">
              <SkipForward className="w-4 h-4 fill-current" />
            </button>
          )}

          <button onClick={() => { const v = videoRef.current; if (v) v.currentTime = Math.max(0, v.currentTime - 10); }} className="hover:text-accent p-1" title="Back 10s">
            <SkipBack className="w-4 h-4" />
          </button>
          <button onClick={() => { const v = videoRef.current; if (v) v.currentTime = Math.min(v.duration, v.currentTime + 10); }} className="hover:text-accent p-1" title="Forward 10s">
            <SkipForward className="w-4 h-4" />
          </button>

          {/* Volume */}
          <button onClick={() => {
            const v = videoRef.current;
            if (!v) return;
            v.muted = !v.muted;
            setMuted(v.muted);
            localStorage.setItem(MUTED_KEY, String(v.muted));
          }} className="hover:text-accent p-1">
            {muted || vol === 0 ? <VolumeX className="w-4 h-4" /> : <Volume2 className="w-4 h-4" />}
          </button>
          <div className="w-20 h-3 flex items-center cursor-pointer group/vol" onClick={changeVolume}>
            <div className="w-full h-1 bg-white/20 rounded-full relative">
              <div className="absolute top-0 left-0 h-full bg-white rounded-full" style={{ width: `${(muted ? 0 : vol) * 100}%` }} />
            </div>
          </div>

          <span className="text-xs text-white/70 ml-1 select-none tabular-nums">
            {fmtTime(currentTime)} / {fmtTime(duration)}
          </span>

          <div className="ml-auto flex items-center gap-2">
            {/* Playback speed */}
            <div className="relative">
              <button
                onClick={() => setShowSpeed(!showSpeed)}
                className={`hover:text-accent p-1 text-xs font-medium flex items-center gap-1 ${rate !== 1 ? "text-accent" : ""}`}
              >
                {rate}x
              </button>
              {showSpeed && (
                <div className="absolute bottom-full right-0 mb-2 bg-surface border border-border rounded shadow-lg py-1 z-10">
                  {PLAYBACK_RATES.map((r) => (
                    <button
                      key={r}
                      onClick={() => changeRate(r)}
                      className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${r === rate ? "text-accent" : "text-white"}`}
                    >
                      {r}x
                    </button>
                  ))}
                </div>
              )}
            </div>

            {/* A-B Loop */}
            {showAbLoop && (
              <button
                onClick={cycleAbLoop}
                className={`hover:text-accent p-1 text-xs font-medium flex items-center gap-1 ${abLoop.a != null ? "text-accent" : ""}`}
                title={abLoop.a == null ? "Set loop start (A)" : abLoop.b == null ? "Set loop end (B)" : "Clear A-B loop"}
              >
                <Repeat className="w-4 h-4" />
                {abLoop.a != null && abLoop.b == null && "A"}
                {abLoop.a != null && abLoop.b != null && "A-B"}
              </button>
            )}

            {/* Quality selector */}
            {availableQualities.length > 0 && (
              <div className="relative">
                <button
                  onClick={() => setShowQuality(!showQuality)}
                  className={`hover:text-accent p-1 text-xs font-medium ${selectedQuality !== "Direct" ? "text-accent" : ""}`}
                  title="Video quality"
                >
                  {selectedQuality === "Direct" ? "Direct" : selectedQuality}
                </button>
                {showQuality && (
                  <div className="absolute bottom-full right-0 mb-2 bg-surface border border-border rounded shadow-lg py-1 z-10">
                    <button
                      onClick={() => changeQuality("Direct")}
                      className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${selectedQuality === "Direct" ? "text-accent" : "text-white"}`}
                    >
                      Direct
                    </button>
                    {availableQualities.map((q) => (
                      <button
                        key={q}
                        onClick={() => changeQuality(q)}
                        className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${q === selectedQuality ? "text-accent" : "text-white"}`}
                      >
                        {q}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            )}

            {/* Loop entire video */}
            <button
              onClick={() => setLoop(!loop)}
              className={`hover:text-accent p-1 ${loop ? "text-accent" : ""}`}
              title={loop ? "Disable loop" : "Loop video"}
            >
              <Repeat1 className="w-4 h-4" />
            </button>

            {/* Picture-in-Picture */}
            <button onClick={togglePip} className={`hover:text-accent p-1 ${pip ? "text-accent" : ""}`} title="Picture-in-Picture">
              <PictureInPicture2 className="w-4 h-4" />
            </button>

            {/* Captions toggle */}
            {captions && captions.length > 0 && (
              <button
                onClick={() => setShowCaptions((prev) => !prev)}
                className={`hover:text-accent p-1 ${showCaptions ? "text-accent" : ""}`}
                title={showCaptions ? "Hide captions" : "Show captions"}
              >
                <Subtitles className="w-4 h-4" />
              </button>
            )}

            <button onClick={toggleFullscreen} className="hover:text-accent p-1">
              {fullscreen ? <Minimize className="w-4 h-4" /> : <Maximize className="w-4 h-4" />}
            </button>
          </div>
        </div>
      </div>

      {/* Big play button overlay when paused */}
      {!playing && (
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
          <div className="bg-black/40 rounded-full p-4">
            <Play className="w-12 h-12 text-white" />
          </div>
        </div>
      )}
    </div>
  );
}

// Scene Scrubber / Timeline Component
function SceneScrubber({ 
  sceneId, 
  duration, 
  markers,
  onSeek,
  currentTime,
}: { 
  sceneId: number; 
  duration: number; 
  markers: { id: number; title: string; seconds: number; primaryTagName: string }[];
  onSeek?: (time: number) => void;
  currentTime?: number;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [spriteData, setSpriteData] = useState<{ entries: { start: number; end: number; x: number; y: number; w: number; h: number }[]; imageUrl: string } | null>(null);
  const [spriteError, setSpriteError] = useState(false);
  const [spriteLoadSettled, setSpriteLoadSettled] = useState(false);
  
  const spriteVttUrl = `/api/stream/scene/${sceneId}/vtt/thumbs`;
  const spriteImageUrl = `/api/stream/scene/${sceneId}/sprite`;
  const screenshotUrl = `/api/stream/scene/${sceneId}/screenshot`;
  
  const formatTime = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = Math.floor(s % 60);
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  // Load and parse VTT sprite data
  useEffect(() => {
    let cancelled = false;

    setSpriteData(null);
    setSpriteError(false);
    setSpriteLoadSettled(false);

    fetch(spriteVttUrl)
      .then(r => { if (!r.ok) throw new Error("VTT not found"); return r.text(); })
      .then(text => {
        if (cancelled) return;
        const entries: typeof spriteData extends null ? never : NonNullable<typeof spriteData>["entries"] = [];
        const blocks = text.split(/\n\n+/);
        for (const block of blocks) {
          const lines = block.trim().split("\n");
          for (let i = 0; i < lines.length; i++) {
            const timeMatch = lines[i].match(/(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})/);
            if (timeMatch && lines[i + 1]) {
              const xywhMatch = lines[i + 1].match(/#xywh=(\d+),(\d+),(\d+),(\d+)/);
              if (xywhMatch) {
                entries.push({
                  start: parseVttTime(timeMatch[1]),
                  end: parseVttTime(timeMatch[2]),
                  x: parseInt(xywhMatch[1]),
                  y: parseInt(xywhMatch[2]),
                  w: parseInt(xywhMatch[3]),
                  h: parseInt(xywhMatch[4]),
                });
              }
            }
          }
        }
        if (entries.length > 0) {
          setSpriteData({ entries, imageUrl: spriteImageUrl });
        } else {
          setSpriteError(true);
        }
        setSpriteLoadSettled(true);
      })
      .catch(() => {
        if (cancelled) return;
        setSpriteError(true);
        setSpriteLoadSettled(true);
      });

    return () => {
      cancelled = true;
    };
  }, [sceneId, spriteVttUrl, spriteImageUrl]);

  const thumbCount = spriteData ? spriteData.entries.length : Math.min(Math.ceil(duration / 10), 60);
  const thumbWidth = 160;
  const thumbHeight = spriteData?.entries[0] ? Math.round(thumbWidth * (spriteData.entries[0].h / spriteData.entries[0].w)) : 90;

  // Determine which thumbnail index is active based on current video time
  const activeIndex = useMemo(() => {
    if (currentTime == null || currentTime <= 0) return -1;
    if (spriteData) {
      for (let i = spriteData.entries.length - 1; i >= 0; i--) {
        if (currentTime >= spriteData.entries[i].start) return i;
      }
      return 0;
    }
    // Fallback: evenly-spaced thumbs
    const interval = duration / thumbCount;
    return Math.min(Math.floor(currentTime / interval), thumbCount - 1);
  }, [currentTime, spriteData, duration, thumbCount]);

  // Auto-scroll to active thumbnail
  useEffect(() => {
    if (activeIndex >= 0 && scrollRef.current) {
      const targetLeft = activeIndex * thumbWidth;
      const { scrollLeft, clientWidth } = scrollRef.current;
      if (targetLeft < scrollLeft || targetLeft + thumbWidth > scrollLeft + clientWidth) {
        scrollRef.current.scrollTo({ left: Math.max(0, targetLeft - clientWidth / 2 + thumbWidth / 2), behavior: "smooth" });
      }
    }
  }, [activeIndex, thumbWidth]);

  const scroll = (dir: number) => {
    if (scrollRef.current) scrollRef.current.scrollBy({ left: dir * thumbWidth * 4, behavior: "smooth" });
  };
  
  return (
    <div className="flex-shrink-0 bg-[#1a1a1a] border-t border-border">
      {/* Markers bar */}
      {markers.length > 0 && (
        <div className="relative h-5 bg-[#333]">
          {markers.map((marker) => (
            <div
              key={marker.id}
              className="absolute top-0 h-full px-2 bg-accent/80 text-[10px] text-white flex items-center whitespace-nowrap cursor-pointer hover:bg-accent"
              style={{ 
                left: `${(marker.seconds / duration) * 100}%`,
                transform: "translateX(-50%)"
              }}
              title={`${marker.title} - ${marker.primaryTagName}`}
              onClick={() => onSeek?.(marker.seconds)}
            >
              {marker.title || marker.primaryTagName}
            </div>
          ))}
        </div>
      )}
      
      {/* Thumbnails scrubber - uses sprite sheet if available, falls back to individual screenshots */}
      <div className="relative flex overflow-hidden" ref={containerRef}>
        <button onClick={() => scroll(-1)} className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-r border-border z-10">
          <ChevronLeft className="w-4 h-4 mx-auto" />
        </button>
        
        <div ref={scrollRef} className="flex-1 flex overflow-x-auto scrollbar-thin scrollbar-thumb-border">
          {Array.from({ length: Math.max(thumbCount, 1) }).map((_, i) => {
            const time = spriteData ? spriteData.entries[i]?.start ?? (i / thumbCount) * duration : (i / thumbCount) * duration;
            const entry = spriteData?.entries[i];
            const isActive = i === activeIndex;
            return (
              <div 
                key={i} 
                className={`flex-shrink-0 relative cursor-pointer hover:ring-2 hover:ring-accent hover:z-10 ${isActive ? "ring-2 ring-accent z-10" : ""}`}
                style={{ width: thumbWidth }}
                onClick={() => onSeek?.(time)}
              >
                <div className="bg-surface" style={{ width: thumbWidth, height: thumbHeight }}>
                  {entry ? (
                    <div
                      style={{
                        width: thumbWidth,
                        height: thumbHeight,
                        backgroundImage: `url(${spriteData!.imageUrl})`,
                        backgroundPosition: `-${entry.x * (thumbWidth / entry.w)}px -${entry.y * (thumbHeight / entry.h)}px`,
                        backgroundSize: `${(spriteData!.entries[0].w * Math.ceil(Math.sqrt(thumbCount))) * (thumbWidth / entry.w)}px auto`,
                      }}
                    />
                  ) : spriteLoadSettled && spriteError ? (
                    <img 
                      src={`${screenshotUrl}?seconds=${Math.floor(time)}`} 
                      alt="" 
                      className="w-full h-full object-cover"
                      loading="lazy"
                      onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
                    />
                  ) : null}
                </div>
                <div className="absolute bottom-0 left-0 right-0 text-center text-[10px] text-white bg-black/70 py-0.5">
                  {formatTime(time)}
                </div>
              </div>
            );
          })}
        </div>
        
        <button onClick={() => scroll(1)} className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-l border-border z-10">
          <ChevronRight className="w-4 h-4 mx-auto" />
        </button>
      </div>
    </div>
  );
}

function parseVttTime(timeStr: string): number {
  const parts = timeStr.split(":");
  return parseInt(parts[0]) * 3600 + parseInt(parts[1]) * 60 + parseFloat(parts[2]);
}

// Markers Panel (for Markers tab)
function MarkersPanel({ 
  sceneId, 
  markers,
  onSeek,
}: { 
  sceneId: number; 
  markers: { id: number; title: string; seconds: number; endSeconds?: number; primaryTagId: number; primaryTagName: string }[];
  onSeek?: (time: number) => void;
}) {
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [title, setTitle] = useState("");
  const [seconds, setSeconds] = useState(0);
  const [tagSearch, setTagSearch] = useState("");
  const [selectedTagId, setSelectedTagId] = useState<number | null>(null);
  const [selectedTagName, setSelectedTagName] = useState("");

  const { data: tagResults } = useQuery({
    queryKey: ["tags-search", tagSearch],
    queryFn: () => tags.find({ q: tagSearch, perPage: 10 }),
    enabled: tagSearch.length >= 1,
  });

  const createMutation = useMutation({
    mutationFn: (data: SceneMarkerCreate) => scenes.markers.create(sceneId, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["scene", sceneId] });
      resetForm();
    },
  });

  const updateMutation = useMutation({
    mutationFn: (data: { id: number; title?: string; seconds?: number; primaryTagId?: number }) =>
      scenes.markers.update(sceneId, data.id, {
        title: data.title,
        seconds: data.seconds,
        primaryTagId: data.primaryTagId,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["scene", sceneId] });
      resetForm();
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (markerId: number) => scenes.markers.delete(sceneId, markerId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["scene", sceneId] }),
  });

  const resetForm = () => {
    setAdding(false);
    setEditingId(null);
    setTitle("");
    setSeconds(0);
    setTagSearch("");
    setSelectedTagId(null);
    setSelectedTagName("");
  };

  const startEdit = (marker: { id: number; title: string; seconds: number; primaryTagId: number; primaryTagName: string }) => {
    setAdding(true);
    setEditingId(marker.id);
    setTitle(marker.title || "");
    setSeconds(marker.seconds);
    setTagSearch("");
    setSelectedTagId(marker.primaryTagId);
    setSelectedTagName(marker.primaryTagName);
  };

  const formatTime = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = Math.floor(s % 60);
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-3">
        <span className="text-sm text-secondary">{markers.length} marker{markers.length !== 1 ? "s" : ""}</span>
        <button onClick={() => adding ? resetForm() : setAdding(true)} className="text-accent hover:underline text-sm flex items-center gap-1">
          <Plus className="w-3.5 h-3.5" /> Add
        </button>
      </div>

      {adding && (
        <div className="bg-card border border-border rounded p-3 mb-3 space-y-2">
          <input
            type="text"
            placeholder="Marker title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            className="w-full bg-input border border-border rounded px-3 py-1.5 text-sm text-foreground"
          />
          <div className="flex gap-2">
            <input
              type="number"
              placeholder="Seconds"
              value={seconds || ""}
              onChange={(e) => setSeconds(Number(e.target.value))}
              className="w-28 bg-input border border-border rounded px-3 py-1.5 text-sm text-foreground"
              min={0}
            />
            <div className="relative flex-1">
              <div className="flex items-center bg-input border border-border rounded px-3 py-1.5 text-sm">
                <Search className="w-3.5 h-3.5 text-muted mr-2 flex-shrink-0" />
                <input
                  type="text"
                  placeholder={selectedTagName || "Search tag..."}
                  value={tagSearch}
                  onChange={(e) => { setTagSearch(e.target.value); setSelectedTagId(null); setSelectedTagName(""); }}
                  className="bg-transparent w-full outline-none text-foreground"
                />
              </div>
              {tagSearch && tagResults && tagResults.items.length > 0 && (
                <div className="absolute z-10 mt-1 w-full bg-card border border-border rounded shadow-lg max-h-40 overflow-y-auto">
                  {tagResults.items.map((t: { id: number; name: string }) => (
                    <button
                      key={t.id}
                      onClick={() => { setSelectedTagId(t.id); setSelectedTagName(t.name); setTagSearch(""); }}
                      className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover hover:text-foreground"
                    >
                      {t.name}
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>
          <div className="flex gap-2 justify-end">
            <button onClick={resetForm} className="px-3 py-1 text-sm text-secondary hover:text-foreground">Cancel</button>
            <button
              onClick={() => selectedTagId && (editingId
                ? updateMutation.mutate({ id: editingId, title, seconds, primaryTagId: selectedTagId })
                : createMutation.mutate({ title, seconds, primaryTagId: selectedTagId }))}
              disabled={!selectedTagId || createMutation.isPending || updateMutation.isPending}
              className="px-3 py-1 text-sm bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50"
            >
              {editingId ? "Update" : "Save"}
            </button>
          </div>
        </div>
      )}

      {markers.length === 0 && !adding && (
        <p className="text-muted text-sm">No markers yet. Click Add to create one.</p>
      )}

      <div className="space-y-1">
        {markers.map((m) => (
          <div key={m.id} className="flex items-center justify-between bg-card border border-border rounded px-3 py-2 text-sm group">
            <button
              className="flex items-center gap-3 hover:text-accent transition-colors"
              onClick={() => onSeek?.(m.seconds)}
              title="Seek to marker"
            >
              <span className="text-accent font-mono text-xs w-12">{formatTime(m.seconds)}</span>
              <span className="text-foreground group-hover:text-accent">{m.title || "Untitled"}</span>
              <span className="text-xs text-secondary bg-surface px-1.5 py-0.5 rounded">{m.primaryTagName}</span>
            </button>
            <div className="flex items-center gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
              <button
                onClick={() => startEdit(m)}
                className="text-muted hover:text-accent"
                title="Edit marker"
              >
                <Pencil className="w-3.5 h-3.5" />
              </button>
              <button
                onClick={() => deleteMutation.mutate(m.id)}
                className="text-muted hover:text-red-400"
                title="Delete marker"
              >
                <Trash2 className="w-3.5 h-3.5" />
              </button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ===== Inline Scene Edit Panel =====
function SceneEditPanel({ scene, onSaved }: { scene: Scene; onSaved: () => void }) {
  const queryClient = useQueryClient();
  const [title, setTitle] = useState(scene.title || "");
  const [code, setCode] = useState(scene.code || "");
  const [details, setDetails] = useState(scene.details || "");
  const [director, setDirector] = useState(scene.director || "");
  const [date, setDate] = useState(scene.date || "");
  const [rating, setRating] = useState<number | undefined>(scene.rating ?? undefined);
  const [urls, setUrls] = useState(scene.urls.length > 0 ? scene.urls : [""]);
  const [studioId, setStudioId] = useState<number | undefined>(scene.studioId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(scene.tags.map((t) => t.id));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(scene.performers.map((p) => p.id));
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>(scene.galleries.map((g) => g.id));
  const [selectedGroups, setSelectedGroups] = useState<{ groupId: number; sceneIndex: number }[]>(
    scene.groups.map((g) => ({ groupId: g.id, sceneIndex: g.sceneIndex }))
  );
  const [tagSearch, setTagSearch] = useState("");
  const [perfSearch, setPerfSearch] = useState("");
  const [gallerySearch, setGallerySearch] = useState("");
  const [groupSearch, setGroupSearch] = useState("");
  const [studioSearch, setStudioSearch] = useState("");

  const { data: allTags } = useQuery({ queryKey: ["tags-all"], queryFn: () => tags.find({ perPage: 500, sort: "name", direction: "asc" }) });
  const { data: allPerformers } = useQuery({ queryKey: ["performers-all"], queryFn: () => performersApi.find({ perPage: 500, sort: "name", direction: "asc" }) });
  const { data: allStudios } = useQuery({ queryKey: ["studios-all"], queryFn: () => studiosApi.find({ perPage: 500, sort: "name", direction: "asc" }) });
  const { data: allGalleries } = useQuery({ queryKey: ["galleries-all"], queryFn: () => galleriesApi.find({ perPage: 500, sort: "title", direction: "asc" }) });
  const { data: allGroups } = useQuery({ queryKey: ["groups-all"], queryFn: () => groupsApi.find({ perPage: 500, sort: "name", direction: "asc" }) });

  useEffect(() => {
    setTitle(scene.title || ""); setCode(scene.code || ""); setDetails(scene.details || "");
    setDirector(scene.director || ""); setDate(scene.date || ""); setRating(scene.rating ?? undefined);
    setUrls(scene.urls.length > 0 ? scene.urls : [""]); setStudioId(scene.studioId ?? undefined);
    setSelectedTagIds(scene.tags.map((t) => t.id)); setSelectedPerformerIds(scene.performers.map((p) => p.id));
    setSelectedGalleryIds(scene.galleries.map((g) => g.id));
    setSelectedGroups(scene.groups.map((g) => ({ groupId: g.id, sceneIndex: g.sceneIndex })));
  }, [scene]);

  const mutation = useMutation({
    mutationFn: (data: SceneUpdate) => scenes.update(scene.id, data),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["scene", scene.id] }); queryClient.invalidateQueries({ queryKey: ["scenes"] }); onSaved(); },
  });

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    mutation.mutate({ title: title || undefined, code: code || undefined, details: details || undefined,
      director: director || undefined, date: date || undefined, rating, studioId,
      urls: urlList, tagIds: selectedTagIds, performerIds: selectedPerformerIds, galleryIds: selectedGalleryIds, groups: selectedGroups });
  };

  const filteredTags = allTags?.items.filter((t) => !selectedTagIds.includes(t.id) && t.name.toLowerCase().includes(tagSearch.toLowerCase())) ?? [];
  const filteredPerformers = allPerformers?.items.filter((p) => !selectedPerformerIds.includes(p.id) && p.name.toLowerCase().includes(perfSearch.toLowerCase())) ?? [];
  const filteredGalleries = allGalleries?.items.filter((g) => !selectedGalleryIds.includes(g.id) && (g.title || "").toLowerCase().includes(gallerySearch.toLowerCase())) ?? [];
  const selectedGroupIds = selectedGroups.map((g) => g.groupId);
  const filteredGroupsList = allGroups?.items.filter((g) => !selectedGroupIds.includes(g.id) && g.name.toLowerCase().includes(groupSearch.toLowerCase())) ?? [];
  const selectedTags = allTags?.items.filter((t) => selectedTagIds.includes(t.id)) ?? scene.tags;
  const selectedPerformers = allPerformers?.items.filter((p) => selectedPerformerIds.includes(p.id)) ?? scene.performers.map((p) => ({ ...p }));
  const selectedGalleries = allGalleries?.items.filter((g) => selectedGalleryIds.includes(g.id)) ?? scene.galleries;

  const inputCls = "w-full bg-input border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent";

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <label className="space-y-1"><span className="text-xs text-secondary">Title</span><input value={title} onChange={(e) => setTitle(e.target.value)} className={inputCls} /></label>
        <label className="space-y-1"><span className="text-xs text-secondary">Date</span><input type="date" value={date} onChange={(e) => setDate(e.target.value)} className={inputCls} /></label>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <label className="space-y-1"><span className="text-xs text-secondary">Studio Code</span><input value={code} onChange={(e) => setCode(e.target.value)} className={inputCls} /></label>
        <label className="space-y-1"><span className="text-xs text-secondary">Director</span><input value={director} onChange={(e) => setDirector(e.target.value)} className={inputCls} /></label>
      </div>
      <label className="block space-y-1"><span className="text-xs text-secondary">Details</span><textarea value={details} onChange={(e) => setDetails(e.target.value)} rows={3} className={inputCls} /></label>
      <div className="space-y-1">
        <span className="text-xs text-secondary">Studio</span>
        {studioId && allStudios?.items.find((s) => s.id === studioId) && (
          <div className="flex items-center gap-1 mb-1">
            <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-accent/20 text-accent">
              {allStudios.items.find((s) => s.id === studioId)!.name}
              <button onClick={() => setStudioId(undefined)} className="hover:text-white">×</button>
            </span>
          </div>
        )}
        {!studioId && (
          <>
            <input value={studioSearch} onChange={(e) => setStudioSearch(e.target.value)} placeholder="Search studios…" className={inputCls} />
            {studioSearch && allStudios && (
              <div className="max-h-24 overflow-y-auto bg-surface rounded border border-border">
                {allStudios.items.filter((s) => s.name.toLowerCase().includes(studioSearch.toLowerCase())).slice(0, 10).map((s) => (
                  <button key={s.id} onClick={() => { setStudioId(s.id); setStudioSearch(""); }} className="block w-full text-left px-3 py-1 text-sm text-foreground hover:bg-card">{s.name}</button>
                ))}
              </div>
            )}
          </>
        )}
      </div>
      <div className="space-y-1"><span className="text-xs text-secondary">URLs</span><StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" /></div>

      {/* Tags */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Tags</span>
        <div className="flex flex-wrap gap-1 mb-1">
          {selectedTags.map((t) => <span key={t.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-accent/20 text-accent">{t.name}<button onClick={() => setSelectedTagIds(selectedTagIds.filter((id) => id !== t.id))} className="hover:text-white">×</button></span>)}
        </div>
        <input value={tagSearch} onChange={(e) => setTagSearch(e.target.value)} placeholder="Search tags…" className={inputCls} />
        {tagSearch && filteredTags.length > 0 && <div className="max-h-24 overflow-y-auto bg-surface rounded border border-border">{filteredTags.slice(0, 10).map((t) => <button key={t.id} onClick={() => { setSelectedTagIds([...selectedTagIds, t.id]); setTagSearch(""); }} className="block w-full text-left px-3 py-1 text-sm text-foreground hover:bg-card">{t.name}</button>)}</div>}
      </div>

      {/* Performers */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Performers</span>
        <div className="flex flex-wrap gap-1 mb-1">
          {selectedPerformers.map((p) => <span key={p.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-accent/10 text-accent-hover">{p.name}<button onClick={() => setSelectedPerformerIds(selectedPerformerIds.filter((id) => id !== p.id))} className="hover:text-white">×</button></span>)}
        </div>
        <input value={perfSearch} onChange={(e) => setPerfSearch(e.target.value)} placeholder="Search performers…" className={inputCls} />
        {perfSearch && filteredPerformers.length > 0 && <div className="max-h-24 overflow-y-auto bg-surface rounded border border-border">{filteredPerformers.slice(0, 10).map((p) => <button key={p.id} onClick={() => { setSelectedPerformerIds([...selectedPerformerIds, p.id]); setPerfSearch(""); }} className="block w-full text-left px-3 py-1 text-sm text-foreground hover:bg-card">{p.name}{p.disambiguation ? ` (${p.disambiguation})` : ""}</button>)}</div>}
      </div>

      {/* Galleries */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Galleries</span>
        <div className="flex flex-wrap gap-1 mb-1">
          {selectedGalleries.map((g) => <span key={g.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-emerald-900 text-emerald-300">{g.title || "Untitled"}<button onClick={() => setSelectedGalleryIds(selectedGalleryIds.filter((id) => id !== g.id))} className="hover:text-white">×</button></span>)}
        </div>
        <input value={gallerySearch} onChange={(e) => setGallerySearch(e.target.value)} placeholder="Search galleries…" className={inputCls} />
        {gallerySearch && filteredGalleries.length > 0 && <div className="max-h-24 overflow-y-auto bg-surface rounded border border-border">{filteredGalleries.slice(0, 10).map((g) => <button key={g.id} onClick={() => { setSelectedGalleryIds([...selectedGalleryIds, g.id]); setGallerySearch(""); }} className="block w-full text-left px-3 py-1 text-sm text-foreground hover:bg-card">{g.title || "Untitled"}</button>)}</div>}
      </div>

      {/* Groups */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Groups</span>
        <div className="space-y-1 mb-1">
          {selectedGroups.map((sg) => {
            const group = allGroups?.items.find((g) => g.id === sg.groupId);
            return (
              <div key={sg.groupId} className="flex items-center gap-2">
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-orange-900 text-orange-300">
                  {group?.name || `Group #${sg.groupId}`}
                  <button onClick={() => setSelectedGroups(selectedGroups.filter((g) => g.groupId !== sg.groupId))} className="hover:text-white">×</button>
                </span>
                <label className="flex items-center gap-1 text-xs text-muted">
                  Scene #
                  <input type="number" min={0} value={sg.sceneIndex}
                    onChange={(e) => setSelectedGroups(selectedGroups.map((g) => g.groupId === sg.groupId ? { ...g, sceneIndex: Number(e.target.value) || 0 } : g))}
                    className="w-16 bg-surface border border-border rounded px-2 py-0.5 text-xs text-foreground focus:outline-none focus:border-accent" />
                </label>
              </div>
            );
          })}
        </div>
        <input value={groupSearch} onChange={(e) => setGroupSearch(e.target.value)} placeholder="Search groups…" className={inputCls} />
        {groupSearch && filteredGroupsList.length > 0 && <div className="max-h-24 overflow-y-auto bg-surface rounded border border-border">{filteredGroupsList.slice(0, 10).map((g) => <button key={g.id} onClick={() => { setSelectedGroups([...selectedGroups, { groupId: g.id, sceneIndex: 0 }]); setGroupSearch(""); }} className="block w-full text-left px-3 py-1 text-sm text-foreground hover:bg-card">{g.name}</button>)}</div>}
      </div>

      {mutation.error && <div className="bg-red-900/50 border border-red-700 text-red-300 rounded p-2 text-sm">{(mutation.error as Error).message}</div>}

      <div className="flex justify-end gap-3 pt-2">
        <button onClick={onSaved} className="px-4 py-2 text-sm text-secondary hover:text-foreground">Cancel</button>
        <button onClick={handleSave} disabled={mutation.isPending} className="px-4 py-2 text-sm bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50">
          {mutation.isPending ? "Saving…" : "Save"}
        </button>
      </div>
    </div>
  );
}
