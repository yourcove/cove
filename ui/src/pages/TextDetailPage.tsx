import { Suspense, lazy, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BookOpenText, Check, ChevronLeft, ChevronRight, Clapperboard, Download, ExternalLink, Files, FolderOpen, Image, Layers, Link2, MoreVertical, RefreshCw, Rows3, ThumbsUp, Trash2 } from "lucide-react";
import { entityImages, fileOps, playback, texts } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { BookmarkButton } from "../components/BookmarkButton";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { NarrativeText } from "../components/NarrativeText";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { ListLoadError } from "../components/ListLoadError";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { GenerateDialog } from "../components/GenerateDialog";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import type { MediaDetailTab } from "../components/MediaDetailLayout/types";
import type { FieldProvenance, VideoHistory } from "../api/types";
import { InteractiveRating } from "../components/Rating";
import { TextViewer } from "../components/TextViewer";
import { CustomFieldsDisplay, FieldProvenanceHover, formatDate, formatDuration, formatFileSize, TagBadge, resolveTagProvenance } from "../components/shared";
import { EntityRefBadge, MediaStudioSubtitle, PerformerTile, StudioHeaderImage } from "../components/EntityCards";
import { PerformerContextTagList, getPerformerContextTags } from "../components/PerformerContextTags";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { createPlaybackSessionId, trackInteraction } from "../utils/interactionTracking";
import { getTextDisplayTitle, pickPrimaryTextFile } from "../utils/audioTextDisplay";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { TextEditPanel } from "./TextEditPanel";
import { LikeHistorySection } from "../components/LikeHistorySection";

const MediaScrapeDialog = lazy(() => import("../components/MediaScrapeDialog").then((module) => ({ default: module.MediaScrapeDialog })));
const MediaDownloadDialog = lazy(() => import("../components/MediaDownloadDialog").then((module) => ({ default: module.MediaDownloadDialog })));

type TextTab = "details" | "file-info" | "history" | "edit";

interface Props {
  id: number;
  onNavigate: (route: any) => void;
}

function isPdfTextFile(file?: { format?: string | null; basename?: string | null; path?: string | null }) {
  const format = file?.format?.trim().toLowerCase();
  const basename = file?.basename?.trim().toLowerCase();
  const path = file?.path?.trim().toLowerCase();
  return format === "pdf" || basename?.endsWith(".pdf") || path?.endsWith(".pdf") || false;
}

function buildPdfFrameUrl(sourceUrl: string, page: number) {
  const fragmentSeparator = sourceUrl.includes("#") ? "&" : "#";
  return `${sourceUrl}${fragmentSeparator}page=${page}&zoom=page-width&view=FitH&toolbar=1&navpanes=0`;
}

