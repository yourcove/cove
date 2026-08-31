import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { entityImages, faces, images, playback, fileOps, galleries } from "../api/client";
import { formatDate, TagBadge, CustomFieldsDisplay, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { EntityRefBadge, PerformerTile, StudioHeaderImage } from "../components/EntityCards";
import { Check, Clapperboard, Download, Eye, FolderOpen, Image as ImageIcon, ImageOff, Layers, Link as LinkIcon, Loader2, Maximize, MoreVertical, RefreshCw, Scissors, Search, Sparkles, ThumbsUp, Trash2, UserRound, UserX } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, lazy, Suspense } from "react";
import { Lightbox, type LightboxImage } from "../components/Lightbox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { NarrativeText } from "../components/NarrativeText";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { FaceSplitDialog } from "../components/FaceSplitDialog";
import { ListLoadError } from "../components/ListLoadError";
import { useFaceCapabilities } from "../hooks/useFaceCapabilities";
import { ExtensionSlot } from "../router/RouteRegistry";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { InteractiveRating } from "../components/Rating";
import { createRouteLinkProps } from "../components/cardNavigation";
import { ExtensionEntityActions } from "../components/ExtensionEntityActions";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { GenerateDialog } from "../components/GenerateDialog";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import type { FaceHostFace, TagApplication } from "../api/types";
import { createPlaybackSessionId, trackInteraction } from "../utils/interactionTracking";
import { ImageVisualSimilarityPanel, useImageVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { PerformerContextTagList, getPerformerContextTags } from "../components/PerformerContextTags";
import { ImageEditPanel } from "./ImageEditModal";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { LikeHistorySection } from "../components/LikeHistorySection";

const ImageDownloadDialog = lazy(() => import("../components/ImageDownloadDialog").then((module) => ({ default: module.ImageDownloadDialog })));
const MediaScrapeDialog = lazy(() => import("../components/MediaScrapeDialog").then((module) => ({ default: module.MediaScrapeDialog })));

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type ImageTab = "details" | "file-info" | "similar" | "detections" | "history" | "edit";

type ImageCoverTarget = {
  key: string;
  label: string;
  subtitle: string;
  run: () => Promise<unknown>;
};

export function ImageDetailPage({ id, onNavigate }: Props) {
  const { data: image, isLoading, error: imageError, refetch: retryImage } = useQuery({
    queryKey: ["image", id],
    queryFn: () => images.get(id),
  });
  const imageLoadError = getLoadError(image, imageError);
  const { hasPermission, user } = useAuth();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const [imageLoadFailed, setImageLoadFailed] = useState(false);
  const [showDownloadDialog, setShowDownloadDialog] = useState(false);
  const [showScrapeDialog, setShowScrapeDialog] = useState(false);
  const [showCoverTargetDialog, setShowCoverTargetDialog] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [showGenerate, setShowGenerate] = useState(false);
  const [activeTab, setActiveTab] = useState<ImageTab>("details");
  const queryClient = useQueryClient();
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const { backLabel, goBack } = useBackNavigation({ page: "images" }, onNavigate);
  const canWriteImage = canWriteEntity("image", hasPermission);
  const canDeleteImage = canDeleteEntity("image", hasPermission);
  const canDownloadImage = hasPermission("jobs.run") && canWriteImage;
  const canLibraryScan = hasPermission("library.scan");
  const canGenerateImage = hasPermission("jobs.run") && canWriteImage;
  const canEngageImage = canReadEntity("image", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canReadFaces = canReadEntity("face", hasPermission);
  const canWriteFaces = canWriteEntity("face", hasPermission);
  // Needs a provider extension registering an IFaceOccurrenceEditor; hidden when none is installed.
  const { canEditOccurrences } = useFaceCapabilities(canWriteFaces);
  const canReadFiles = hasPermission("files.read");
  const canReadStudios = canReadEntity("studio", hasPermission);
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canReadGalleries = canReadEntity("gallery", hasPermission);
  const canReadGroups = canReadEntity("group", hasPermission);
  const canWriteStudios = canWriteEntity("studio", hasPermission);
  const canWritePerformers = canWriteEntity("performer", hasPermission);
  const canWriteTags = canWriteEntity("tag", hasPermission);
  const canWriteGalleries = canWriteEntity("gallery", hasPermission);
  const canWriteGroups = canWriteEntity("group", hasPermission);
  const trackingEnabled = user?.uiPreferences?.tracking?.enabled ?? true;
  const trackImageActivity = canEngageImage && trackingEnabled;
  const {
    engagement: imageEngagement,
    favorite: imageFavorite,
    favoritePending: imageFavoritePending,
    setFavorite: setImageFavorite,
    rating: imageRating,
    setRating: setImageRating,
  } = useEntityEngagement("image", id, {
    enabled: !!image && canEngageImage,
    fallbackRating: undefined,
  });
  const { data: imageFacesData, error: imageFacesError, refetch: retryImageFaces } = useQuery({
    queryKey: ["image", id, "faces"],
    queryFn: () => faces.imageFaces(id),
    enabled: canReadFaces,
  });
  const imageFacesLoadError = getLoadError(imageFacesData, imageFacesError);
  const imageFaces = imageFacesData ?? [];
  // Splits the wrong-person occurrences off a face that isn't really in this image (AI.Faces extension).
  const markFaceNotPresentMut = useMutation({
    mutationFn: (faceId: number) => faces.markNotPresent(faceId, { hostType: "image", hostId: Number(id) }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["image", id, "faces"] });
      queryClient.invalidateQueries({ queryKey: ["face"] });
    },
  });
  // Separating two people tangled into one face, one appearance at a time rather than rejecting the
  // whole image (see FaceSplitDialog).
  const [splitFace, setSplitFace] = useState<FaceHostFace | null>(null);
  const deleteMut = useMutation({
    mutationFn: () => images.delete(id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["images"] }); goBack(); },
  });
  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => images.update(id, data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["image", id] }),
  });
  const incrementLikeMut = useMutation({
    mutationFn: () => images.incrementLike(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["image", id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "image", id] });
      queryClient.invalidateQueries({ queryKey: ["image", id, "history"] });
      queryClient.invalidateQueries({ queryKey: ["gallery-like-count"] });
    },
  });
  const imageHistoryQuery = useQuery({
    queryKey: ["image", id, "history"],
    queryFn: () => images.getHistory(id),
    enabled: activeTab === "history" && canEngageImage,
  });
  const revealFileMutation = useMutation({ mutationFn: (fileId: number) => fileOps.reveal(fileId) });
  const rescanMut = useMutation({ mutationFn: () => images.rescan(id) });
  const setImageAsCoverMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (target: ImageCoverTarget) => target.run(),
    onSuccess: () => {
      queryClient.invalidateQueries();
      setShowCoverTargetDialog(false);
    },
  });
  const canRevealFiles = typeof window !== "undefined" && ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);
  const imageLikeCount = imageEngagement?.likeCount ?? 0;
  const imagePageVisitCount = imageEngagement?.pageVisitCount ?? 0;
  const displayTitle = image ? getImageDisplayTitle(image) : `Image ${id}`;
  const lightboxImages = useMemo<LightboxImage[]>(() => {
    if (!image) {
      return [];
    }

    return [{
      id: image.id,
      src: images.imageUrl(image.id),
      title: displayTitle,
      interactionSource: "imageDetailPage",
      interactionMeta: { pageKey: "imageDetail", route: `/image/${image.id}` },
    }];
  }, [displayTitle, image]);
  const hasVisualSimilarity = useImageVisualSimilarityAvailable(id);
  const tabs = useMemo(() => {
    const nextTabs = [
      { key: "details", label: "Details" },
      ...(canReadFiles ? [{ key: "file-info", label: "File Info", count: image?.files.length ?? 0 }] : []),
      ...(hasVisualSimilarity ? [{ key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> }] : []),
      ...(imageFaces.length > 0 ? [{ key: "detections", label: "Faces", count: imageFaces.length }] : []),
      { key: "history", label: "History" },
      ...(canWriteImage ? [{ key: "edit", label: "Edit" }] : []),
    ];
    return nextTabs;
  }, [canReadFiles, canWriteImage, hasVisualSimilarity, image?.files.length, imageFaces.length]);

  useEffect(() => {
    if (!tabs.some((tab) => tab.key === activeTab)) {
      setActiveTab("details");
    }
  }, [activeTab, tabs]);

  useDocumentTitle(image ? displayTitle : null);

  useEffect(() => {
    if (!image || !trackImageActivity) return;

    const imageId = image.id;
    const startedAt = performance.now();
    const sessionId = createPlaybackSessionId();
    trackInteraction({
      hostType: "image",
      hostId: imageId,
      kind: "pageVisit",
      meta: { source: "imageDetailPage" },
    });
    queryClient.invalidateQueries({ queryKey: ["engagement", "image", imageId] });

    let flushed = false;
    const flushDwell = (state: "ended" | "abandoned") => {
      if (flushed) return;
      flushed = true;
      const durationSec = Math.max(0.001, (performance.now() - startedAt) / 1000);
      void playback.recordIntervals({
        hostType: "image",
        hostId: imageId,
        sessionId,
        mediaDurationSec: durationSec,
        currentPositionSec: durationSec,
        state,
        intervals: [{ startSec: 0, endSec: durationSec }],
      }).catch(() => {});
      queryClient.invalidateQueries({ queryKey: ["engagement", "image", imageId] });
    };
    const handlePageHide = () => flushDwell("abandoned");
    window.addEventListener("pagehide", handlePageHide);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      flushDwell("ended");
    };
  }, [image?.id, queryClient, trackImageActivity]);

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

  useEffect(() => {
    setImageLoadFailed(false);
  }, [id]);

  const openLightbox = useCallback(() => {
    if (imageLoadFailed) {
      return;
    }

    setLightboxOpen(true);
  }, [imageLoadFailed]);

  const closeLightbox = useCallback(() => {
    setLightboxOpen(false);
  }, []);
  const imageKeyboardShortcuts = useMemo(() => ([
    {
      id: "detail.edit",
      key: "e",
      description: "Open edit tab",
      handler: () => {
        if (canWriteImage) {
          setActiveTab("edit");
        }
      },
    },
    {
      id: "detail.image.likes",
      key: "l",
      description: "Likes",
      handler: () => {
        if (canWriteImage) {
          incrementLikeMut.mutate();
        }
      },
    },
    {
      id: "detail.image.lightbox",
      key: "f",
      description: "Toggle fullscreen lightbox",
      handler: () => {
        if (lightboxOpen) {
          closeLightbox();
        } else {
          openLightbox();
        }
      },
    },
    {
      id: "detail.image.detections",
      key: "d",
      description: "Open detections tab",
      handler: () => setActiveTab("detections"),
    },
    {
      key: "Escape",
      description: "Close lightbox",
      handler: () => closeLightbox(),
    },
  ]), [canWriteImage, closeLightbox, incrementLikeMut, lightboxOpen, openLightbox]);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(imageLoadError)) {
    return <div className="text-center text-secondary py-16">Image not found</div>;
  }

  if (imageLoadError) {
    return <ListLoadError error={imageLoadError} onRetry={() => { void retryImage(); }} title="Could not load image" className="mx-0 mt-0" />;
  }

  if (!image) return <div className="text-center text-secondary py-16">Image not found</div>;

  const currentImage = image;
  const imageCoverTargets: ImageCoverTarget[] = [
    ...(canWriteStudios && currentImage.studioId ? [{
      key: `studio:${currentImage.studioId}`,
      label: currentImage.studioName || `Studio ${currentImage.studioId}`,
      subtitle: "Studio cover",
      run: () => entityImages.setStudioImageFromSource(currentImage.studioId!, { imageId: currentImage.id }),
    }] : []),
    ...(canWritePerformers ? currentImage.performers.map((performer) => ({
      key: `performer:${performer.id}`,
      label: performer.name,
      subtitle: "Performer cover",
      run: () => entityImages.setPerformerImageFromSource(performer.id, { imageId: currentImage.id }),
    })) : []),
    ...(canWriteTags ? currentImage.tags.map((tag) => ({
      key: `tag:${tag.id}`,
      label: tag.name,
      subtitle: "Tag cover",
      run: () => entityImages.setTagImageFromSource(tag.id, { imageId: currentImage.id }),
    })) : []),
    ...(canWriteGalleries ? currentImage.galleries.map((gallery) => ({
      key: `gallery:${gallery.id}`,
      label: gallery.title || `Gallery ${gallery.id}`,
      subtitle: "Gallery cover",
      run: () => entityImages.setGalleryImageFromSource(gallery.id, { imageId: currentImage.id }),
    })) : []),
    ...(canWriteGroups ? (currentImage.groups ?? []).map((group) => ({
      key: `group:${group.id}`,
      label: group.name,
      subtitle: "Group cover",
      run: () => entityImages.setGroupFrontImageFromSource(group.id, { imageId: currentImage.id }),
    })) : []),
  ];

  function renderRelatedContent() {
    return (
      <div className="space-y-5">
        {canReadPerformers && currentImage.performers.length > 0 ? (
          <section>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Performers</h2>
            <FieldProvenanceHover fieldProvenance={currentImage.fieldProvenance} fieldKey="performers" block className="mt-3">
              <div className={currentImage.performers.length > 1 ? "grid grid-cols-2 gap-3" : "grid max-w-[220px] gap-3"}>
                {currentImage.performers.map((performer) => (
                  <PerformerTile
                    key={performer.id}
                    performer={performer}
                    onClick={() => onNavigate({ page: "performer", id: performer.id })}
                    onNavigate={onNavigate}
                    referenceDate={currentImage.date}
                  >
                    {getPerformerContextTags(currentImage.contextTagApplications, performer.id).length > 0 ? <div className="space-y-2 text-xs text-secondary"><PerformerContextTagList contextTags={getPerformerContextTags(currentImage.contextTagApplications, performer.id)} onNavigate={onNavigate} /></div> : null}
                  </PerformerTile>
                ))}
              </div>
            </FieldProvenanceHover>
          </section>
        ) : null}

        {canReadTags && currentImage.tags.length > 0 ? (
          <section>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Tags</h2>
            <div className="mt-3 flex flex-wrap gap-1.5">
              {currentImage.tags.map((tag) => (
                <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, currentImage.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
              ))}
            </div>
          </section>
        ) : null}

        {canReadGalleries && currentImage.galleries.length > 0 ? (
          <section>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Galleries</h2>
            <div className="mt-3 flex flex-wrap gap-2">
              {currentImage.galleries.map((gallery) => (
                <EntityRefBadge
                  key={gallery.id}
                  route={{ page: "gallery", id: gallery.id }}
                  onNavigate={onNavigate}
                  imageUrl={galleries.coverUrl(gallery.id)}
                  icon={<FolderOpen className="h-5 w-5" />}
                  label={gallery.title || `Gallery ${gallery.id}`}
                />
              ))}
            </div>
          </section>
        ) : null}

        {canReadGroups && (currentImage.groups?.length ?? 0) > 0 ? (
          <section>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Groups</h2>
            <div className="mt-3 flex flex-wrap gap-2">
              {(currentImage.groups ?? []).map((group) => (
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
          </section>
        ) : null}

        {(!canReadPerformers || currentImage.performers.length === 0) && (!canReadTags || currentImage.tags.length === 0) && (!canReadGalleries || currentImage.galleries.length === 0) && (!canReadGroups || (currentImage.groups?.length ?? 0) === 0) ? (
          <EmptyPanel icon={<UserRound className="h-10 w-10" />} message="No related performers, studio, tags, galleries, or groups are linked to this image yet." />
        ) : null}
      </div>
    );
  }

  const detailsContent = (
    <div className="space-y-5">
      {image.details ? (
        <section>
          <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Description</h2>
          <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="details" block>
            <NarrativeText className="mt-2 text-sm leading-relaxed text-secondary">{image.details}</NarrativeText>
          </FieldProvenanceHover>
        </section>
      ) : null}

      <section>
        <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Details</h2>
        <dl className="mt-3 grid gap-y-1.5 text-sm" style={{ gridTemplateColumns: "auto 1fr" }}>
          <dt className="pr-3 text-muted">Created</dt>
          <dd className="text-foreground">{formatDate(image.createdAt)}</dd>
          <dt className="pr-3 text-muted">Updated</dt>
          <dd className="text-foreground">{formatDate(image.updatedAt)}</dd>
        </dl>
      </section>

      <AspectRatingsPanel hostType="image" hostId={id} canRate={canEngageImage} />

      {image.urls.length > 0 ? (
        <section>
          <h2 className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-muted">
            <LinkIcon className="h-3.5 w-3.5" /> URLs
          </h2>
          <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="urls" block>
            <div className="space-y-1">
              {image.urls.map((url, index) => (
                <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="block truncate text-sm text-accent hover:underline">
                  {url}
                </a>
              ))}
            </div>
          </FieldProvenanceHover>
        </section>
      ) : null}

      {renderRelatedContent()}

      <CustomFieldsDisplay customFields={image.customFields} entityType="image" />
      <ExtensionSlot slot="image-detail-sidebar-bottom" context={{ image, onNavigate }} />
    </div>
  );

  const fileInfoContent = canReadFiles ? (
    image.files.length > 0 ? (
      <section>
        <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">File Info</h2>
        <div className="mt-3 space-y-3">
          {image.files.map((file) => (
            <div key={file.id} className="space-y-2 rounded-xl border border-border/60 bg-card/60 p-3">
              {canRevealFiles && file.id ? (
                <div className="flex justify-end">
                  <button
                    type="button"
                    onClick={() => revealFileMutation.mutate(file.id)}
                    className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
                  >
                    <FolderOpen className="h-3.5 w-3.5" />
                    Reveal
                  </button>
                </div>
              ) : null}
              <dl className="grid gap-2 md:grid-cols-2">
                <DetailField label="Path" value={<span className="break-all font-mono text-[11px]">{file.path}</span>} />
                <DetailField label="Dimensions" value={`${file.width} x ${file.height}`} />
                <DetailField label="Format" value={file.format} />
                <DetailField label="Size" value={`${(file.size / 1024 / 1024).toFixed(2)} MB`} />
              </dl>
            </div>
          ))}
        </div>
      </section>
    ) : (
      <EmptyPanel icon={<ImageOff className="h-10 w-10" />} message="No image file metadata is available yet." />
    )
  ) : (
    <EmptyPanel icon={<ImageOff className="h-10 w-10" />} message="File metadata is unavailable with your current permissions." />
  );

  const detectionsContent = (
    <section>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xs font-semibold uppercase tracking-wide text-muted">Faces</h2>
          <p className="mt-1 text-sm text-secondary">Face clusters attached to this image.</p>
        </div>
        <div className="text-xs text-muted">{imageFacesLoadError ? "Unavailable" : `${imageFaces.length} face${imageFaces.length === 1 ? "" : "s"}`}</div>
      </div>

      {imageFacesLoadError ? (
        <ListLoadError error={imageFacesLoadError} onRetry={() => { void retryImageFaces(); }} className="mt-4" />
      ) : canReadFaces ? (
        imageFaces.length > 0 ? (
          <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
            {imageFaces.map((face) => {
              const title = face.performerName?.trim() || face.label?.trim() || `Face #${face.id}`;
              const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "face", id: face.id }, () => onNavigate({ page: "face", id: face.id }));
              const isMarking = markFaceNotPresentMut.isPending && markFaceNotPresentMut.variables === face.id;

              return (
                <div key={face.id} className="group relative flex items-stretch">
                  <a
                    {...linkProps}
                    className="flex w-full items-center gap-2 rounded-lg border border-border bg-surface/35 px-2 py-2 transition-colors hover:border-accent"
                  >
                    <div className="flex h-8 w-8 shrink-0 items-center justify-center overflow-hidden rounded-md bg-surface text-[10px] text-muted">
                      {face.coverImageUrl ? (
                        <img src={face.coverImageUrl} alt={title} className="h-full w-full object-cover" loading="lazy" />
                      ) : (
                        title.slice(0, 2).toUpperCase()
                      )}
                    </div>
                    <div className="min-w-0">
                      <div className="truncate text-sm text-foreground">{title}</div>
                      <div className="text-[11px] text-secondary">{formatImageFaceSummary(face)}</div>
                    </div>
                  </a>
                  {canWriteFaces && canEditOccurrences ? (
                    <div className={`absolute right-1 top-1 flex gap-1 transition-opacity group-hover:opacity-100 ${isMarking ? "opacity-100" : "opacity-0"}`}>
                      {face.hostTrackCount > 1 ? (
                        <button
                          type="button"
                          title={`Detected as ${face.hostTrackCount} separate appearances — separate a different person out of this face`}
                          aria-label="Separate people in this face"
                          onClick={() => setSplitFace(face)}
                          className="rounded-md bg-surface/80 p-1 text-muted transition-colors hover:text-accent"
                        >
                          <Scissors className="h-3.5 w-3.5" />
                        </button>
                      ) : null}
                      <button
                        type="button"
                        title="This face is not actually present in this image"
                        aria-label="Mark face not present in this image"
                        disabled={isMarking}
                        onClick={() => {
                          if (window.confirm(`Mark "${title}" as NOT present in this image?\n\nIts occurrence here (and other media that matches it) will be split off into the correct face.`)) {
                            markFaceNotPresentMut.mutate(face.id);
                          }
                        }}
                        className="rounded-md bg-surface/80 p-1 text-muted transition-colors hover:text-red-300 disabled:cursor-not-allowed"
                      >
                        {isMarking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UserX className="h-3.5 w-3.5" />}
                      </button>
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        ) : (
          <EmptyPanel icon={<Maximize className="h-10 w-10" />} message="No face detections are attached to this image yet." />
        )
      ) : (
        <EmptyPanel icon={<Maximize className="h-10 w-10" />} message="Face detections are unavailable with your current permissions." />
      )}
    </section>
  );

  const activeContent = activeTab === "file-info"
    ? fileInfoContent
    : activeTab === "similar"
      ? <ImageVisualSimilarityPanel imageId={image.id} onNavigate={onNavigate} />
    : activeTab === "detections"
      ? detectionsContent
      : activeTab === "history"
        ? <LikeHistorySection
            likeHistory={imageHistoryQuery.data?.likeHistory}
            loading={imageHistoryQuery.isLoading}
            canAddHistoricalLike={canWriteImage}
            onAddHistoricalLike={async (at) => {
              await images.addHistoricalLike(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["image", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "image", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "image", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["image", id, "history"] }),
                queryClient.invalidateQueries({ queryKey: ["gallery-like-count"] }),
              ]);
            }}
            onDeleteLike={async (at) => {
              await images.deleteLikeFromHistory(id, at);
              await Promise.all([
                queryClient.invalidateQueries({ queryKey: ["image", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "image", id] }),
                queryClient.invalidateQueries({ queryKey: ["engagement", "image", "batch"] }),
                queryClient.invalidateQueries({ queryKey: ["image", id, "history"] }),
                queryClient.invalidateQueries({ queryKey: ["gallery-like-count"] }),
              ]);
            }}
          />
      : activeTab === "edit"
          ? <ImageEditPanel image={image} onSaved={() => setActiveTab("details")} />
          : detailsContent;

  return (
    <>
      <Suspense fallback={null}>
        {showDownloadDialog ? (
          <ImageDownloadDialog
            open={showDownloadDialog}
            image={image}
            onClose={() => setShowDownloadDialog(false)}
            onNavigate={onNavigate}
          />
        ) : null}
        {showScrapeDialog ? (
          <MediaScrapeDialog
            open={showScrapeDialog}
            onClose={() => setShowScrapeDialog(false)}
            entityType="image"
            entity={{
              id: image.id,
              title: image.title,
              details: image.details,
              creator: image.photographer,
              date: image.date,
              studioName: image.studioName,
              urls: image.urls,
              tags: image.tags,
              performers: image.performers,
              files: image.files,
              organized: image.organized,
            }}
          />
        ) : null}
        <CoverImageDialog
          open={showCoverTargetDialog}
          title="Set Cover"
          entityType="image"
          entityId={id}
          currentImageUrl={images.imageUrl(id)}
          objectFit="contain"
          aspectRatio="4/3"
          onClose={() => { setImageAsCoverMut.reset(); setShowCoverTargetDialog(false); }}
          extraActions={(
            <ImageCoverTargetActions
              targets={imageCoverTargets}
              pending={setImageAsCoverMut.isPending}
              error={setImageAsCoverMut.error}
              onSelect={(target) => setImageAsCoverMut.mutate(target)}
            />
          )}
        />
      </Suspense>
      <ConfirmDialog open={confirmDelete} title="Delete Image" message={`Delete "${displayTitle}"? This cannot be undone.`} onConfirm={() => deleteMut.mutate()} onCancel={() => setConfirmDelete(false)} />

      <GenerateDialog open={showGenerate} onClose={() => setShowGenerate(false)} imageIds={[id]} />

      <FaceSplitDialog
        open={splitFace != null}
        faceId={splitFace?.id ?? null}
        faceTitle={splitFace ? (splitFace.performerName?.trim() || splitFace.label?.trim() || `Face #${splitFace.id}`) : ""}
        hostType="image"
        hostId={Number(id)}
        onClose={() => setSplitFace(null)}
        onSplit={() => {
          queryClient.invalidateQueries({ queryKey: ["image", id, "faces"] });
          queryClient.invalidateQueries({ queryKey: ["face"] });
        }}
        onMarkNotPresent={canWriteFaces && canEditOccurrences && splitFace ? () => markFaceNotPresentMut.mutate(splitFace.id) : undefined}
      />

      <Lightbox
        images={lightboxImages}
        initialIndex={0}
        open={lightboxOpen && lightboxImages.length > 0}
        onClose={closeLightbox}
        canEngage={canEngageImage}
        canLike={canWriteImage}
      />
      <MediaDetailLayout
        title={<FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="title">{displayTitle}</FieldProvenanceHover>}
        headerImage={canReadStudios ? <StudioHeaderImage studioId={image.studioId} studioName={image.studioName} onNavigate={onNavigate} /> : undefined}
        subtitle={
          <div className="flex flex-wrap items-center gap-3 text-sm text-secondary">
            {image.date ? <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="date"><span>{formatDate(image.date)}</span></FieldProvenanceHover> : null}
            {image.studioName && image.studioId ? (
              canReadStudios ? (
                <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="studio">
                  <button onClick={() => onNavigate({ page: "studio", id: image.studioId })} className="text-accent hover:underline">
                    {image.studioName}
                  </button>
                </FieldProvenanceHover>
              ) : (
                <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="studio"><span>{image.studioName}</span></FieldProvenanceHover>
              )
            ) : null}
            {image.photographer ? <FieldProvenanceHover fieldProvenance={image.fieldProvenance} fieldKey="photographer"><span>Photo: {image.photographer}</span></FieldProvenanceHover> : null}
          </div>
        }
        backLabel={backLabel}
        onGoBack={goBack}
        media={
          <div className="relative flex h-full min-h-[40vh] flex-1 items-center justify-center bg-black/90 group">
            {imageLoadFailed ? (
              <div className="flex w-full flex-col items-center justify-center gap-3 px-6 text-center text-secondary">
                <ImageOff className="h-10 w-10 text-muted" />
                <div>
                  <div className="text-sm font-medium text-foreground">Image file unavailable</div>
                  {image.files[0]?.path ? <div className="mt-2 max-w-xl break-all text-xs text-muted">{image.files[0].path}</div> : null}
                </div>
              </div>
            ) : null}
            <img
              src={images.imageUrl(id)}
              alt={displayTitle}
              className={["h-full max-h-full w-full select-none object-contain", imageLoadFailed ? "hidden" : "cursor-zoom-in"].join(" ")}
              onError={() => setImageLoadFailed(true)}
              onLoad={(event) => setImageLoadFailed(event.currentTarget.naturalWidth === 0)}
              onClick={openLightbox}
            />
            {!imageLoadFailed ? (
              <button
                type="button"
                onClick={(event) => { event.stopPropagation(); openLightbox(); }}
                className="absolute top-3 right-3 rounded bg-black/60 p-2 text-white opacity-0 transition-opacity group-hover:opacity-100 hover:bg-black/80"
                title="View fullscreen (F)"
              >
                <Maximize className="h-5 w-5" />
              </button>
            ) : null}
          </div>
        }
        mediaAspectRatio="auto"
        tabs={tabs}
        activeTab={activeTab}
        onTabChange={(key) => setActiveTab(key as ImageTab)}
        engagement={{
          primaryContent: (
            <div className="flex flex-wrap items-center gap-3">
              <InteractiveRating value={imageRating} onChange={(value) => setImageRating(value)} readOnly={!canEngageImage} />
            </div>
          ),
          favorite: imageFavorite,
          favoritePending: imageFavoritePending,
          onFavoriteChange: canEngageImage ? setImageFavorite : undefined,
          additionalMetrics: [
            {
              label: "Likes",
              value: imageLikeCount,
              icon: <ThumbsUp className={["h-4 w-4", imageLikeCount > 0 ? "fill-accent text-accent" : ""].join(" ")} />,
              title: "Likes",
              onClick: canWriteImage ? () => incrementLikeMut.mutate() : undefined,
              active: imageLikeCount > 0,
            },
            {
              label: "Page Visits",
              value: imagePageVisitCount,
              icon: <Eye className="h-4 w-4" />,
              title: "Page visits",
            },
          ],
        }}
        actions={
          <>
            <ExtensionSlot slot="image-detail-actions" context={{ image, onNavigate }} />
            {canWriteImage ? (
              <button
                type="button"
                onClick={() => updateMut.mutate({ organized: !image.organized })}
                className={`inline-flex items-center justify-center rounded p-1 transition ${image.organized ? "bg-green-600 text-white" : "text-secondary hover:bg-card hover:text-foreground"}`}
                title={image.organized ? "Organized" : "Mark organized"}
              >
                <Check className="h-4 w-4" />
              </button>
            ) : null}
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
                  <ExtensionEntityActions entityType="image" entityId={image.id} renderMode="menu" onInvoked={() => setShowOpsMenu(false)} />
                  {canWriteImage ? (
                    <button
                      type="button"
                      onClick={() => { setShowScrapeDialog(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Search className="h-3.5 w-3.5" /> Scrape...
                    </button>
                  ) : null}
                  {imageCoverTargets.length > 0 ? (
                    <button
                      type="button"
                      onClick={() => { setShowCoverTargetDialog(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <ImageIcon className="h-3.5 w-3.5" /> Set Cover...
                    </button>
                  ) : null}
                  {image.files.length > 0 && canLibraryScan ? (
                    <button
                      type="button"
                      onClick={() => { rescanMut.mutate(); setShowOpsMenu(false); }}
                      disabled={rescanMut.isPending}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                    >
                      <RefreshCw className={["h-3.5 w-3.5", rescanMut.isPending ? "animate-spin" : ""].join(" ")} /> Rescan
                    </button>
                  ) : null}
                  {canGenerateImage ? (
                    <button
                      type="button"
                      onClick={() => { setShowGenerate(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Clapperboard className="h-3.5 w-3.5" /> Generate…
                    </button>
                  ) : null}
                  {image.files.length === 0 && canDownloadImage ? (
                    <button
                      type="button"
                      onClick={() => { setShowDownloadDialog(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface"
                    >
                      <Download className="h-3.5 w-3.5" /> Download Media...
                    </button>
                  ) : null}
                  {canDeleteImage ? <div className="my-1 border-t border-border" /> : null}
                  {canDeleteImage ? (
                    <button
                      type="button"
                      onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-400 transition-colors hover:bg-surface"
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </button>
                  ) : null}
              </FloatingActionMenu>
            </div>
          </>
        }
        keyboardShortcuts={imageKeyboardShortcuts}
      >
        <MediaDetailLayout.Content>
          {activeContent}
          <ExtensionSlot slot="image-detail-main-bottom" context={{ image, onNavigate }} />
        </MediaDetailLayout.Content>
      </MediaDetailLayout>
    </>
  );
}

function ImageCoverTargetActions({ targets, pending, error, onSelect }: { targets: ImageCoverTarget[]; pending: boolean; error: Error | null; onSelect: (target: ImageCoverTarget) => void }) {
  return (
    <div className="space-y-2">
      <div className="max-h-[32vh] space-y-2 overflow-y-auto pr-1">
        {targets.map((target) => (
          <button
            key={target.key}
            type="button"
            onClick={() => onSelect(target)}
            disabled={pending}
            className="flex w-full items-center gap-3 rounded-lg border border-border bg-card px-3 py-2 text-left transition-colors hover:border-accent disabled:opacity-60"
          >
            <ImageIcon className="h-4 w-4 shrink-0 text-accent" />
            <span className="min-w-0 flex-1">
              <span className="block truncate text-sm font-medium text-foreground">{target.label}</span>
              <span className="block text-xs text-secondary">{target.subtitle}</span>
            </span>
          </button>
        ))}
      </div>
      {error ? <p className="text-sm text-red-300">{error.message}</p> : null}
    </div>
  );
}

function formatImageFaceSummary(face: FaceHostFace) {
  const confidence = face.topConfidence != null ? `${Math.round(face.topConfidence <= 1 ? face.topConfidence * 100 : face.topConfidence)}%` : null;
  return confidence || "AI face";
}

function DetailField({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-border bg-surface/40 px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-1 text-sm text-foreground">{value}</div>
    </div>
  );
}

function EmptyPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="mt-4 flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      <div className="mb-3 opacity-60 text-muted">{icon}</div>
      <p>{message}</p>
    </div>
  );
}
