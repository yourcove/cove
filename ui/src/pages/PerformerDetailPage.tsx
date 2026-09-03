import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { audios, faces, galleries, groups, images, performers, videos, texts, entityImages } from "../api/client";
import type { Audio, AudioFilterCriteria, Face, FaceSimilar, FieldProvenance, FindFilter, Gallery, GalleryFilterCriteria, Group, GroupFilterCriteria, Image, ImageFilterCriteria, Performer as PerformerModel, PerformerFilterCriteria, Video, VideoFilterCriteria, TextDocument, TextFilterCriteria } from "../api/types";
import { formatDate, formatDuration, getResolutionLabel, TagBadge, CustomFieldsDisplay, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { Calendar, FileText, Film, FolderOpen, GitMerge, Headphones, Heart, ImageIcon, Layers, Loader2, MapPin, MoreVertical, Music, Pencil, Ruler, Scale, Search, Sparkles, ThumbsUp, Trash2, Users, UserRound } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { PerformerEditModal } from "./PerformerEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { NarrativeText } from "../components/NarrativeText";
import { DetailMergeDialog } from "../components/DetailMergeDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { InteractiveRating } from "../components/Rating";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { EntityHeroLayout, HERO_ACTION_BUTTON_CLASS, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { ContextualImageListView, ContextualVideoListView } from "../components/ContextualMediaListViews";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { AUDIO_CRITERIA, GALLERY_CRITERIA, GROUP_CRITERIA, IMAGE_CRITERIA, VIDEO_CRITERIA, TEXT_CRITERIA } from "../components/filterCriteriaCatalogs";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { TEXT_SORT_OPTIONS } from "../components/textSortOptions";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";
import { PerformerMetadataTaggerDialog } from "../components/MetadataTaggerDialog";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useKeySequence } from "../hooks/useKeySequence";
import { useDetailListSelection } from "../hooks/useDetailListSelection";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission, hasAnyPermission } from "../auth/visibility";
import { withRequiredMultiId } from "../utils/detailRelationFilters";
import { useAppConfig } from "../state/AppConfigContext";
import { useDetailTabUrlState, useRelatedDetailListUrlState } from "../hooks/useDetailListUrlState";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { sortSeededRandom } from "../utils/seededRandomSort";
import { PerformerExternalLinks } from "../components/PerformerExternalLinks";
import { getPerformerAge, getUtcToday, hasDeathOccurred } from "../utils/performerAge";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "videos" | "galleries" | "images" | "audios" | "texts" | "groups" | "faces" | "appearsWith" | "similar" | (string & {});

const GALLERY_SORT = GALLERY_SORT_OPTIONS;
const IMAGE_SORT = IMAGE_SORT_OPTIONS;
const GROUP_SORT = GROUP_SORT_OPTIONS;
const AUDIO_SORT = AUDIO_SORT_OPTIONS;
const TEXT_SORT = TEXT_SORT_OPTIONS;