function SourcePdfViewer({ title, sourceUrl, pageCount }: { title: string; sourceUrl: string; pageCount?: number | null }) {
  const [page, setPage] = useState(1);
  const normalizedPageCount = Number.isFinite(pageCount ?? NaN) && (pageCount ?? 0) > 0 ? Math.floor(pageCount ?? 0) : undefined;
  const frameUrl = buildPdfFrameUrl(sourceUrl, page);

  useEffect(() => {
    setPage(1);
  }, [sourceUrl]);

  const setClampedPage = (nextPage: number) => {
    const maxPage = normalizedPageCount ?? Number.MAX_SAFE_INTEGER;
    setPage(Math.min(maxPage, Math.max(1, Math.floor(nextPage))));
  };

  return (
    <div className="flex min-h-[70svh] flex-1 flex-col overflow-hidden rounded-lg border border-border bg-surface">
      <div className="flex flex-wrap items-center gap-2 border-b border-border bg-card/80 px-3 py-2 text-sm">
        <div className="mr-auto min-w-0 text-xs font-medium uppercase text-secondary">PDF</div>
        <div className="flex items-center gap-1">
          <button
            type="button"
            onClick={() => setClampedPage(page - 1)}
            disabled={page <= 1}
            className="inline-flex h-10 w-10 items-center justify-center rounded-md border border-border bg-background/70 text-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
            aria-label="Previous page"
          >
            <ChevronLeft className="h-4 w-4" />
          </button>
          <label className="flex items-center gap-1 text-xs text-secondary">
            <span>Page</span>
            <input
              type="number"
              min={1}
              max={normalizedPageCount}
              value={page}
              onChange={(event) => setClampedPage(Number(event.currentTarget.value))}
              className="h-10 w-16 rounded-md border border-border bg-input px-2 text-center text-sm text-foreground focus:border-accent focus:outline-none"
            />
            {normalizedPageCount ? <span>/ {normalizedPageCount}</span> : null}
          </label>
          <button
            type="button"
            onClick={() => setClampedPage(page + 1)}
            disabled={normalizedPageCount ? page >= normalizedPageCount : false}
            className="inline-flex h-10 w-10 items-center justify-center rounded-md border border-border bg-background/70 text-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
            aria-label="Next page"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
        </div>
        <a
          href={sourceUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex h-10 items-center gap-1 rounded-md border border-border bg-background/70 px-3 text-xs text-secondary hover:text-foreground"
        >
          <ExternalLink className="h-3.5 w-3.5" />
          Open
        </a>
      </div>
      <iframe
        title={title}
        src={frameUrl}
        className="min-h-0 flex-1 border-0 bg-surface"
        scrolling="yes"
      />
    </div>
  );
}

export function TextDetailPage({ id, onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { data: text, isLoading, error: textError, refetch: retryText } = useQuery({
    queryKey: ["text", id],
    queryFn: () => texts.get(id),
  });
  const textLoadError = getLoadError(text, textError);
  const { hasPermission, user } = useAuth();
  const { engagementById: performerEngagement } = useEntityEngagementBatch("performer", text?.performers?.map((p) => p.id) ?? []);
  const primaryFile = useMemo(() => pickPrimaryTextFile(text), [text]);
  const canStreamTextFile = hasPermission("stream.read");
  const primaryFileIsPdf = isPdfTextFile(primaryFile);
  const { data: content, isLoading: contentLoading, isError: contentError } = useQuery({
    queryKey: ["text", id, "content"],
    queryFn: () => texts.content(id),
    enabled: !!text && !(primaryFileIsPdf && canStreamTextFile),
  });
  const { backLabel, goBack } = useBackNavigation({ page: "texts" }, onNavigate);
  const [activeTab, setActiveTab] = useState<TextTab>("details");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const [showScrapeDialog, setShowScrapeDialog] = useState(false);
  const [showDownloadDialog, setShowDownloadDialog] = useState(false);
  const [showGenerate, setShowGenerate] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const canReadText = canReadEntity("text", hasPermission);
  const canWriteText = canWriteEntity("text", hasPermission);
  const canDeleteText = canDeleteEntity("text", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canReadGroups = canReadEntity("group", hasPermission);
  const canReadStudio = canReadEntity("studio", hasPermission);
  const canReadFiles = hasPermission("files.read");
  const canDeleteFiles = hasPermission("files.delete");
  const trackingEnabled = user?.uiPreferences?.tracking?.enabled ?? true;
  const canEngageText = canReadText && (user?.kind === "user" || user?.kind === "system");
  const trackTextActivity = canEngageText && trackingEnabled;
  const {
    engagement: textEngagement,
    favorite: textFavorite,
    setFavorite: setTextFavorite,
    favoritePending: textFavoritePending,
    rating: textRating,
    setRating: setTextRating,
  } = useEntityEngagement("text", id, {
    enabled: !!text && canEngageText,
    fallbackFavorite: false,
    fallbackRating: undefined,
  });
  const updateTextMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => texts.update(id, data),
    onSuccess: (updatedText) => {
      queryClient.setQueryData(["text", id], updatedText);
      queryClient.invalidateQueries({ queryKey: ["texts"] });
    },
  });
  const deleteTextMut = useMutation({
    mutationFn: (options?: { deleteFile?: boolean; deleteGenerated?: boolean }) => texts.delete(id, options),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["texts"] });
      goBack();
    },
  });
  const incrementLikeMut = useMutation({
    mutationFn: () => texts.incrementLike(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["text", id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "text", id] });
      queryClient.invalidateQueries({ queryKey: ["text", id, "history"] });
    },
  });
  const textHistoryQuery = useQuery({
    queryKey: ["text", id, "history"],
    queryFn: () => texts.getHistory(id),
    enabled: activeTab === "history" && canEngageText,
  });
  const revealFileMutation = useMutation({ mutationFn: (fileId: number) => fileOps.reveal(fileId) });
  const rescanTextMut = useMutation({ mutationFn: () => texts.rescan(id) });
  const canRevealFiles = typeof window !== "undefined" && ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);
  const canDownloadTextMedia = canWriteText && hasPermission("jobs.run") && (text?.files.length ?? 0) === 0 && (text?.urls.length ?? 0) > 0;
  const canLibraryScan = hasPermission("library.scan");
  const canGenerateText = hasPermission("jobs.run") && canWriteText;

  useDocumentTitle(text ? getTextDisplayTitle(text) : null);

  useEffect(() => {
    if (!text || !trackTextActivity) {
      return;
    }

    const sessionId = createPlaybackSessionId();
    const startedAt = performance.now();
    let recorded = false;

    trackInteraction({ hostType: "text", hostId: text.id, kind: "pageVisit", meta: { source: "textDetailPage" } });
    queryClient.invalidateQueries({ queryKey: ["engagement", "text", text.id] });

    const recordVisit = (state: "abandoned" | "ended") => {
      if (recorded) {
        return;
      }

      recorded = true;
      const durationSec = Math.max(0.001, (performance.now() - startedAt) / 1000);
      void playback.recordIntervals({
        hostType: "text",
        hostId: text.id,
        sessionId,
        mediaDurationSec: durationSec,
        currentPositionSec: durationSec,
        state,
        intervals: [{ startSec: 0, endSec: durationSec }],
      }).catch(() => {});
      queryClient.invalidateQueries({ queryKey: ["engagement", "text", text.id] });
    };

    const handlePageHide = () => recordVisit("abandoned");
    window.addEventListener("pagehide", handlePageHide);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      recordVisit("ended");
    };
  }, [queryClient, text, trackTextActivity]);

  const displayTitle = text ? getTextDisplayTitle(text) : `Text ${id}`;
  const textCoverUrl = text?.imagePath ?? undefined;
  const tabs = useMemo(() => {
    const nextTabs: MediaDetailTab[] = [{ key: "details", label: "Details" }];
    if (canReadFiles && (text?.files.length ?? 0) > 0) {
      nextTabs.push({ key: "file-info", label: "File Info", count: text?.files.length ?? 0 });
    }
    nextTabs.push({ key: "history", label: "History" });
    if (canWriteText) {
      nextTabs.push({ key: "edit", label: "Edit" });
    }
    return nextTabs;
  }, [canReadFiles, canReadGroups, canReadPerformers, canReadStudio, canReadTags, canWriteText, text?.files.length, text?.groups.length, text?.performers.length, text?.studioId, text?.tags.length]);

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
    return <DetailSkeleton showMedia={false} />;
  }

  if (isApiNotFoundError(textLoadError)) {
    return <div className="rounded-3xl border border-dashed border-border bg-card/70 px-6 py-10 text-sm text-muted">Text document #{id} was not found.</div>;
  }

  if (textLoadError) {
    return <ListLoadError error={textLoadError} onRetry={() => { void retryText(); }} title="Could not load text" className="mx-0 mt-0" />;
  }

  if (!text) {
    return <div className="rounded-3xl border border-dashed border-border bg-card/70 px-6 py-10 text-sm text-muted">Text document #{id} was not found.</div>;
  }

  const contentIsPdf = content?.format?.trim().toLowerCase() === "pdf";
  const showSourcePdf = canStreamTextFile && (primaryFileIsPdf || contentIsPdf);
  const textMedia = (
    <div className="flex min-h-0 flex-1 bg-background p-2 sm:p-4">
      {showSourcePdf ? (
        <SourcePdfViewer title={displayTitle} sourceUrl={texts.fileUrl(text.id)} pageCount={text.maxPageCount ?? primaryFile?.pageCount} />
      ) : contentLoading ? (
        <div className="flex min-h-[28rem] flex-1 items-center justify-center rounded-lg border border-border bg-surface text-sm text-muted">
          Loading extracted text content...
        </div>
      ) : content?.content ? (
        <TextViewer content={content.content} renderMode={content.renderMode} className="min-h-0 flex-1 rounded-lg" />
      ) : contentError && canStreamTextFile && text.files.length > 0 ? (
        <iframe
          title={displayTitle}
          src={texts.fileUrl(text.id)}
          className="h-full min-h-[28rem] w-full rounded-lg border border-border bg-surface"
        />
      ) : (
        <div className="flex min-h-[28rem] flex-1 items-center justify-center rounded-lg border border-dashed border-border bg-surface px-5 text-center text-sm text-muted">
          No readable text content is available for this document yet.
        </div>
      )}
    </div>
  );

  return (
    <>
    {text ? (
      <CoverImageDialog
        open={coverOpen}
        title="Set Text Cover"
        entityType="text"
        entityId={text.id}
        currentImageUrl={textCoverUrl}
        onUpload={(file) => entityImages.uploadTextImage(text.id, file)}
        onDelete={() => entityImages.deleteTextImage(text.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => {
          queryClient.invalidateQueries({ queryKey: ["text", text.id] });
          queryClient.invalidateQueries({ queryKey: ["texts"] });
        }}
        aspectRatio="2/3"
      />
    ) : null}
    <GenerateDialog open={showGenerate} onClose={() => setShowGenerate(false)} textIds={[id]} />
    <MediaDetailLayout
      title={<FieldProvenanceHover fieldProvenance={text.fieldProvenance} fieldKey="title">{displayTitle}</FieldProvenanceHover>}
      subtitle={<MediaStudioSubtitle date={text.date} studioId={text.studioId} studioName={text.studioName} fieldProvenance={text.fieldProvenance} onNavigate={onNavigate} canReadStudio={canReadStudio} />}
      backLabel={backLabel}
      onGoBack={goBack}
      headerImage={<StudioHeaderImage studioId={text.studioId} studioName={text.studioName} onNavigate={onNavigate} />}
      media={textMedia}
      mediaAspectRatio="auto"
      mediaFullBleed
      mediaSticky={false}
      tabs={tabs}
      activeTab={activeTab}
      onTabChange={(key) => setActiveTab(key as TextTab)}
      engagement={{
        primaryContent: <InteractiveRating value={textRating} onChange={(value) => setTextRating(value)} readOnly={!canEngageText} />,
        favorite: canEngageText ? textFavorite : undefined,
        favoritePending: textFavoritePending,
        onFavoriteChange: setTextFavorite,
        additionalMetrics: [
          {
            label: "Likes",
            value: textEngagement?.likeCount ?? 0,
            icon: <ThumbsUp className={["h-4 w-4", (textEngagement?.likeCount ?? 0) > 0 ? "fill-accent text-accent" : ""].join(" ")} />,
            title: "Likes",
            onClick: canWriteText ? () => incrementLikeMut.mutate() : undefined,
            active: (textEngagement?.likeCount ?? 0) > 0,
          },
          { label: "Words", value: text.maxWordCount ? Intl.NumberFormat().format(text.maxWordCount) : "-", icon: <BookOpenText className="h-4 w-4" /> },
          { label: "Pages", value: text.maxPageCount ?? "-", icon: <Files className="h-4 w-4" /> },
        ],
      }}
      actions={
        <>
          <BookmarkButton hostType="text" hostId={text.id} compact />
          {canWriteText ? (
            <button
              type="button"
              onClick={() => { if (!updateTextMut.isPending) updateTextMut.mutate({ organized: !text.organized }); }}
              disabled={updateTextMut.isPending}
              className={`inline-flex items-center justify-center rounded p-1 transition ${text.organized ? "bg-green-600 text-white" : "text-secondary hover:bg-card hover:text-foreground"} ${updateTextMut.isPending ? "cursor-not-allowed opacity-60" : ""}`}
              title={text.organized ? "Organized" : "Mark organized"}
            >
              <Check className="h-4 w-4" />
            </button>
          ) : text.organized ? (
            <span className="inline-flex items-center justify-center rounded bg-green-600 p-1 text-white" title="Organized">
              <Check className="h-4 w-4" />
            </span>
          ) : null}
          {canWriteText || canDeleteText ? (
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
                  {canWriteText ? (
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
                  {canDownloadTextMedia ? (
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
                  {canWriteText ? (
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
                  {(text.files?.length ?? 0) > 0 && canLibraryScan ? (
                    <button
                      type="button"
                      onClick={() => { rescanTextMut.mutate(); setShowOpsMenu(false); }}
                      disabled={rescanTextMut.isPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <RefreshCw className={["h-3.5 w-3.5", rescanTextMut.isPending ? "animate-spin" : ""].join(" ")} /> Rescan
                    </button>
                  ) : null}
                  {canGenerateText ? (
                    <button
                      type="button"
                      onClick={() => { setShowGenerate(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Clapperboard className="h-3.5 w-3.5" /> Generate…
                    </button>
                  ) : null}
                  {canDeleteText ? (
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
        title="Delete Text"
        message={`Delete "${displayTitle}"? This cannot be undone.`}
        confirmLabel={deleteTextMut.isPending ? "Deleting..." : "Delete Text"}
        onConfirm={(options) => deleteTextMut.mutate(options)}
        onCancel={() => setConfirmDelete(false)}
        showDeleteFile={canDeleteFiles}
        showDeleteGenerated
      />
      <MediaDetailLayout.Content>
        {activeTab === "details" ? (
          <div className="space-y-4">
            <div>
              <DetailGrid
                fieldProvenance={text.fieldProvenance}
                items={[
                  { label: "Words", value: text.maxWordCount ? Intl.NumberFormat().format(text.maxWordCount) : undefined },
                  { label: "Pages", value: text.maxPageCount ? String(text.maxPageCount) : undefined },
                  { label: "Files", value: String(text.fileCount) },
                ]}
              />
            </div>
            <AspectRatingsPanel hostType="text" hostId={text.id} canRate={canEngageText} />
            {text.details ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Notes</h3>
                <FieldProvenanceHover fieldProvenance={text.fieldProvenance} fieldKey="details" block>
                  <NarrativeText className="mt-3 text-sm leading-7 text-foreground/92">{text.details}</NarrativeText>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {text.urls.length > 0 ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Source URLs</h3>
                <FieldProvenanceHover fieldProvenance={text.fieldProvenance} fieldKey="urls" block className="mt-3">
                  <div className="flex flex-col gap-2">
                    {text.urls.map((url) => (
                      <a key={url} href={url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-sm text-accent transition hover:text-accent/80">
                        <Link2 className="h-4 w-4" />
                        <span className="truncate">{url}</span>
                      </a>
                    ))}
                  </div>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {canReadPerformers && text.performers.length > 0 ? (
              <div>
                <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-muted">Performers</h3>
                <FieldProvenanceHover fieldProvenance={text.fieldProvenance} fieldKey="performers" block className="mt-4">
                  <div className={text.performers.length > 1 ? "grid grid-cols-2 gap-3" : "grid max-w-[220px] gap-3"}>
                    {text.performers.map((performer) => {
                      const contextTags = getPerformerContextTags(text.contextTagApplications, performer.id);
                      return (
                        <PerformerTile
                          key={performer.id}
                          performer={performer}
                          engagement={performerEngagement.get(performer.id)}
                          onClick={() => onNavigate({ page: "performer", id: performer.id })}
                          onNavigate={onNavigate}
                          referenceDate={text.date}
                        >
                          {contextTags.length > 0 ? <div className="space-y-2 text-xs text-secondary"><PerformerContextTagList contextTags={contextTags} onNavigate={onNavigate} /></div> : null}
                        </PerformerTile>
                      );
                    })}
                  </div>
                </FieldProvenanceHover>
              </div>
            ) : null}
            {canReadTags && text.tags.length > 0 ? (
              <div>
                <h3 className="text-sm text-muted mb-2">Tags</h3>
                <div className="flex flex-wrap gap-2">
                  {text.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, text.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              </div>
            ) : null}
            {canReadGroups && text.groups.length > 0 ? (
              <div>
                <h3 className="text-sm text-muted mb-2">Groups</h3>
                <div className="flex flex-wrap gap-2">
                  {text.groups.map((group) => (
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
            {text.customFields && Object.keys(text.customFields).length > 0 ? (
              <MediaDetailLayout.Metadata>
                <CustomFieldsDisplay customFields={text.customFields} entityType="text" />
              </MediaDetailLayout.Metadata>
            ) : null}
          </div>
        ) : null}

        {activeTab === "file-info" ? (
          <div className="space-y-4">
            {text.files.map((file) => (
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
                    { label: "Pages", value: file.pageCount ? String(file.pageCount) : undefined },
                    { label: "Words", value: file.wordCount ? Intl.NumberFormat().format(file.wordCount) : undefined },
                    { label: "Size", value: formatFileSize(file.size) },
                    { label: "Excerpt", value: file.excerptText?.trim() || undefined },
                  ]}
                />
              </MediaDetailLayout.Metadata>
            ))}
          </div>
        ) : null}

        {activeTab === "history" ? (
          <TextHistoryTab
            pageVisitCount={textEngagement?.pageVisitCount ?? 0}
            timeOpen={textEngagement?.playDuration ?? 0}
            createdAt={text.createdAt}
            updatedAt={text.updatedAt}
            history={textHistoryQuery.data}
            historyLoading={textHistoryQuery.isLoading}
            canAddHistoricalLike={canWriteText}
            onAddHistoricalLike={async (at) => {
              await texts.addHistoricalLike(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["text", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "text", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "text", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["text", id, "history"] }),
              ]);
            }}
            onDeleteLike={async (at) => {
              await texts.deleteLikeFromHistory(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["text", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "text", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "text", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["text", id, "history"] }),
              ]);
            }}
          />
        ) : null}

        {activeTab === "edit" ? <TextEditPanel text={text} onSaved={() => setActiveTab("details")} /> : null}
      </MediaDetailLayout.Content>
    </MediaDetailLayout>
    {showScrapeDialog ? (
      <Suspense fallback={null}>
        <MediaScrapeDialog
          open={showScrapeDialog}
          onClose={() => setShowScrapeDialog(false)}
          entityType="text"
          entity={{
            id: text.id,
            title: text.title,
            code: text.code,
            details: text.details,
            date: text.date,
            studioName: text.studioName,
            urls: text.urls,
            tags: text.tags,
            performers: text.performers,
            files: text.files,
            organized: text.organized,
          }}
        />
      </Suspense>
    ) : null}
    {showDownloadDialog ? (
      <Suspense fallback={null}>
        <MediaDownloadDialog
          open={showDownloadDialog}
          entity="Text"
          item={text}
          listQueryKey="texts"
          detailQueryKey="text"
          routePage="text"
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
          <dd className="mt-1 whitespace-pre-wrap text-sm text-foreground">
            {item.fieldKey ? <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey={item.fieldKey}>{item.value}</FieldProvenanceHover> : item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function TextHistoryTab({
  pageVisitCount,
  timeOpen,
  createdAt,
  updatedAt,
  history,
  historyLoading,
  canAddHistoricalLike,
  onAddHistoricalLike,
  onDeleteLike,
}: {
  pageVisitCount: number;
  timeOpen: number;
  createdAt: string;
  updatedAt: string;
  history?: VideoHistory;
  historyLoading?: boolean;
  canAddHistoricalLike: boolean;
  onAddHistoricalLike: (at: string) => Promise<unknown>;
  onDeleteLike: (at: string) => Promise<unknown>;
}) {
  return (
    <div className="space-y-6 text-sm">
      <section>
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Reading History</h3>
        </div>
        <div className="mb-2 grid grid-cols-2 gap-2">
          <div><span className="text-muted">Page Visits:</span> <span className="text-foreground">{pageVisitCount}</span></div>
          <div><span className="text-muted">Time Open:</span> <span className="text-foreground">{formatDuration(timeOpen)}</span></div>
        </div>
      </section>

      <LikeHistorySection likeHistory={history?.likeHistory} loading={historyLoading} canAddHistoricalLike={canAddHistoricalLike} onAddHistoricalLike={onAddHistoricalLike} onDeleteLike={onDeleteLike} />

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
