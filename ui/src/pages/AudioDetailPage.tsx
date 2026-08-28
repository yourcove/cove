import { Suspense, lazy, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Clapperboard, Download, ExternalLink, Eye, FileAudio, Files, FolderOpen, Image, Layers, Link2, Mic2, MoreVertical, RefreshCw, Rows3, ThumbsUp, Trash2 } from "lucide-react";
import { audios, entityImages, fileOps } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { AudioPlayer } from "../components/AudioPlayer";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { BookmarkButton } from "../components/BookmarkButton";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { ListLoadError } from "../components/ListLoadError";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { GenerateDialog } from "../components/GenerateDialog";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import type { MediaDetailTab } from "../components/MediaDetailLayout/types";
import type { FieldProvenance, VideoHistory } from "../api/types";
import { InteractiveRating } from "../components/Rating";
import { CustomFieldsDisplay, FieldProvenanceHover, formatDate, formatDuration, formatFileSize, TagBadge, resolveTagProvenance } from "../components/shared";
import { EntityRefBadge, MediaStudioSubtitle, PerformerTile, StudioHeaderImage } from "../components/EntityCards";
import { PerformerContextTagList, getPerformerContextTags } from "../components/PerformerContextTags";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { VideoPlayer } from "../components/VideoPlayer";
import { trackInteraction } from "../utils/interactionTracking";
import { getAudioDisplayTitle, pickPrimaryAudioFile } from "../utils/audioTextDisplay";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { AudioEditPanel } from "./AudioEditPanel";
import { LikeHistorySection } from "../components/LikeHistorySection";

const MediaScrapeDialog = lazy(() => import("../components/MediaScrapeDialog").then((module) => ({ default: module.MediaScrapeDialog })));
const MediaDownloadDialog = lazy(() => import("../components/MediaDownloadDialog").then((module) => ({ default: module.MediaDownloadDialog })));

type AudioTab = "details" | "tracks" | "file-info" | "history" | "edit";

function getMutationErrorMessage(error: unknown) {
  return error instanceof Error ? error.message : error ? String(error) : null;
}

interface Props {
  id: number;
  onNavigate: (route: any) => void;
}