export function PerformerDetailPage({ id, onNavigate }: Props) {
  const { config } = useAppConfig();
  const { hasPermission, user } = useAuth();
  const { data: performer, isLoading, error: performerError, refetch: retryPerformer } = useQuery({
    queryKey: ["performer", id],
    queryFn: () => performers.get(id),
  });
  const performerLoadError = getLoadError(performer, performerError);
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [scrapeOpen, setScrapeOpen] = useState(false);
  const { activeTab, setActiveTab } = useDetailTabUrlState<TabKey>("videos");
  const { allTabs: performerTabs, renderExtensionTab, extensionCounts } = useExtensionTabs("performer", [
    { key: "videos", label: "Videos", count: performer?.videoCount },
    { key: "galleries", label: "Galleries", count: performer?.galleryCount },
    { key: "images", label: "Images", count: performer?.imageCount },
    { key: "audios", label: "Audios", count: performer?.audioCount },
    { key: "texts", label: "Texts", count: performer?.textCount },
    { key: "groups", label: "Groups", count: performer?.groupCount },
    { key: "faces", label: "Faces", count: performer?.faceCount },
    { key: "appearsWith", label: "Appears With" },
    { key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> },
  ], id);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "performers" }, onNavigate);
  const canWritePerformer = canWriteEntity("performer", hasPermission);
  const canEngagePerformer = canReadEntity("performer", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canDeletePerformer = canDeleteEntity("performer", hasPermission);
  const canReadFaces = canReadEntity("face", hasPermission);
  const canReadPerformerVideos = canReadEntity("video", hasPermission);
  const canReadPerformerGalleries = canReadEntity("gallery", hasPermission);
  const canReadPerformerImages = canReadEntity("image", hasPermission);
  const canReadPerformerAudios = canReadEntity("audio", hasPermission);
  const canReadPerformerTexts = canReadEntity("text", hasPermission);
  const canReadPerformerGroups = canReadEntity("group", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canScrapePerformer = hasAnyPermission(hasPermission, ["performers.scrape", "performers.write"]);
  const showPerformerOpsMenu = canWritePerformer || canScrapePerformer || canDeletePerformer;
  const visiblePerformerTabs = filterItemsByPermission(performerTabs, {
    videos: "videos.read",
    galleries: "galleries.read",
    images: "images.read",
    audios: "audios.read",
    texts: "texts.read",
    groups: "groups.read",
    faces: "faces.read",
    appearsWith: "performers.read",
    similar: "performers.read",
  }, hasPermission);

  const deleteMut = useMutation({
    mutationFn: () => performers.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["performers"] });
      goBack();
    },
  });

  const {
    favorite: performerFavorite,
    rating: performerRating,
    setFavorite: setPerformerFavorite,
    setRating: setPerformerRating,
  } = useEntityEngagement("performer", id, {
    fallbackFavorite: performer?.favorite,
    fallbackRating: undefined,
  });

  useDocumentTitle(performer?.name);

  useKeySequence(useMemo(() => [
    { id: "detail.edit", keys: "e", surface: "detail" as const, action: () => { if (canWritePerformer) setEditing((value) => !value); } },
    { id: "detail.favorite", keys: "o", surface: "detail" as const, action: () => { if (performer && canEngagePerformer) setPerformerFavorite(!performerFavorite); } },
    { id: "detail.performer.videos", keys: "c", surface: "detail" as const, action: () => { if (canReadPerformerVideos) setActiveTab("videos"); } },
    { id: "detail.performer.galleries", keys: "g", surface: "detail" as const, action: () => { if (canReadPerformerGalleries) setActiveTab("galleries"); } },
  ], [canEngagePerformer, canReadPerformerGalleries, canReadPerformerVideos, canWritePerformer, performer, performerFavorite, setPerformerFavorite]));

  useEffect(() => {
    if (!showOpsMenu) return;
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) setShowOpsMenu(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  useEffect(() => {
    if (visiblePerformerTabs.length > 0 && !visiblePerformerTabs.some((tab) => tab.key === activeTab)) {
      setActiveTab(visiblePerformerTabs[0].key as TabKey);
    }
  }, [activeTab, visiblePerformerTabs]);

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (isApiNotFoundError(performerLoadError)) {
    return <div className="py-16 text-center text-secondary">Performer not found</div>;
  }

  if (performerLoadError) {
    return <ListLoadError error={performerLoadError} onRetry={() => { void retryPerformer(); }} title="Could not load performer" className="mx-0 mt-0" />;
  }

  if (!performer) {
    return <div className="py-16 text-center text-secondary">Performer not found</div>;
  }

  const today = getUtcToday();
  const age = getPerformerAge(performer.birthdate, performer.deathDate, today);
  const deceased = hasDeathOccurred(performer.deathDate, today);
  const performerImageUrl = performer.imagePath || entityImages.performerImageUrl(performer.id, performer.updatedAt, 1200);
  const handleCoverChanged = () => {
    queryClient.invalidateQueries({ queryKey: ["performer", performer.id] });
    queryClient.invalidateQueries({ queryKey: ["performers"] });
  };

  return (
    <>
      <CoverImageDialog
        open={coverOpen}
        title="Set Performer Cover"
        entityType="performer"
        entityId={performer.id}
        currentImageUrl={performerImageUrl}
        onUpload={(file) => entityImages.uploadPerformerImage(performer.id, file)}
        onDelete={() => entityImages.deletePerformerImage(performer.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={handleCoverChanged}
        aspectRatio="2/3"
        deleteLabel="Remove Image"
      />
      <EntityHeroLayout
        entityType="performer"
        entityId={performer.id}
        backLabel={backLabel}
        onGoBack={goBack}
        backgroundImageUrl={entityImages.performerImageUrl(performer.id, performer.updatedAt, 1600)}
        imageUrl={performerImageUrl}
        imageAlt={performer.name}
        imageContainerClassName="relative flex h-96 w-72 max-w-full flex-shrink-0 items-center justify-center overflow-hidden rounded-xl border border-border bg-card shadow-xl shadow-black/35 md:h-[34rem] md:w-[25rem]"
        onImageClick={canWritePerformer ? () => setCoverOpen(true) : undefined}
        imageFallbackClassName="flex h-full w-full items-center justify-center bg-gradient-to-b from-card to-surface"
        imageFallback={<UserRound className="h-20 w-20 text-muted/50" />}
        title={<FieldProvenanceHover fieldProvenance={performer.fieldProvenance} fieldKey="name">{performer.name}</FieldProvenanceHover>}
        subtitle={performer.disambiguation ? <FieldProvenanceHover fieldProvenance={performer.fieldProvenance} fieldKey="disambiguation">{performer.disambiguation}</FieldProvenanceHover> : undefined}
        aliases={performer.aliases.length > 0 ? <FieldProvenanceHover fieldProvenance={performer.fieldProvenance} fieldKey="aliases">{performer.aliases.join(", ")}</FieldProvenanceHover> : undefined}
        favorite={performerFavorite}
        onFavoriteToggle={canEngagePerformer ? () => setPerformerFavorite(!performerFavorite) : undefined}
        counts={[
          { key: "likes", label: "Likes", value: performer.likeCount ?? 0, icon: <ThumbsUp className={`h-4 w-4 ${(performer.likeCount ?? 0) > 0 ? "fill-accent text-accent" : ""}`} /> },
          { key: "videos", label: "Videos", value: performer.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "galleries", label: "Galleries", value: performer.galleryCount, icon: <FolderOpen className="h-4 w-4" /> },
          { key: "images", label: "Images", value: performer.imageCount, icon: <ImageIcon className="h-4 w-4" /> },
          { key: "audios", label: "Audios", value: performer.audioCount, icon: <Headphones className="h-4 w-4" /> },
          { key: "texts", label: "Texts", value: performer.textCount, icon: <FileText className="h-4 w-4" /> },
          { key: "groups", label: "Groups", value: performer.groupCount, icon: <Layers className="h-4 w-4" /> },
          ...extensionCounts.map((ec) => ({
            key: ec.key,
            label: ec.label,
            value: ec.count,
            icon: ec.icon === "music" ? <Music className="h-4 w-4" /> : <Layers className="h-4 w-4" />,
          })),
        ]}
        heroContent={(
          <>
            <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
              <div className="shrink-0">
                <InteractiveRating value={performerRating} onChange={(value) => setPerformerRating(value)} readOnly={!canEngagePerformer} />
              </div>
              <AspectRatingsPanel hostType="performer" hostId={id} canRate={canEngagePerformer} showHeading={false} variant="inline" className="min-w-0" />
            </div>

            <div className="mt-4 grid grid-cols-2 gap-3 md:grid-cols-4">
              {performer.gender && <InfoItem icon={<UserRound className="h-4 w-4" />} label="Gender" value={performer.gender} fieldProvenance={performer.fieldProvenance} fieldKey="gender" />}
              {performer.birthdate && (
                <InfoItem icon={<Calendar className="h-4 w-4" />} label="Born" value={`${formatDate(performer.birthdate)}${!deceased && age != null ? ` (${age})` : ""}`} fieldProvenance={performer.fieldProvenance} fieldKey="birthdate" />
              )}
              {performer.deathDate && <InfoItem icon={<Calendar className="h-4 w-4" />} label="Died" value={`${formatDate(performer.deathDate)}${deceased && age != null ? ` (age ${age})` : ""}`} fieldProvenance={performer.fieldProvenance} fieldKey="deathDate" />}
              {performer.country && <InfoItem icon={<MapPin className="h-4 w-4" />} label="Country" value={performer.country} fieldProvenance={performer.fieldProvenance} fieldKey="country" />}
              {performer.ethnicity && <InfoItem label="Ethnicity" value={performer.ethnicity} fieldProvenance={performer.fieldProvenance} fieldKey="ethnicity" />}
              {performer.heightCm && <InfoItem icon={<Ruler className="h-4 w-4" />} label="Height" value={`${performer.heightCm} cm`} fieldProvenance={performer.fieldProvenance} fieldKey="height_cm" />}
              {performer.weight && <InfoItem icon={<Scale className="h-4 w-4" />} label="Weight" value={`${performer.weight} kg`} fieldProvenance={performer.fieldProvenance} fieldKey="weight" />}
              {performer.measurements && <InfoItem label="Measurements" value={performer.measurements} fieldProvenance={performer.fieldProvenance} fieldKey="measurements" />}
              {performer.eyeColor && <InfoItem label="Eye Color" value={performer.eyeColor} fieldProvenance={performer.fieldProvenance} fieldKey="eye_color" />}
              {performer.hairColor && <InfoItem label="Hair Color" value={performer.hairColor} fieldProvenance={performer.fieldProvenance} fieldKey="hair_color" />}
              {performer.fakeTits && <InfoItem label="Fake Tits" value={performer.fakeTits} fieldProvenance={performer.fieldProvenance} fieldKey="fake_tits" />}
              {performer.penisLength != null && <InfoItem label="Penis Length" value={`${performer.penisLength} cm`} fieldProvenance={performer.fieldProvenance} fieldKey="penis_length" />}
              {performer.circumcised && <InfoItem label="Circumcised" value={performer.circumcised} fieldProvenance={performer.fieldProvenance} fieldKey="circumcised" />}
              {performer.tattoos && <InfoItem label="Tattoos" value={performer.tattoos} fieldProvenance={performer.fieldProvenance} fieldKey="tattoos" />}
              {performer.piercings && <InfoItem label="Piercings" value={performer.piercings} fieldProvenance={performer.fieldProvenance} fieldKey="piercings" />}
              {performer.careerStart && <InfoItem label="Career" value={`${performer.careerStart}${performer.careerEnd ? ` – ${performer.careerEnd}` : " – present"}`} fieldProvenance={performer.fieldProvenance} fieldKey={["career_start", "careerStart"]} />}
            </div>

            {(performer.urls.length > 0 || performer.remoteIds.length > 0) ? (
              <FieldProvenanceHover fieldProvenance={performer.fieldProvenance} fieldKey="urls" block className="mt-4 space-y-2">
                <PerformerExternalLinks
                  key={id}
                  remoteIds={performer.remoteIds}
                  urls={performer.urls}
                  metadataServers={config?.scraping?.metadataServers}
                />
              </FieldProvenanceHover>
            ) : null}

            {canReadTags && performer.tags.length > 0 ? (
              <div className="mt-4 flex flex-wrap gap-1.5">
                {performer.tags.map((tag) => (
                  <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, performer.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                ))}
              </div>
            ) : null}

            {performer.details ? <FieldProvenanceHover fieldProvenance={performer.fieldProvenance} fieldKey="details" block><NarrativeText className="mt-4 max-w-4xl text-sm leading-6 text-secondary">{performer.details}</NarrativeText></FieldProvenanceHover> : null}
            <CustomFieldsDisplay customFields={performer.customFields} entityType="performer" />
          </>
        )}
        actions={(
          <>
            <ExtensionSlot slot="performer-detail-actions" context={{ performer, onNavigate }} />
            {canWritePerformer ? (
              <button
                type="button"
                onClick={() => setEditing(true)}
                className={HERO_PRIMARY_ACTION_BUTTON_CLASS}
                title="Edit"
              >
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
            ) : null}
            {showPerformerOpsMenu ? (
              <div className="relative" ref={opsMenuRef}>
                <button onClick={() => setShowOpsMenu(!showOpsMenu)} className={`${HERO_ACTION_BUTTON_CLASS} text-secondary`} title="Actions">
                  <MoreVertical className="h-4 w-4" />
                </button>
                <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[160px] py-1">
                    {canScrapePerformer ? <button onClick={() => { setScrapeOpen(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface"><Search className="h-3.5 w-3.5" /> Scrape / Metadata...</button> : null}
                    {canWritePerformer ? <button onClick={() => { setMergeOpen(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface"><GitMerge className="h-3.5 w-3.5" /> Merge...</button> : null}
                    {canDeletePerformer ? <div className="my-1 border-t border-border" /> : null}
                    {canDeletePerformer ? <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-red-400 hover:bg-surface"><Trash2 className="h-3.5 w-3.5" /> Delete</button> : null}
                </FloatingActionMenu>
              </div>
            ) : null}
          </>
        )}
      >
        <EntityDetailTabs tabs={visiblePerformerTabs} activeTab={activeTab} onTabChange={(key) => setActiveTab(key as TabKey)} className="mx-auto max-w-7xl mt-0" />

        <div className="py-6">
          {activeTab === "videos" && (
            <PerformerVideosPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "galleries" && (
            <PerformerGalleriesPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "images" && (
            <PerformerImagesPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "audios" && (
            <PerformerAudiosPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "texts" && (
            <PerformerTextsPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "groups" && (
            <PerformerGroupsPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "faces" && (
            <PerformerFacesPanel performerId={id} canReadFaces={canReadFaces} onNavigate={onNavigate} />
          )}
          {activeTab === "appearsWith" && (
            <PerformerAppearsWithPanel performerId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "similar" && (
            <PerformerSimilarPanel performer={performer} canReadFaces={canReadFaces} onNavigate={onNavigate} />
          )}
          {renderExtensionTab(activeTab, id, onNavigate)}
        </div>

        <ExtensionSlot slot="performer-detail-bottom" context={{ performer, onNavigate }} />
      </EntityHeroLayout>

      <PerformerEditModal performer={performer} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Performer"
        message={`Are you sure you want to delete "${performer.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />
      <DetailMergeDialog
        open={mergeOpen}
        onClose={() => setMergeOpen(false)}
        entityType="performer"
        targetItem={{ id: performer.id, name: performer.name, imagePath: performer.imagePath || entityImages.performerImageUrl(performer.id, performer.updatedAt), subtitle: performer.disambiguation }}
        searchItems={async (term) => {
          const response = await performers.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: term || undefined });
          return response.items.map((item) => ({
            id: item.id,
            name: item.name,
            imagePath: item.imagePath,
            subtitle: item.disambiguation,
          }));
        }}
        onMerge={(targetId, sourceIds) => performers.merge(targetId, sourceIds)}
        invalidateQueryKeys={[["performer", id], ["performers"]]}
      />

      <PerformerMetadataTaggerDialog open={scrapeOpen} onClose={() => setScrapeOpen(false)} performer={performer} onNavigate={onNavigate} />
    </>
  );
}

type PerformerFaceMatch = {
  performerId: number;
  performerName: string;
  coverImageUrl?: string;
  bestDistance: number;
  bestFaceId: number;
  bestFaceLabel?: string;
  matchingFaceIds: number[];
};

type PerformerAttributeMatch = {
  performer: PerformerModel;
  reasons: string[];
};

function PerformerSimilarPanel({ performer, canReadFaces, onNavigate }: { performer: PerformerModel; canReadFaces: boolean; onNavigate: (r: any) => void }) {
  return (
    <div className="space-y-6">
      <PerformerFaceSimilarityPanel performerId={performer.id} canReadFaces={canReadFaces} onNavigate={onNavigate} />
      <PerformerAttributeSimilarityPanel performer={performer} onNavigate={onNavigate} />
    </div>
  );
}

function PerformerAttributeSimilarityPanel({ performer, onNavigate }: { performer: PerformerModel; onNavigate: (r: any) => void }) {
  const attributeQueries = useMemo(() => buildPerformerAttributeQueries(performer), [performer]);
  const queryResults = useQueries({
    queries: attributeQueries.map((query) => ({
      queryKey: ["performer", performer.id, "similar-attribute", query.key, query.value],
      queryFn: () => performers.findFiltered({
        findFilter: { page: 1, perPage: 18, sort: "latest_video_date", direction: "desc" },
        objectFilter: query.objectFilter,
      }),
      enabled: attributeQueries.length > 0,
    })),
  });

  const matches = useMemo<PerformerAttributeMatch[]>(() => {
    const byPerformer = new Map<number, PerformerAttributeMatch & { reasonSet: Set<string> }>();

    queryResults.forEach((result, index) => {
      const label = attributeQueries[index]?.label;
      if (!label) return;

      for (const candidate of result.data?.items ?? []) {
        if (candidate.id === performer.id) continue;

        const existing = byPerformer.get(candidate.id);
        if (existing) {
          existing.reasonSet.add(label);
          existing.reasons = Array.from(existing.reasonSet);
          continue;
        }

        byPerformer.set(candidate.id, {
          performer: candidate,
          reasons: [label],
          reasonSet: new Set([label]),
        });
      }
    });

    return Array.from(byPerformer.values())
      .map(({ reasonSet: _reasonSet, ...match }) => match)
      .sort((left, right) => right.reasons.length - left.reasons.length || right.performer.videoCount - left.performer.videoCount || left.performer.name.localeCompare(right.performer.name))
      .slice(0, 12);
  }, [attributeQueries, performer.id, queryResults]);

  const isLoading = queryResults.some((result) => result.isLoading);

  return (
    <section className="rounded-xl border border-border bg-card p-4">
      <div>
        <h2 className="text-base font-semibold text-foreground">Similar Performers by Attributes</h2>
        <p className="mt-1 text-sm text-secondary">Matches are grouped from shared ethnicity, hair color, measurements, height, country, and eye color.</p>
      </div>

      {attributeQueries.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
          This performer does not have enough profile attributes for comparison yet.
        </div>
      ) : isLoading ? (
        <p className="mt-4 text-sm text-secondary">Finding similar performers...</p>
      ) : matches.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
          No attribute-similar performers were found yet.
        </div>
      ) : (
        <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {matches.map((match) => (
            <SimilarPerformerAttributeCard key={match.performer.id} match={match} onNavigate={onNavigate} />
          ))}
        </div>
      )}
    </section>
  );
}

function SimilarPerformerAttributeCard({ match, onNavigate }: { match: PerformerAttributeMatch; onNavigate: (r: any) => void }) {
  const imageUrl = match.performer.imagePath || entityImages.performerImageUrl(match.performer.id, match.performer.updatedAt);

  return (
    <button
      type="button"
      onClick={() => onNavigate({ page: "performer", id: match.performer.id })}
      className="flex w-full gap-3 rounded-xl border border-border bg-surface/60 p-3 text-left transition-colors hover:border-accent/60"
    >
      <div className="h-20 w-14 flex-shrink-0 overflow-hidden rounded-lg bg-surface/90">
        {imageUrl ? (
          <img src={imageUrl} alt={match.performer.name} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <UserRound className="h-6 w-6" />
          </div>
        )}
      </div>
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-semibold text-foreground">{match.performer.name}</div>
        {match.performer.country ? <div className="mt-1 text-xs text-secondary">{match.performer.country}</div> : null}
        <div className="mt-2 flex flex-wrap gap-1">
          {match.reasons.map((reason) => (
            <span key={reason} className="rounded-full border border-border px-2 py-0.5 text-[11px] text-muted">
              {reason}
            </span>
          ))}
        </div>
      </div>
    </button>
  );
}

function buildPerformerAttributeQueries(performer: PerformerModel) {
  const queries: { key: string; label: string; value: string | number; objectFilter: PerformerFilterCriteria }[] = [];
  const addString = (key: string, label: string, value: string | undefined | null, criterionKey: keyof PerformerFilterCriteria) => {
    const trimmed = value?.trim();
    if (!trimmed) return;
    queries.push({
      key,
      label,
      value: trimmed,
      objectFilter: { [criterionKey]: { modifier: "EQUALS", value: trimmed } } as PerformerFilterCriteria,
    });
  };

  addString("ethnicity", "Ethnicity", performer.ethnicity, "ethnicityCriterion");
  addString("hairColor", "Hair", performer.hairColor, "hairColorCriterion");
  addString("measurements", "Measurements", performer.measurements, "measurementsCriterion");
  addString("country", "Country", performer.country, "countryCriterion");
  addString("eyeColor", "Eyes", performer.eyeColor, "eyeColorCriterion");

  if (performer.heightCm && performer.heightCm > 0) {
    queries.push({
      key: "height",
      label: "Height",
      value: performer.heightCm,
      objectFilter: {
        heightCriterion: {
          modifier: "BETWEEN",
          value: Math.max(1, performer.heightCm - 3),
          value2: performer.heightCm + 3,
        },
      },
    });
  }

  return queries;
}

function PerformerFaceSimilarityPanel({ performerId, canReadFaces, onNavigate }: { performerId: number; canReadFaces: boolean; onNavigate: (r: any) => void }) {
  const { data: linkedFacesData, isLoading: linkedFacesLoading, error: linkedFacesError, refetch: retryLinkedFaces } = useQuery({
    queryKey: ["performer", performerId, "linked-faces"],
    queryFn: () => faces.performerFaces(performerId),
    enabled: canReadFaces,
  });
  const linkedFacesLoadError = getLoadError(linkedFacesData, linkedFacesError);
  const linkedFaces = linkedFacesData ?? [];
  const similaritySourceFaces = linkedFaces.slice(0, 6);
  const visibleLinkedFaces = linkedFaces.slice(0, 12);
  const similarFaceQueries = useQueries({
    queries: similaritySourceFaces.map((face) => ({
      queryKey: ["performer", performerId, "linked-face", face.id, "similar"],
      queryFn: () => faces.similar(face.id, { k: 12 }),
      enabled: canReadFaces,
    })),
  });

  const similarPerformers = useMemo<PerformerFaceMatch[]>(() => {
    const matches = new Map<number, PerformerFaceMatch & { matchingFaceIdSet: Set<number> }>();

    for (const query of similarFaceQueries) {
      const candidates = query.data?.items ?? [];
      for (const candidate of candidates) {
        if (candidate.performerId == null || candidate.performerId === performerId) {
          continue;
        }

        const existing = matches.get(candidate.performerId);
        if (existing) {
          existing.matchingFaceIdSet.add(candidate.id);
          existing.matchingFaceIds = Array.from(existing.matchingFaceIdSet);
          if (candidate.distance < existing.bestDistance) {
            existing.bestDistance = candidate.distance;
            existing.bestFaceId = candidate.id;
            existing.bestFaceLabel = candidate.label;
            if (candidate.coverImageUrl) {
              existing.coverImageUrl = candidate.coverImageUrl;
            }
          }
          continue;
        }

        matches.set(candidate.performerId, {
          performerId: candidate.performerId,
          performerName: candidate.performerName || `Performer #${candidate.performerId}`,
          coverImageUrl: candidate.coverImageUrl,
          bestDistance: candidate.distance,
          bestFaceId: candidate.id,
          bestFaceLabel: candidate.label,
          matchingFaceIds: [candidate.id],
          matchingFaceIdSet: new Set([candidate.id]),
        });
      }
    }

    return Array.from(matches.values())
      .map(({ matchingFaceIdSet: _matchingFaceIdSet, ...candidate }) => candidate)
      .sort((left, right) => left.bestDistance - right.bestDistance || right.matchingFaceIds.length - left.matchingFaceIds.length)
      .slice(0, 8);
  }, [performerId, similarFaceQueries]);

  if (!canReadFaces) {
    return null;
  }

  const similarFacesLoading = similarFaceQueries.some((query) => query.isLoading);
  const similarFacesLoadError = similarFaceQueries
    .map((query) => getLoadError(query.data, query.error))
    .find((error) => error != null) ?? null;
  const retrySimilarFaces = () => {
    void retryLinkedFaces();
    similarFaceQueries.forEach((query) => { if (query.isError) void query.refetch(); });
  };

  return (
    <div className="mt-6 rounded-xl border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-base font-semibold text-foreground">Similar Performers by Face</h2>
          <p className="mt-1 text-sm text-secondary">Visual matches derived from linked face embeddings.</p>
        </div>
        <div className="rounded-full border border-border bg-surface px-3 py-1 text-xs text-muted">
          {linkedFacesLoadError ? "Unavailable" : `${linkedFaces.length} linked face${linkedFaces.length === 1 ? "" : "s"}`}
        </div>
      </div>

      {linkedFacesLoading ? (
        <p className="mt-4 text-sm text-secondary">Loading linked faces...</p>
      ) : linkedFacesLoadError ? (
        <ListLoadError error={linkedFacesLoadError} onRetry={retrySimilarFaces} className="mt-4" />
      ) : linkedFaces.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
          This performer does not have any primary face clusters linked yet.
        </div>
      ) : (
        <>
          <div className="mt-4 flex flex-wrap gap-2">
            {visibleLinkedFaces.map((face) => (
              <button
                key={face.id}
                type="button"
                onClick={() => onNavigate({ page: "face", id: face.id })}
                className="rounded-full border border-border bg-surface px-3 py-1.5 text-xs text-foreground transition-colors hover:border-accent"
              >
                {face.label?.trim() || `Face #${face.id}`}
              </button>
            ))}
            {linkedFaces.length > visibleLinkedFaces.length ? (
              <span className="rounded-full border border-border bg-surface px-3 py-1.5 text-xs text-muted">
                +{linkedFaces.length - visibleLinkedFaces.length} more
              </span>
            ) : null}
          </div>

          {similarFacesLoading ? (
            <p className="mt-4 text-sm text-secondary">Finding visually similar performers...</p>
          ) : similarFacesLoadError ? (
            <ListLoadError error={similarFacesLoadError} onRetry={retrySimilarFaces} className="mt-4" />
          ) : similarPerformers.length === 0 ? (
            <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
              No similar performers were found for the linked faces yet.
            </div>
          ) : (
            <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {similarPerformers.map((match) => (
                <SimilarPerformerFaceCard key={match.performerId} match={match} onNavigate={onNavigate} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function SimilarPerformerFaceCard({ match, onNavigate }: { match: PerformerFaceMatch; onNavigate: (r: any) => void }) {
  return (
    <article className="overflow-hidden rounded-2xl border border-border bg-surface/60">
      <button
        type="button"
        onClick={() => onNavigate({ page: "performer", id: match.performerId })}
        className="flex w-full items-center gap-3 p-4 text-left"
      >
        <div className="h-16 w-16 overflow-hidden rounded-xl bg-surface/90">
          {match.coverImageUrl ? (
            <img src={match.coverImageUrl} alt={match.performerName} className="h-full w-full object-cover" loading="lazy" />
          ) : (
            <div className="flex h-full w-full items-center justify-center text-muted">
              <UserRound className="h-6 w-6" />
            </div>
          )}
        </div>
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold text-foreground">{match.performerName}</div>
          <div className="mt-1 text-xs text-secondary">Best face distance {match.bestDistance.toFixed(3)}</div>
          <div className="mt-1 text-xs text-muted">
            Matched via {match.matchingFaceIds.length} face{match.matchingFaceIds.length === 1 ? "" : "s"}
          </div>
        </div>
      </button>
      <div className="border-t border-border px-4 py-3 text-xs text-secondary">
        <button
          type="button"
          onClick={() => onNavigate({ page: "face", id: match.bestFaceId })}
          className="text-accent hover:underline"
        >
          Open best face match{match.bestFaceLabel ? `: ${match.bestFaceLabel}` : ""}
        </button>
      </div>
    </article>
  );
}

const FACE_SORT_OPTIONS = [
  { value: "appearances", label: "Appearances" },
  { value: "videos", label: "Videos" },
  { value: "images", label: "Images" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "random", label: "Random" },
];

function PerformerFacesPanel({ performerId, canReadFaces, onNavigate }: { performerId: number; canReadFaces: boolean; onNavigate: (r: any) => void }) {
  const [zoomLevel, setZoomLevel] = useState(0);
  // Client-side sort over the (small) linked-face set so this tab gets the same toolbar (sort + zoom +
  // grid/list) as the other detail tabs without needing a separate paginated endpoint.
  const { filter, setFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "faces", resetKey: "performer-faces", entityType: "faces", builtInFilter: { page: 1, perPage: 200, sort: "appearances", direction: "desc" } });
  // Shares the cache key with the similarity panel's linked-faces query so switching tabs is instant.
  const { data: linkedFacesData, isLoading, error, refetch } = useQuery({
    queryKey: ["performer", performerId, "linked-faces"],
    queryFn: () => faces.performerFaces(performerId),
    enabled: canReadFaces,
  });
  const loadError = getLoadError(linkedFacesData, error);
  const linkedFaces = linkedFacesData ?? [];

  const sortedFaces = useMemo(() => {
    const dir = filter.direction === "asc" ? 1 : -1;
    const key = filter.sort ?? "appearances";
    if (filter.sort === "random") {
      return sortSeededRandom(linkedFaces, (face) => String(face.id), filter.seed, filter.direction === "desc");
    }
    return [...linkedFaces].sort((left, right) => {
      switch (key) {
        case "videos": return dir * (left.videoCount - right.videoCount);
        case "images": return dir * (left.imageCount - right.imageCount);
        case "created_at": return dir * (new Date(left.createdAt).getTime() - new Date(right.createdAt).getTime());
        case "updated_at": return dir * (new Date(left.updatedAt).getTime() - new Date(right.updatedAt).getTime());
        default: return dir * (left.appearanceCount - right.appearanceCount);
      }
    });
  }, [linkedFaces, filter.sort, filter.direction, filter.seed]);

  if (!canReadFaces) return null;
  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void refetch(); }} className="mt-3" />;

  return (
    <div className="space-y-4">
      <DetailListToolbar
        filter={filter}
        onFilterChange={setFilter}
        totalCount={linkedFaces.length}
        sortOptions={FACE_SORT_OPTIONS}
        zoomLevel={zoomLevel}
        onZoomChange={setZoomLevel}
        cardSizeEntityType="faces"
        showPagingControls={false}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={availableDisplayModes}
      />
      {isLoading ? (
        <p className="text-sm text-secondary">Loading linked faces...</p>
      ) : linkedFaces.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border px-4 py-6 text-sm text-secondary">
          No faces are linked to this performer yet.
        </div>
      ) : (
        <RelatedEntityListView
          entityType="faces"
          items={sortedFaces}
          displayMode={displayMode}
          zoomLevel={zoomLevel}
          selectedIds={EMPTY_SELECTION}
          selecting={false}
          onToggle={noop}
          onNavigate={onNavigate}
          infinitePageSize={false}
        />
      )}
    </div>
  );
}

const EMPTY_SELECTION = new Set<number>();
const noop = () => {};

function InfoItem({ icon, label, value, fieldProvenance, fieldKey }: { icon?: React.ReactNode; label: string; value: string; fieldProvenance?: FieldProvenance[]; fieldKey?: string | string[] }) {
  const valueNode = fieldKey ? <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey={fieldKey}>{value}</FieldProvenanceHover> : value;

  return (
    <div className="flex items-center gap-2 text-sm">
      {icon && <span className="text-muted">{icon}</span>}
      <div>
        <div className="text-xs text-muted">{label}</div>
        <div className="text-foreground">{valueNode}</div>
      </div>
    </div>
  );
}

function PerformerVideosPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "videos", resetKey: "performer-videos", entityType: "videos", builtInFilter: { page: 1, perPage: 24, direction: "desc" }, defaultFilterKey: "videos" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback((nextFilter: FindFilter) => hasObjectFilter
    ? videos.findFiltered({
        findFilter: nextFilter,
        objectFilter: withRequiredMultiId(objectFilter as VideoFilterCriteria, "performersCriterion", performerId),
      })
    : videos.find(nextFilter, { performerIds: String(performerId) }), [hasObjectFilter, objectFilter, performerId]);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Video>({
    queryKey: ["performer-videos", performerId, objectFilter],
    filter,
    queryFn: queryPage,
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={VIDEO_SORT_OPTIONS} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="videos" selectedIds={selectedIds} onDone={selectNone} videoItems={items} onNavigate={onNavigate} removeFromParent={{ type: "performer", id: performerId }} />} criteriaDefinitions={VIDEO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="videos" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading videos..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Film className="h-12 w-12" />} message="No videos found for this performer" /></>;

  return (
    <>
      {toolbar}
      <ContextualVideoListView items={items} filter={filter} totalCount={data.totalCount} queryPage={queryPage} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onVideoQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
      {quickViewId !== null && (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function PerformerGalleriesPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "galleries", resetKey: "performer-galleries", entityType: "galleries", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "galleries" });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Gallery>({
    queryKey: ["performer-galleries", performerId, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter
      ? galleries.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as GalleryFilterCriteria, "performersCriterion", performerId),
        })
      : galleries.find(nextFilter, { performerIds: String(performerId) }),
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GALLERY_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="galleries" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "performer", id: performerId }} />} criteriaDefinitions={GALLERY_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="galleries" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<FolderOpen className="h-10 w-10" />} message="Loading galleries..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<FolderOpen className="h-12 w-12" />} message="No galleries found for this performer" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="galleries" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
    </>
  );
}

function PerformerImagesPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "images", resetKey: "performer-images", entityType: "images", builtInFilter: { page: 1, perPage: 30, direction: "desc" }, defaultFilterKey: "images" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback((nextFilter: FindFilter) => hasObjectFilter
    ? images.findFiltered({
        findFilter: nextFilter,
        objectFilter: withRequiredMultiId(objectFilter as ImageFilterCriteria, "performersCriterion", performerId),
      })
    : images.find(nextFilter, { performerIds: String(performerId) }), [hasObjectFilter, objectFilter, performerId]);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Image>({
    queryKey: ["performer-images", performerId, objectFilter],
    filter,
    queryFn: queryPage,
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={IMAGE_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="images" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "performer", id: performerId }} />} criteriaDefinitions={IMAGE_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="images" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<ImageIcon className="h-10 w-10" />} message="Loading images..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images found for this performer" /></>;

  return (
    <>
      {toolbar}
      <ContextualImageListView items={items} filter={filter} totalCount={data.totalCount} queryPage={queryPage} interactionSource="performerDetailPage" interactionMeta={{ performerId }} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onImageQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
      {quickViewId !== null && (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function PerformerAudiosPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "audios", resetKey: "performer-audios", entityType: "audios", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "audios" });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Audio>({
    queryKey: ["performer-audios", performerId, objectFilter],
    filter,
    queryFn: (nextFilter) => audios.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as AudioFilterCriteria, "performersCriterion", performerId),
    }),
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={AUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="audios" selectedIds={selectedIds} onDone={selectNone} audioItems={items} downloadItems={items} onNavigate={onNavigate} removeFromParent={{ type: "performer", id: performerId }} />} criteriaDefinitions={AUDIO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="audios" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<Headphones className="h-10 w-10" />} message="Loading audios..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Headphones className="h-12 w-12" />} message="No audios found for this performer" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="audios" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
    </>
  );
}

function PerformerTextsPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "texts", resetKey: "performer-texts", entityType: "texts", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "texts" });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<TextDocument>({
    queryKey: ["performer-texts", performerId, objectFilter],
    filter,
    queryFn: (nextFilter) => texts.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as TextFilterCriteria, "performersCriterion", performerId),
    }),
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={TEXT_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="texts" selectedIds={selectedIds} onDone={selectNone} textItems={items} downloadItems={items} removeFromParent={{ type: "performer", id: performerId }} />} criteriaDefinitions={TEXT_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="texts" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<FileText className="h-10 w-10" />} message="Loading texts..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<FileText className="h-12 w-12" />} message="No texts found for this performer" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="texts" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
    </>
  );
}

function PerformerGroupsPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "groups", resetKey: "performer-groups", entityType: "groups", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "groups" });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Group>({
    queryKey: ["performer-groups", performerId, objectFilter],
    filter,
    queryFn: (nextFilter) => groups.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as GroupFilterCriteria, "performersCriterion", performerId),
    }),
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter }), [infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone } = useMultiSelect(data?.items ?? [], { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const items = data?.items ?? [];
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GROUP_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll} selectAllPending={infinitePageSize ? selectAllMatchingPending : false} onSelectAllMatching={infinitePageSize ? selectAll : undefined} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="groups" selectedIds={selectedIds} onDone={selectNone} />} criteriaDefinitions={GROUP_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="groups" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading groups..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Layers className="h-12 w-12" />} message="No groups for this performer" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="groups" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
    </>
  );
}

function PerformerAppearsWithPanel({ performerId, onNavigate }: {
  performerId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "appearsWith", resetKey: "performer-appears-with", entityType: "performers", builtInFilter: { page: 1, perPage: 18, direction: "asc" } });
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<PerformerModel>({
    queryKey: ["performer-appears-with", performerId, filter],
    filter,
    queryFn: (nextFilter) => performers.appearsWith(performerId, nextFilter),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={[{ value: "co_video_count", label: "Shared Videos" }, { value: "name", label: "Name" }, { value: "random", label: "Random" }]} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="performers" selectedIds={selectedIds} onDone={selectNone} />} allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;
  if (isLoading) return <LoadingPanel icon={<Users className="h-10 w-10" />} message="Loading co-stars..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Users className="h-12 w-12" />} message="No co-stars found" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="performers" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} allowInfinitePageSize />
    </>
  );
}

function LoadingPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-12 text-muted">
      <div className="mb-3 animate-pulse">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function EmptyPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-card/40 py-12 text-muted">
      <div className="mb-3 opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}