export function AudioDetailPage({ id, onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { data: audio, isLoading, error: audioError, refetch: retryAudio } = useQuery({
    queryKey: ["audio", id],
    queryFn: () => audios.get(id),
  });
  const audioLoadError = getLoadError(audio, audioError);
  const { hasPermission, user } = useAuth();
  const { engagementById: performerEngagement } = useEntityEngagementBatch("performer", audio?.performers?.map((p) => p.id) ?? []);
  const { backLabel, goBack } = useBackNavigation({ page: "audios" }, onNavigate);
  const [activeTab, setActiveTab] = useState<AudioTab>("details");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const [showScrapeDialog, setShowScrapeDialog] = useState(false);
  const [showDownloadDialog, setShowDownloadDialog] = useState(false);
  const [showGenerate, setShowGenerate] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const canReadAudio = canReadEntity("audio", hasPermission);
  const canWriteAudio = canWriteEntity("audio", hasPermission);
  const canDeleteAudio = canDeleteEntity("audio", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canReadGroups = canReadEntity("group", hasPermission);
  const canReadStudio = canReadEntity("studio", hasPermission);
  const canStreamAudio = hasPermission("stream.read");
  const canReadFiles = hasPermission("files.read");
  const canDeleteFiles = hasPermission("files.delete");
  const trackingEnabled = user?.uiPreferences?.tracking?.enabled ?? true;
  const canEngageAudio = canReadAudio && (user?.kind === "user" || user?.kind === "system");
  const trackAudioActivity = canEngageAudio && trackingEnabled;
  const {
    engagement: audioEngagement,
    rating: audioRating,
    setRating: setAudioRating,
  } = useEntityEngagement("audio", id, {
    enabled: !!audio && canEngageAudio,
    fallbackFavorite: false,
    fallbackRating: undefined,
  });
  const updateAudioMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => audios.update(id, data),
    onSuccess: (updatedAudio) => {
      queryClient.setQueryData(["audio", id], updatedAudio);
      queryClient.invalidateQueries({ queryKey: ["audios"] });
    },
  });
  const deleteAudioMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (options?: { deleteFile?: boolean; deleteGenerated?: boolean }) => audios.delete(id, options),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["audios"] });
      goBack();
    },
  });
  const incrementLikeMut = useMutation({
    mutationFn: () => audios.incrementLike(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["audio", id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "audio", id] });
      queryClient.invalidateQueries({ queryKey: ["audio", id, "history"] });
    },
  });
  const revealFileMutation = useMutation({ mutationFn: (fileId: number) => fileOps.reveal(fileId) });
  const rescanAudioMut = useMutation({ mutationFn: () => audios.rescan(id) });
  const canRevealFiles = typeof window !== "undefined" && ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);
  const canDownloadAudio = canWriteAudio && hasPermission("jobs.run") && (audio?.files.length ?? 0) === 0 && (audio?.urls.length ?? 0) > 0;
  const canLibraryScan = hasPermission("library.scan");
  const canGenerateAudio = hasPermission("jobs.run") && canWriteAudio;

  useDocumentTitle(audio ? getAudioDisplayTitle(audio) : null);

  useEffect(() => {
    if (!audio || !trackAudioActivity) {
      return;
    }

    trackInteraction({ hostType: "audio", hostId: audio.id, kind: "pageVisit", meta: { source: "audioDetailPage" } });
    queryClient.invalidateQueries({ queryKey: ["engagement", "audio", audio.id] });
  }, [audio, queryClient, trackAudioActivity]);

  const primaryFile = useMemo(() => pickPrimaryAudioFile(audio), [audio]);
  const audioHistoryQuery = useQuery({
    queryKey: ["audio", id, "history"],
    queryFn: () => audios.getHistory(id),
    enabled: activeTab === "history" && canEngageAudio,
  });
  const displayTitle = audio ? getAudioDisplayTitle(audio) : `Audio ${id}`;
  const subtitleText = useMemo(() => {
    if (!audio) {
      return undefined;
    }

    return [audio.performers.map((performer) => performer.name).filter(Boolean).join(", "), audio.studioName, audio.date ? formatDate(audio.date) : null]
      .filter(Boolean)
      .join(" • ") || undefined;
  }, [audio]);
  const detailSubtitle = subtitleText;
  const audioCoverUrl = audio?.imagePath ?? undefined;
  const tabs = useMemo(() => {
    const nextTabs: MediaDetailTab[] = [{ key: "details", label: "Details" }];
    if ((audio?.tracks.length ?? 0) > 0) {
      nextTabs.push({ key: "tracks", label: "Tracks", count: audio?.tracks.length ?? 0 });
    }
    if (canReadFiles && (audio?.files.length ?? 0) > 0) {
      nextTabs.push({ key: "file-info", label: "File Info", count: audio?.files.length ?? 0 });
    }
    nextTabs.push({ key: "history", label: "History" });
    if (canWriteAudio) {
      nextTabs.push({ key: "edit", label: "Edit" });
    }
    return nextTabs;
  }, [audio?.files.length, audio?.groups.length, audio?.performers.length, audio?.studioId, audio?.tags.length, audio?.tracks.length, canReadFiles, canReadGroups, canReadPerformers, canReadStudio, canReadTags, canWriteAudio]);

  useEffect(() => {
    if (!tabs.some((tab) => tab.key === activeTab)) {
      setActiveTab("details");
    }
  }, [activeTab, tabs]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(event.target as Node)) {
        setShowOpsMenu(false);
      }
    };

    if (showOpsMenu) {
      document.addEventListener("mousedown", handleClickOutside);
    }

    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, [showOpsMenu]);

  if (isLoading) {
    return <DetailSkeleton />;
  }

  if (isApiNotFoundError(audioLoadError)) {
    return <div className="rounded-3xl border border-dashed border-border bg-card/70 px-6 py-10 text-sm text-muted">Audio #{id} was not found.</div>;
  }

  if (audioLoadError) {
    return <ListLoadError error={audioLoadError} onRetry={() => { void retryAudio(); }} title="Could not load audio" className="mx-0 mt-0" />;
  }

  if (!audio) {
    return <div className="rounded-3xl border border-dashed border-border bg-card/70 px-6 py-10 text-sm text-muted">Audio #{id} was not found.</div>;
  }

  const audioPlayCount = audioEngagement?.playCount ?? 0;
  const audioPlayDuration = audioEngagement?.playDuration ?? 0;
  const audioPageVisitCount = audioEngagement?.pageVisitCount ?? 0;
  const audioLikeCount = audioEngagement?.likeCount ?? 0;

  const audioMedia = !canStreamAudio ? (
    <div className="flex h-full min-h-[20rem] items-center justify-center rounded-[2rem] border border-dashed border-border bg-card/70 text-sm text-muted">
      Playback is unavailable with your current permissions.
    </div>
  ) : primaryFile ? (
    primaryFile.hasVideoTrack ? (
      <div className="flex h-full min-h-0 w-full items-center justify-center bg-black">
        <VideoPlayer
          streamUrl={audios.streamUrl(audio.id)}
          format={primaryFile.format}
          duration={audio.maxDuration || primaryFile.duration}
          resumeTime={audioEngagement?.resumeTime}
          videoId={audio.id}
          trackingEnabled={trackAudioActivity}
          playbackTracking={{ hostType: "audio", hostId: audio.id, surface: "detail", scopeKey: `audio:${audio.id}` }}
          onEnded={() => queryClient.invalidateQueries({ queryKey: ["engagement", "audio", audio.id] })}
        />
      </div>
    ) : (
      <div className="flex h-full min-h-[45vh] w-full flex-col overflow-hidden bg-background">
        <div className="flex min-h-0 flex-1 items-center justify-center p-6 sm:p-8">
          {audio.imagePath ? (
            <img
              src={audio.imagePath}
              alt={`${displayTitle} cover`}
              className="max-h-[min(52vh,34rem)] max-w-[min(54vw,42rem)] rounded-lg border border-border bg-card object-contain shadow-lg"
            />
          ) : (
            <div className="flex h-32 w-32 items-center justify-center rounded-lg border border-border bg-card text-accent shadow-sm">
              <FileAudio className="h-14 w-14" />
            </div>
          )}
        </div>
        <div className="shrink-0 p-4 sm:p-6">
          <AudioPlayer
            streamUrl={audios.streamUrl(audio.id)}
            format={primaryFile.format}
            title={displayTitle}
            subtitle={subtitleText}
            coverUrl={audio.imagePath}
            duration={audio.maxDuration || primaryFile.duration}
            resumeTime={audioEngagement?.resumeTime}
            trackingEnabled={trackAudioActivity}
            playbackTracking={{ hostType: "audio", hostId: audio.id, surface: "detail", scopeKey: `audio:${audio.id}` }}
            onEnded={() => queryClient.invalidateQueries({ queryKey: ["engagement", "audio", audio.id] })}
          />
        </div>
      </div>
    )
  ) : (
    <div className="flex h-full min-h-[20rem] items-center justify-center rounded-[2rem] border border-dashed border-border bg-card/70 text-sm text-muted">
      No playable audio file is available.
    </div>
  );

  return (
    <>
    {audio ? (
      <CoverImageDialog
        open={coverOpen}
        title="Set Audio Cover"
        entityType="audio"
        entityId={audio.id}
        currentImageUrl={audioCoverUrl}
        onUpload={(file) => entityImages.uploadAudioImage(audio.id, file)}
        onDelete={() => entityImages.deleteAudioImage(audio.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => {
          queryClient.invalidateQueries({ queryKey: ["audio", audio.id] });
          queryClient.invalidateQueries({ queryKey: ["audios"] });
        }}
        aspectRatio="1/1"
      />
    ) : null}
    <GenerateDialog open={showGenerate} onClose={() => setShowGenerate(false)} audioIds={[id]} />
    <MediaDetailLayout
      title={<FieldProvenanceHover fieldProvenance={audio.fieldProvenance} fieldKey="title">{displayTitle}</FieldProvenanceHover>}
      subtitle={<MediaStudioSubtitle date={audio.date} studioId={audio.studioId} studioName={audio.studioName} fieldProvenance={audio.fieldProvenance} onNavigate={onNavigate} canReadStudio={canReadStudio} />}
      backLabel={backLabel}
      onGoBack={goBack}
      headerImage={<StudioHeaderImage studioId={audio.studioId} studioName={audio.studioName} onNavigate={onNavigate} />}
      media={audioMedia}
      mediaAspectRatio={primaryFile?.hasVideoTrack ? "video" : "auto"}
      mediaFullBleed={primaryFile?.hasVideoTrack ?? false}
      mediaSticky={false}
      tabs={tabs}
      activeTab={activeTab}
      onTabChange={(key) => setActiveTab(key as AudioTab)}
      engagement={{
        primaryContent: <InteractiveRating value={audioRating} onChange={(value) => setAudioRating(value)} readOnly={!canEngageAudio} />,
        additionalMetrics: [{
          label: "Likes",
          value: audioLikeCount,
          icon: <ThumbsUp className={["h-4 w-4", audioLikeCount > 0 ? "fill-accent text-accent" : ""].join(" ")} />,
          title: "Likes",
          onClick: canWriteAudio ? () => incrementLikeMut.mutate() : undefined,
          active: audioLikeCount > 0,
        }],
      }}
      actions={
        <>
          <BookmarkButton hostType="audio" hostId={audio.id} compact />
          {canWriteAudio ? (
            <button
              type="button"
              onClick={() => { if (!updateAudioMut.isPending) updateAudioMut.mutate({ organized: !audio.organized }); }}
              disabled={updateAudioMut.isPending}
              className={`inline-flex items-center justify-center rounded p-1 transition ${audio.organized ? "bg-green-600 text-white" : "text-secondary hover:bg-card hover:text-foreground"} ${updateAudioMut.isPending ? "cursor-not-allowed opacity-60" : ""}`}
              title={audio.organized ? "Organized" : "Mark organized"}
            >
              <Check className="h-4 w-4" />
            </button>
          ) : audio.organized ? (
            <span className="inline-flex items-center justify-center rounded bg-green-600 p-1 text-white" title="Organized">
              <Check className="h-4 w-4" />
            </span>
          ) : null}
          {canStreamAudio ? (
            <a
              href={audios.streamUrl(audio.id)}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
              title="Open in external player"
            >
              <ExternalLink className="h-4 w-4" />
            </a>
          ) : null}
          {canWriteAudio || canDeleteAudio ? (
            <div className="relative" ref={opsMenuRef}>
              <button
                type="button"
                onClick={() => setShowOpsMenu((current) => !current)}
                className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
                title="More actions"
              >
                <MoreVertical className="h-4 w-4" />
              </button>
              <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[220px] py-1">
                  {canWriteAudio ? (
                    <button
                      type="button"
                      onClick={() => {
                        setShowScrapeDialog(true);
                        setShowOpsMenu(false);
                      }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <ExternalLink className="h-3.5 w-3.5" /> Scrape...
                    </button>
                  ) : null}
                  {canDownloadAudio ? (
                    <button
                      type="button"
                      onClick={() => {
                        setShowDownloadDialog(true);
                        setShowOpsMenu(false);
                      }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Download className="h-3.5 w-3.5" /> Download Media...
                    </button>
                  ) : null}
                  {canWriteAudio ? (
                    <button
                      type="button"
                      onClick={() => {
                        setCoverOpen(true);
                        setShowOpsMenu(false);
                      }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Image className="h-3.5 w-3.5" /> Set Cover...
                    </button>
                  ) : null}
                  {(audio.files?.length ?? 0) > 0 && canLibraryScan ? (
                    <button
                      type="button"
                      onClick={() => { rescanAudioMut.mutate(); setShowOpsMenu(false); }}
                      disabled={rescanAudioMut.isPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <RefreshCw className={["h-3.5 w-3.5", rescanAudioMut.isPending ? "animate-spin" : ""].join(" ")} /> Rescan
                    </button>
                  ) : null}
                  {canGenerateAudio ? (
                    <button
                      type="button"
                      onClick={() => { setShowGenerate(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Clapperboard className="h-3.5 w-3.5" /> Generate…
                    </button>
                  ) : null}
                  {canDeleteAudio ? (
                    <button
                      type="button"
                      onClick={() => {
                        setConfirmDelete(true);
                        setShowOpsMenu(false);
                      }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-400 transition-colors hover:bg-surface"
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </button>
                  ) : null}
              </FloatingActionMenu>
            </div>
          ) : null}
        </>
      }
    >
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Audio"
        message={`Delete "${displayTitle}"? This cannot be undone.`}
        confirmLabel={deleteAudioMut.isPending ? "Deleting..." : "Delete Audio"}
        onConfirm={(options) => deleteAudioMut.mutate(options)}
        onCancel={() => { deleteAudioMut.reset(); setConfirmDelete(false); }}
        isPending={deleteAudioMut.isPending}
        errorMessage={getMutationErrorMessage(deleteAudioMut.error)}
        showDeleteFile={canDeleteFiles}
        showDeleteGenerated
      />
      <MediaDetailLayout.Content>
        {activeTab === "details" ? (
          <div className="space-y-4">
            <div>
              <DetailGrid
                fieldProvenance={audio.fieldProvenance}
                items={[
                  { label: "Duration", value: audio.maxDuration > 0 ? formatDuration(audio.maxDuration) : undefined },
                  { label: "Tracks", value: audio.tracks.length > 0 ? String(audio.tracks.length) : undefined },
                  { label: "Files", value: String(audio.fileCount) },
                ]}
              />
            </div>
            <AspectRatingsPanel hostType="audio" hostId={audio.id} canRate={canEngageAudio} />
            {audio.details ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Notes</h3>
                <FieldProvenanceHover fieldProvenance={audio.fieldProvenance} fieldKey="details" block>
                  <p className="mt-3 whitespace-pre-wrap text-sm leading-7 text-foreground/92">{audio.details}</p>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {audio.urls.length > 0 ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Source URLs</h3>
                <FieldProvenanceHover fieldProvenance={audio.fieldProvenance} fieldKey="urls" block className="mt-3">
                  <div className="flex flex-col gap-2">
                    {audio.urls.map((url) => (
                      <a key={url} href={url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-sm text-accent transition hover:text-accent/80">
                        <Link2 className="h-4 w-4" />
                        <span className="truncate">{url}</span>
                      </a>
                    ))}
                  </div>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {canReadPerformers && audio.performers.length > 0 ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Performers</h3>
                <FieldProvenanceHover fieldProvenance={audio.fieldProvenance} fieldKey="performers" block className="mt-4">
                  <div className={audio.performers.length > 1 ? "grid grid-cols-2 gap-3" : "grid max-w-[220px] gap-3"}>
                    {audio.performers.map((performer) => {
                      const contextTags = getPerformerContextTags(audio.contextTagApplications, performer.id);
                      return (
                        <PerformerTile
                          key={performer.id}
                          performer={performer}
                          engagement={performerEngagement.get(performer.id)}
                          onClick={() => onNavigate({ page: "performer", id: performer.id })}
                          onNavigate={onNavigate}
                          referenceDate={audio.date}
                        >
                          {contextTags.length > 0 ? <div className="space-y-2 text-xs text-secondary"><PerformerContextTagList contextTags={contextTags} onNavigate={onNavigate} /></div> : null}
                        </PerformerTile>
                      );
                    })}
                  </div>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {canReadTags && audio.tags.length > 0 ? (
              <div>
                <h3 className="text-sm text-muted mb-2">Tags</h3>
                <div className="flex flex-wrap gap-2">
                  {audio.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, audio.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              </div>
            ) : null}
            {canReadGroups && audio.groups.length > 0 ? (
              <div>
                <h3 className="text-sm text-muted mb-2">Groups</h3>
                <div className="flex flex-wrap gap-2">
                  {audio.groups.map((group) => (
                    <EntityRefBadge
                      key={group.id}
                      route={{ page: "group", id: group.id }}
                      onNavigate={onNavigate}
                      imageUrl={entityImages.groupFrontImageUrl(group.id)}
                      icon={<Layers className="h-5 w-5" />}
                      label={group.name}
                    />
                  ))}
                </div>
              </div>
            ) : null}
            {audio.customFields && Object.keys(audio.customFields).length > 0 ? (
              <MediaDetailLayout.Metadata>
                <CustomFieldsDisplay customFields={audio.customFields} entityType="audio" />
              </MediaDetailLayout.Metadata>
            ) : null}
          </div>
        ) : null}

        {activeTab === "tracks" ? (
          <section className="rounded-3xl border border-border bg-card/75 p-4">
            <div className="space-y-3">
              {audio.tracks.map((track) => (
                <div key={track.id} className="flex items-center justify-between gap-3 rounded-2xl border border-border/80 bg-background/75 px-4 py-3 text-sm">
                  <div className="min-w-0">
                    <div className="font-medium text-foreground">{track.title?.trim() || `Track ${track.orderIndex + 1}`}</div>
                    <div className="text-xs text-muted">Track {track.orderIndex + 1}</div>
                  </div>
                  <div className="shrink-0 text-xs text-muted">
                    {formatDuration(track.startSec)}
                    {track.endSec != null ? ` - ${formatDuration(track.endSec)}` : ""}
                  </div>
                </div>
              ))}
            </div>
          </section>
        ) : null}

        {activeTab === "file-info" ? (
          <div className="space-y-4">
            {audio.files.map((file) => (
              <MediaDetailLayout.Metadata key={file.id}>
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <h3 className="text-sm font-semibold text-foreground">{file.basename}</h3>
                    <p className="text-xs text-muted">{file.path}</p>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    {canRevealFiles && file.id ? (
                      <button
                        type="button"
                        onClick={() => revealFileMutation.mutate(file.id)}
                        className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
                      >
                        <FolderOpen className="h-3.5 w-3.5" />
                        Reveal
                      </button>
                    ) : null}
                    <span className="rounded-full border border-border px-2.5 py-1 text-[11px] font-medium uppercase tracking-[0.18em] text-muted">{file.format}</span>
                  </div>
                </div>
                <DetailGrid
                  items={[
                    { label: "Duration", value: file.duration > 0 ? formatDuration(file.duration) : undefined },
                    { label: "Codec", value: file.audioCodec || undefined },
                    { label: "Bitrate", value: file.bitRate > 0 ? `${Math.round(file.bitRate / 1000)} kbps` : undefined },
                    { label: "Sample Rate", value: file.sampleRate ? `${Intl.NumberFormat().format(file.sampleRate)} Hz` : undefined },
                    { label: "Channels", value: file.channels ? String(file.channels) : undefined },
                    { label: "Size", value: formatFileSize(file.size) },
                    { label: "Video Track", value: file.hasVideoTrack ? "Yes" : "No" },
                  ]}
                />
              </MediaDetailLayout.Metadata>
            ))}
          </div>
        ) : null}

        {activeTab === "history" ? (
          <AudioHistoryTab
            playCount={audioPlayCount}
            playDuration={audioPlayDuration}
            pageVisitCount={audioPageVisitCount}
            history={audioHistoryQuery.data}
            historyLoading={audioHistoryQuery.isLoading}
            createdAt={audio.createdAt}
            updatedAt={audio.updatedAt}
            canAddHistoricalLike={canWriteAudio}
            onAddHistoricalLike={async (at) => {
              await audios.addHistoricalLike(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["audio", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "audio", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "audio", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["audio", id, "history"] }),
              ]);
            }}
            onDeleteLike={async (at) => {
              await audios.deleteLikeFromHistory(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["audio", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "audio", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "audio", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["audio", id, "history"] }),
              ]);
            }}
          />
        ) : null}

        {activeTab === "edit" ? <AudioEditPanel audio={audio} onSaved={() => setActiveTab("details")} /> : null}
      </MediaDetailLayout.Content>
    </MediaDetailLayout>
    {showScrapeDialog ? (
      <Suspense fallback={null}>
        <MediaScrapeDialog
          open={showScrapeDialog}
          onClose={() => setShowScrapeDialog(false)}
          entityType="audio"
          entity={{
            id: audio.id,
            title: audio.title,
            code: audio.code,
            details: audio.details,
            date: audio.date,
            studioName: audio.studioName,
            urls: audio.urls,
            tags: audio.tags,
            performers: audio.performers,
            files: audio.files,
            organized: audio.organized,
          }}
        />
      </Suspense>
    ) : null}
    {showDownloadDialog ? (
      <Suspense fallback={null}>
        <MediaDownloadDialog
          open={showDownloadDialog}
          entity="Audio"
          item={audio}
          listQueryKey="audios"
          detailQueryKey="audio"
          routePage="audio"
          onClose={() => setShowDownloadDialog(false)}
          onNavigate={onNavigate}
        />
      </Suspense>
    ) : null}
    </>
  );
}

function DetailGrid({ items, fieldProvenance }: { items: { label: string; value?: string; fieldKey?: string | string[] }[]; fieldProvenance?: FieldProvenance[] }) {
  const visibleItems = items.filter((item) => item.value != null && String(item.value).trim() !== "");
  if (visibleItems.length === 0) {
    return <p className="text-sm text-muted">No metadata available.</p>;
  }

  return (
    <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
      {visibleItems.map((item) => (
        <div key={item.label}>
          <dt className="text-[11px] font-medium uppercase tracking-[0.18em] text-muted">{item.label}</dt>
          <dd className="mt-1 text-sm text-foreground">
            {item.fieldKey ? <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey={item.fieldKey}>{item.value}</FieldProvenanceHover> : item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function AudioHistoryTab({
  playCount,
  playDuration,
  pageVisitCount,
  history,
  historyLoading,
  createdAt,
  updatedAt,
  canAddHistoricalLike,
  onAddHistoricalLike,
  onDeleteLike,
}: {
  playCount: number;
  playDuration: number;
  pageVisitCount: number;
  history?: VideoHistory;
  historyLoading?: boolean;
  createdAt: string;
  updatedAt: string;
  canAddHistoricalLike: boolean;
  onAddHistoricalLike: (at: string) => Promise<unknown>;
  onDeleteLike: (at: string) => Promise<unknown>;
}) {
  const sessions = history?.sessions ?? [];
  return (
    <div className="space-y-6 text-sm">
      <section>
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Play History</h3>
        </div>
        <div className="mb-2 grid grid-cols-2 gap-2">
          <div><span className="text-muted">Play Count:</span> <span className="text-foreground">{playCount}</span></div>
          <div><span className="text-muted">Listened:</span> <span className="text-foreground">{formatDuration(playDuration)}</span></div>
          <div><span className="text-muted">Page Visits:</span> <span className="text-foreground">{pageVisitCount}</span></div>
          {history ? <div><span className="text-muted">Distinct Listened:</span> <span className="text-foreground">{formatDuration(history.totalDistinctWatchedSec ?? 0)}</span></div> : null}
        </div>
      </section>

      <LikeHistorySection
        likeHistory={history?.likeHistory}
        loading={historyLoading}
        canAddHistoricalLike={canAddHistoricalLike}
        onAddHistoricalLike={onAddHistoricalLike}
        onDeleteLike={onDeleteLike}
      />

      <section>
        <h3 className="mb-2 text-sm font-semibold uppercase tracking-wide text-muted">Sessions</h3>
        {historyLoading ? (
          <p className="text-muted">Loading history...</p>
        ) : sessions.length > 0 ? (
          <div className="space-y-2">
            {sessions.slice(0, 8).map((session) => (
              <div key={session.sessionId} className="rounded-md border border-border bg-card/60 p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="text-foreground">{formatDuration(session.totalWatchedSec)}</span>
                  <span className="text-xs uppercase text-muted">{session.state}</span>
                </div>
                <div className="mt-1 text-xs text-muted">
                  {formatDate(session.startedAt)}
                  {session.lastPositionSec != null ? ` • Resume ${formatDuration(session.lastPositionSec)}` : ""}
                </div>
              </div>
            ))}
          </div>
        ) : (
          <p className="text-muted">No playback sessions recorded yet.</p>
        )}
      </section>

      <div className="grid grid-cols-2 gap-2">
        <div><span className="text-muted">Created:</span> <span className="text-foreground">{formatDate(createdAt)}</span></div>
        <div><span className="text-muted">Updated:</span> <span className="text-foreground">{formatDate(updatedAt)}</span></div>
      </div>
    </div>
  );
}

function RelatedSection({ icon, title, children }: { icon: React.ReactNode; title: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="flex items-center gap-2 text-sm font-semibold uppercase tracking-[0.18em] text-muted">
        {icon}
        {title}
      </div>
      <div className="mt-4 flex flex-wrap gap-2">{children}</div>
    </div>
  );
}
