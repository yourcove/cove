import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { audios, galleries, groups, images, performers, videos, studios, texts, entityImages } from "../api/client";
import type { Audio, AudioFilterCriteria, Gallery, GalleryFilterCriteria, Group, GroupFilterCriteria, Image, ImageFilterCriteria, MetadataServer, MetadataServerStudioMatch, Performer, PerformerFilterCriteria, Video, VideoFilterCriteria, Studio, StudioFilterCriteria, TextDocument, TextFilterCriteria } from "../api/types";
import { formatDate, formatDuration, getResolutionLabel, TagBadge, CustomFieldsDisplay, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { ChevronDown, Building2, CloudDownload, CloudUpload, FileText, Film, FolderOpen, GitMerge, Headphones, ImageIcon, Layers, Link as LinkIcon, Loader2, MoreVertical, Music, Pencil, Search, Trash2, UserRound } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { StudioEditModal } from "./StudioEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailMergeDialog } from "../components/DetailMergeDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { InteractiveRating } from "../components/Rating";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { useAppConfig } from "../state/AppConfigContext";
import { DetailListToolbar } from "../components/DetailListToolbar";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { EntityHeroLayout, HERO_ACTION_BUTTON_CLASS, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { StudioMetadataTaggerDialog } from "../components/MetadataTaggerDialog";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { AUDIO_CRITERIA, GALLERY_CRITERIA, GROUP_CRITERIA, IMAGE_CRITERIA, PERFORMER_CRITERIA, VIDEO_CRITERIA, STUDIO_CRITERIA, TEXT_CRITERIA } from "../components/FilterDialog";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useDetailListSelection } from "../hooks/useDetailListSelection";
import { MetadataServerLinks } from "../components/MetadataServerLinks";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission } from "../auth/visibility";
import { withRequiredMultiId, withRequiredSingleId } from "../utils/detailRelationFilters";
import { HierarchyContentToggle } from "../components/HierarchyContentToggle";
import { useDetailBooleanUrlState, useDetailTabUrlState, useRelatedDetailListUrlState } from "../hooks/useDetailListUrlState";

const PERFORMER_SORT = PERFORMER_SORT_OPTIONS;
const IMAGE_SORT = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "title", label: "Title" },
  { value: "rating", label: "Rating" },
  { value: "random", label: "Random" },
];
const GALLERY_SORT = GALLERY_SORT_OPTIONS;
const STUDIO_SORT = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "video_count", label: "Video Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "image_count", label: "Image Count" },
  { value: "child_count", label: "Substudios Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "random", label: "Random" },
];
const GROUP_SORT = [
  { value: "name", label: "Name" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "random", label: "Random" },
];
const AUDIO_SORT = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "title", label: "Title" },
  { value: "date", label: "Date" },
  { value: "duration", label: "Duration" },
];
const TEXT_SORT = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "title", label: "Title" },
  { value: "date", label: "Date" },
  { value: "words", label: "Word Count" },
  { value: "pages", label: "Page Count" },
];

function formatUrlHost(url: string) {
  try {
    return new URL(url).hostname.replace(/^www\./i, "");
  } catch {
    return url;
  }
}

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "videos" | "performers" | "galleries" | "images" | "audios" | "texts" | "studios" | "groups" | (string & {});

export function StudioDetailPage({ id, onNavigate }: Props) {
  const { config } = useAppConfig();
  const { hasPermission, user } = useAuth();
  const metadataServers = config?.scraping?.metadataServers ?? [];
  const { data: studio, isLoading } = useQuery({
    queryKey: ["studio", id],
    queryFn: () => studios.get(id),
  });
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [metadataTaggerOpen, setMetadataTaggerOpen] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const { activeTab, setActiveTab } = useDetailTabUrlState<TabKey>("videos");
  const [includeSubStudios, setIncludeSubStudios] = useDetailBooleanUrlState("includeSubStudios");
  const { data: recursiveStudio } = useQuery({
    queryKey: ["studio", id, "depth", -1],
    queryFn: () => studios.get(id, -1),
    enabled: includeSubStudios,
  });
  const { allTabs: studioTabs, renderExtensionTab, extensionCounts } = useExtensionTabs("studio", [
    { key: "videos", label: "Videos", count: includeSubStudios ? recursiveStudio?.videoCount : studio?.videoCount },
    { key: "performers", label: "Performers", count: includeSubStudios ? recursiveStudio?.performerCount : studio?.performerCount },
    { key: "galleries", label: "Galleries", count: includeSubStudios ? recursiveStudio?.galleryCount : studio?.galleryCount },
    { key: "images", label: "Images", count: includeSubStudios ? recursiveStudio?.imageCount : studio?.imageCount },
    { key: "audios", label: "Audios", count: includeSubStudios ? recursiveStudio?.audioCount : studio?.audioCount },
    { key: "texts", label: "Texts", count: includeSubStudios ? recursiveStudio?.textCount : studio?.textCount },
    { key: "studios", label: "Sub-studios", count: studio?.childStudioCount },
    { key: "groups", label: "Groups", count: includeSubStudios ? recursiveStudio?.groupCount : studio?.groupCount },
  ], id);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "studios" }, onNavigate);
  const canWriteStudio = canWriteEntity("studio", hasPermission);
  const canEngageStudio = canReadEntity("studio", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canDeleteStudio = canDeleteEntity("studio", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const showStudioOpsMenu = canWriteStudio || canDeleteStudio;
  const visibleStudioTabs = filterItemsByPermission(studioTabs, {
    videos: "videos.read",
    performers: "performers.read",
    galleries: "galleries.read",
    images: "images.read",
    audios: "audios.read",
    texts: "texts.read",
    studios: "studios.read",
    groups: "groups.read",
  }, hasPermission);

  const {
    favorite: studioFavorite,
    rating: studioRating,
    setFavorite: setStudioFavorite,
    setRating: setStudioRating,
  } = useEntityEngagement("studio", id, {
    fallbackFavorite: studio?.favorite,
    fallbackRating: undefined,
  });

  useDocumentTitle(studio?.name);

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

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
      switch (e.key) {
        case "e": if (canWriteStudio) setEditing((v) => !v); break;
        case "f": if (studio && canEngageStudio) setStudioFavorite(!studioFavorite); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [canEngageStudio, canWriteStudio, studio, studioFavorite, setStudioFavorite]);

  useEffect(() => {
    if (visibleStudioTabs.length > 0 && !visibleStudioTabs.some((tab) => tab.key === activeTab)) {
      setActiveTab(visibleStudioTabs[0].key as TabKey);
    }
  }, [activeTab, visibleStudioTabs]);

  const deleteMut = useMutation({
    mutationFn: () => studios.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studios"] });
      goBack();
    },
  });

  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => studios.update(id, data),
    onMutate: async (data) => {
      if (data.organized === undefined) return undefined;
      await queryClient.cancelQueries({ queryKey: ["studio", id] });
      const previous = queryClient.getQueryData<Studio>(["studio", id]);
      queryClient.setQueryData<Studio>(["studio", id], (current) => current ? { ...current, organized: data.organized! } : current);
      return { previous };
    },
    onError: (_error, _data, context) => {
      if (context?.previous) queryClient.setQueryData(["studio", id], context.previous);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studio", id] });
      queryClient.invalidateQueries({ queryKey: ["studios"] });
    },
  });

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (!studio) {
    return <div className="py-16 text-center text-secondary">Studio not found</div>;
  }

  const studioImageUrl = studio.imagePath || entityImages.studioImageUrl(studio.id, studio.updatedAt);
  const handleCoverChanged = () => {
    queryClient.invalidateQueries({ queryKey: ["studio", studio.id] });
    queryClient.invalidateQueries({ queryKey: ["studios"] });
  };

  return (
    <>
      <EntityHeroLayout
        backLabel={backLabel}
        onGoBack={goBack}
        backgroundImageUrl={entityImages.studioImageUrl(studio.id, studio.updatedAt, 1600)}
        imageUrl={studioImageUrl}
        imageAlt={studio.name}
        imageClassName="h-full w-full object-contain p-3"
        onImageClick={canWriteStudio ? () => setCoverOpen(true) : undefined}
        imageFallback={<Building2 className="h-14 w-14 text-accent" />}
        title={<FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="name">{studio.name}</FieldProvenanceHover>}
        subtitle={studio.parentName && studio.parentId ? (
          canReadEntity("studio", hasPermission) ? (
            <FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="parent">
              <button onClick={() => onNavigate({ page: "studio", id: studio.parentId })} className="text-accent hover:underline">
                Part of {studio.parentName}
              </button>
            </FieldProvenanceHover>
          ) : <FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="parent"><span>Part of {studio.parentName}</span></FieldProvenanceHover>
        ) : undefined}
        aliases={studio.aliases.length > 0 ? <FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="aliases">{studio.aliases.join(", ")}</FieldProvenanceHover> : undefined}
        favorite={studioFavorite}
        onFavoriteToggle={canEngageStudio ? () => setStudioFavorite(!studioFavorite) : undefined}
        organized={studio.organized}
        organizedPending={updateMut.isPending}
        onOrganizedToggle={canWriteStudio ? (organized) => updateMut.mutate({ organized }) : undefined}
        counts={[
          { key: "videos", label: "Videos", value: studio.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "performers", label: "Performers", value: studio.performerCount, icon: <UserRound className="h-4 w-4" /> },
          { key: "images", label: "Images", value: studio.imageCount, icon: <ImageIcon className="h-4 w-4" /> },
          { key: "galleries", label: "Galleries", value: studio.galleryCount, icon: <FolderOpen className="h-4 w-4" /> },
          { key: "audios", label: "Audios", value: studio.audioCount, icon: <Headphones className="h-4 w-4" /> },
          { key: "texts", label: "Texts", value: studio.textCount, icon: <FileText className="h-4 w-4" /> },
          { key: "studios", label: "Sub-studios", value: studio.childStudioCount, icon: <Building2 className="h-4 w-4" /> },
          { key: "groups", label: "Groups", value: studio.groupCount, icon: <Layers className="h-4 w-4" /> },
          ...extensionCounts.map((ec) => ({
            key: ec.key,
            label: ec.label,
            value: ec.count,
            icon: ec.icon === "music" ? <Music className="h-4 w-4" /> : undefined,
          })),
        ]}
        metaRow={(
          <>
            <span title={`Created ${formatDate(studio.createdAt)}`}>Updated {formatDate(studio.updatedAt)}</span>
          </>
        )}
        heroContent={(
          <>
            <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
              <div className="shrink-0">
                <InteractiveRating value={studioRating} onChange={(value) => setStudioRating(value)} readOnly={!canEngageStudio} />
              </div>
            </div>
            {studio.details ? <FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="details" block><p className="mt-3 max-w-4xl whitespace-pre-wrap text-sm leading-6 text-secondary">{studio.details}</p></FieldProvenanceHover> : null}
            {canReadTags && studio.tags.length > 0 ? (
              <div className="mt-4 flex flex-wrap gap-1.5">
                {studio.tags.map((tag) => (
                  <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, studio.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                ))}
              </div>
            ) : null}
            {(studio.urls.length > 0 || studio.remoteIds.length > 0) ? (
              <FieldProvenanceHover fieldProvenance={studio.fieldProvenance} fieldKey="urls" block className="mt-3">
                <div className="flex flex-wrap gap-2">
                  <MetadataServerLinks remoteIds={studio.remoteIds} entityType="studios" metadataServers={metadataServers} />
                  {studio.urls.map((url, index) => (
                    <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="inline-flex max-w-xs items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 text-xs text-accent hover:border-accent/60 hover:text-accent-hover">
                      <LinkIcon className="h-3 w-3 flex-shrink-0" />
                      <span className="truncate">{formatUrlHost(url)}</span>
                    </a>
                  ))}
                </div>
              </FieldProvenanceHover>
            ) : null}
            <CustomFieldsDisplay customFields={studio.customFields} entityType="studio" />
            <StudioMetadataServerPanel studio={studio} metadataServers={metadataServers} onNavigate={onNavigate} />
          </>
        )}
        actions={(
          <>
            <ExtensionSlot slot="studio-detail-actions" context={{ studio, onNavigate }} />
            {canWriteStudio ? (
              <button
                type="button"
                onClick={() => setEditing(true)}
                className={HERO_PRIMARY_ACTION_BUTTON_CLASS}
                title="Edit"
              >
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
            ) : null}
            {showStudioOpsMenu ? (
              <div className="relative" ref={opsMenuRef}>
                <button
                  onClick={() => setShowOpsMenu(!showOpsMenu)}
                  className={`${HERO_ACTION_BUTTON_CLASS} text-secondary`}
                  title="Actions"
                >
                  <MoreVertical className="h-4 w-4" />
                </button>
                <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[160px] py-1">
                  {canWriteStudio ? <button onClick={() => { setMetadataTaggerOpen(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface"><Search className="h-3.5 w-3.5" /> Metadata...</button> : null}
                    {canWriteStudio ? <button onClick={() => { setMergeOpen(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface"><GitMerge className="h-3.5 w-3.5" /> Merge...</button> : null}
                    {canDeleteStudio ? <div className="my-1 border-t border-border" /> : null}
                    {canDeleteStudio ? <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-red-400 hover:bg-surface"><Trash2 className="h-3.5 w-3.5" /> Delete</button> : null}
                </FloatingActionMenu>
              </div>
            ) : null}
          </>
        )}
        heroRowClassName="flex flex-col gap-6 md:flex-row md:items-start"
      >
        <ExtensionSlot slot="studio-detail-sidebar-bottom" context={{ studio, onNavigate }} />

        <EntityDetailTabs tabs={visibleStudioTabs} activeTab={activeTab} onTabChange={(key) => setActiveTab(key as TabKey)} className="mx-auto max-w-7xl mt-6" />
        {activeTab !== "studios" && (
          <div className="mx-auto mt-4 max-w-7xl px-4">
            <HierarchyContentToggle checked={includeSubStudios} label="Include sub-studio content" onChange={setIncludeSubStudios} />
          </div>
        )}

        <div className="py-6">
          {activeTab === "videos" && (
            <StudioVideosPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "performers" && (
            <StudioPerformersPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "galleries" && (
            <StudioGalleriesPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "images" && (
            <StudioImagesPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "audios" && (
            <StudioAudiosPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "texts" && (
            <StudioTextsPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {activeTab === "studios" && (
            <ChildStudiosPanel studioId={id} onNavigate={onNavigate} />
          )}
          {activeTab === "groups" && (
            <StudioGroupsPanel studioId={id} includeSubStudios={includeSubStudios} onNavigate={onNavigate} />
          )}
          {renderExtensionTab(activeTab, id, onNavigate)}
        </div>

        <ExtensionSlot slot="studio-detail-bottom" context={{ studio, onNavigate }} />
      </EntityHeroLayout>

      <CoverImageDialog
        open={coverOpen}
        title="Set Studio Cover"
        currentImageUrl={studioImageUrl}
        onUpload={(file) => entityImages.uploadStudioImage(studio.id, file)}
        onDelete={() => entityImages.deleteStudioImage(studio.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={handleCoverChanged}
        aspectRatio="1/1"
        objectFit="contain"
      />

      <StudioEditModal studio={studio} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Studio"
        message={`Delete "${studio.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />
      <DetailMergeDialog
        open={mergeOpen}
        onClose={() => setMergeOpen(false)}
        entityType="studio"
        targetItem={{ id: studio.id, name: studio.name, imagePath: studioImageUrl, subtitle: studio.parentName }}
        searchItems={async (term) => {
          const response = await studios.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: term || undefined });
          return response.items.map((item) => ({
            id: item.id,
            name: item.name,
            imagePath: item.imagePath,
            subtitle: item.parentName,
          }));
        }}
        onMerge={(targetId, sourceIds) => studios.merge(targetId, sourceIds)}
        invalidateQueryKeys={[["studio", id], ["studios"]]}
      />
      <StudioMetadataTaggerDialog open={metadataTaggerOpen} onClose={() => setMetadataTaggerOpen(false)} studio={studio} />

    </>
  );
}

function StudioMetadataServerPanel({ studio, metadataServers, onNavigate }: { studio: Studio; metadataServers: MetadataServer[]; onNavigate: (r: any) => void }) {
  const queryClient = useQueryClient();
  const [term, setTerm] = useState(studio.name);
  const [selectedEndpoint, setSelectedEndpoint] = useState("");
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    setTerm(studio.name);
  }, [studio.id, studio.name]);

  useEffect(() => {
    if (selectedEndpoint && !metadataServers.some((box) => box.endpoint === selectedEndpoint)) {
      setSelectedEndpoint("");
    }
  }, [selectedEndpoint, metadataServers]);

  const searchMutation = useMutation({
    mutationFn: (variables: { term?: string; endpoint?: string }) => studios.searchMetadataServer(studio.id, variables.term, variables.endpoint),
  });

  const submitEndpoint = selectedEndpoint || (metadataServers.length === 1 ? metadataServers[0].endpoint : "");

  const submitMutation = useMutation({
    mutationFn: (endpoint: string) => studios.submitMetadataServerDraft(studio.id, endpoint),
  });

  const importMutation = useMutation({
    mutationFn: (match: MetadataServerStudioMatch) =>
      studios.importFromMetadataServer(studio.id, { endpoint: match.endpoint, studioId: match.id }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studio", studio.id] });
      queryClient.invalidateQueries({ queryKey: ["studios"] });
    },
  });

  return (
    <div className="mt-6 rounded-xl border border-border bg-card p-4">
      <button onClick={() => setExpanded(!expanded)} className="flex w-full items-center justify-between text-left">
        <div className="flex items-center gap-3">
          <h2 className="text-base font-semibold text-foreground">MetadataServer</h2>
        </div>
        <ChevronDown className={`h-4 w-4 text-muted transition-transform ${expanded ? "rotate-180" : ""}`} />
      </button>

      {expanded && (
        <div className="mt-4">
          {metadataServers.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
              No MetadataServer endpoints are configured yet. Use Settings and open Metadata Providers to add one.
              <button onClick={() => onNavigate({ page: "settings" })} className="ml-2 text-accent hover:text-accent-hover">
                Open settings
              </button>
            </div>
          ) : (
            <>
              <div className="grid gap-3 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_auto]">
                <label className="block text-sm">
                  <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">Search term</span>
                  <input
                    value={term}
                    onChange={(event) => setTerm(event.target.value)}
                    placeholder={studio.name}
                    className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                  />
                </label>
                <label className="block text-sm">
                  <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">Endpoint</span>
                  <select
                    value={selectedEndpoint}
                    onChange={(event) => setSelectedEndpoint(event.target.value)}
                    className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                  >
                    <option value="">All configured endpoints</option>
                    {metadataServers.map((box) => (
                      <option key={box.endpoint} value={box.endpoint}>
                        {box.name || box.endpoint}
                      </option>
                    ))}
                  </select>
                </label>
                <div className="flex flex-wrap items-end gap-2">
                  <button
                    onClick={() => searchMutation.mutate({ term: term.trim() || undefined, endpoint: selectedEndpoint || undefined })}
                    disabled={searchMutation.isPending}
                    className="inline-flex items-center gap-2 rounded-xl border border-border px-4 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
                  >
                    {searchMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                    Search MetadataServer
                  </button>
                  <button
                    onClick={() => submitMutation.mutate(submitEndpoint)}
                    disabled={submitMutation.isPending || !submitEndpoint}
                    title={!submitEndpoint ? "Choose a single MetadataServer endpoint before submitting a draft" : "Submit this studio as a MetadataServer draft"}
                    className="inline-flex items-center gap-2 rounded-xl border border-border px-4 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
                  >
                    {submitMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <CloudUpload className="h-4 w-4" />}
                    Submit Draft
                  </button>
                </div>
              </div>

              {searchMutation.error && <p className="mt-3 text-sm text-red-300">{searchMutation.error.message}</p>}
              {submitMutation.error && <p className="mt-3 text-sm text-red-300">{submitMutation.error.message}</p>}
              {importMutation.isSuccess && <p className="mt-3 text-sm text-emerald-300">Studio metadata imported from MetadataServer.</p>}
              {submitMutation.isSuccess && <p className="mt-3 text-sm text-emerald-300">Studio draft submitted to MetadataServer.</p>}

              {searchMutation.data && (
                <div className="mt-4 space-y-3">
                  {searchMutation.data.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
                      No MetadataServer studio matches were found.
                    </div>
                  ) : (
                    searchMutation.data.map((match) => (
                      <button
                        key={`${match.endpoint}:${match.id}`}
                        onClick={() => importMutation.mutate(match)}
                        disabled={importMutation.isPending}
                        className="flex w-full flex-col gap-4 rounded-xl border border-border bg-surface p-4 text-left transition-colors hover:border-accent/60 disabled:opacity-60 md:flex-row"
                      >
                        <div className="h-20 w-20 flex-shrink-0 overflow-hidden rounded-lg border border-border bg-black/20">
                          {match.imageUrl ? (
                            <img src={match.imageUrl} alt={match.name} className="h-full w-full object-cover" />
                          ) : (
                            <div className="flex h-full w-full items-center justify-center bg-gradient-to-b from-card to-surface">
                              <Building2 className="h-10 w-10 text-muted/50" />
                            </div>
                          )}
                        </div>

                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="text-base font-semibold text-foreground">{match.name}</span>
                            <span className="rounded-full border border-border px-2 py-0.5 text-xs text-secondary">{match.serverName}</span>
                          </div>
                          {match.parentName && <p className="mt-1 text-sm text-secondary">Parent: {match.parentName}</p>}
                          <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
                            <span>ID {match.id}</span>
                            {match.urls[0] && <span className="truncate">{match.urls[0]}</span>}
                          </div>
                          {match.aliases.length > 0 && <p className="mt-2 text-xs text-secondary">Aliases: {match.aliases.join(", ")}</p>}
                        </div>

                        <div className="flex items-end">
                          <span className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white">
                            {importMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <CloudDownload className="h-4 w-4" />}
                            Import
                          </span>
                        </div>
                      </button>
                    ))
                  )}
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function StudioVideosPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "videos", resetKey: "studio-videos", entityType: "videos", builtInFilter: { page: 1, perPage: 24, direction: "desc" }, defaultFilterKey: "videos" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Video>({
    queryKey: ["studio-videos", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubStudios
      ? videos.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredSingleId(objectFilter as VideoFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
        })
      : videos.find(nextFilter, { studioId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={VIDEO_SORT_OPTIONS} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="videos" selectedIds={selectedIds} onDone={selectNone} videoItems={items} onNavigate={onNavigate} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={VIDEO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="videos" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading videos..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Film className="h-12 w-12" />} message="No videos from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="videos" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onVideoQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      {quickViewId !== null && (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function StudioGalleriesPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "galleries", resetKey: "studio-galleries", entityType: "galleries", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "galleries" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Gallery>({
    queryKey: ["studio-galleries", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubStudios
      ? galleries.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredSingleId(objectFilter as GalleryFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
        })
      : galleries.find(nextFilter, { studioId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GALLERY_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="galleries" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={GALLERY_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="galleries" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<FolderOpen className="h-10 w-10" />} message="Loading galleries..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<FolderOpen className="h-12 w-12" />} message="No galleries from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="galleries" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
    </>
  );
}

function StudioImagesPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "images", resetKey: "studio-images", entityType: "images", builtInFilter: { page: 1, perPage: 30, direction: "desc" }, defaultFilterKey: "images" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Image>({
    queryKey: ["studio-images", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubStudios
      ? images.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredSingleId(objectFilter as ImageFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
        })
      : images.find(nextFilter, { studioId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={IMAGE_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="images" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={IMAGE_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="images" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<ImageIcon className="h-10 w-10" />} message="Loading images..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="images" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onImageQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      {quickViewId !== null && (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function StudioAudiosPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "audios", resetKey: "studio-audios", entityType: "audios", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "audios" });
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Audio>({
    queryKey: ["studio-audios", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => audios.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as AudioFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
    }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={AUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="audios" selectedIds={selectedIds} onDone={selectNone} audioItems={items} downloadItems={items} onNavigate={onNavigate} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={AUDIO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="audios" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<Headphones className="h-10 w-10" />} message="Loading audios..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Headphones className="h-12 w-12" />} message="No audios from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="audios" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
    </>
  );
}

function StudioTextsPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "texts", resetKey: "studio-texts", entityType: "texts", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "texts" });
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<TextDocument>({
    queryKey: ["studio-texts", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => texts.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as TextFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
    }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={TEXT_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="texts" selectedIds={selectedIds} onDone={selectNone} textItems={items} downloadItems={items} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={TEXT_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="texts" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<FileText className="h-10 w-10" />} message="Loading texts..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<FileText className="h-12 w-12" />} message="No texts from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="texts" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
    </>
  );
}

function ChildStudiosPanel({ studioId, onNavigate }: {
  studioId: number;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "studios", resetKey: "studio-children", entityType: "studios", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "studios" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Studio>({
    queryKey: ["child-studios", studioId, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter
      ? studios.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredSingleId(objectFilter as StudioFilterCriteria, "parentsCriterion", studioId),
        })
      : studios.find(nextFilter, { parentId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={STUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="studios" selectedIds={selectedIds} onDone={selectNone} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={STUDIO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="studios" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<Building2 className="h-10 w-10" />} message="Loading sub-studios..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Building2 className="h-12 w-12" />} message="No sub-studios" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="studios" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
    </>
  );
}

function StudioPerformersPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "performers", resetKey: "studio-performers", entityType: "performers", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "performers" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Performer>({
    queryKey: ["studio-performers", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubStudios
      ? performers.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as PerformerFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
        })
      : performers.find(nextFilter, { studioId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={PERFORMER_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="performers" selectedIds={selectedIds} onDone={selectNone} />} criteriaDefinitions={PERFORMER_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="performers" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<UserRound className="h-10 w-10" />} message="Loading performers..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<UserRound className="h-12 w-12" />} message="No performers from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="performers" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
    </>
  );
}

function StudioGroupsPanel({ studioId, includeSubStudios, onNavigate }: {
  studioId: number;
  includeSubStudios: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "groups", resetKey: "studio-groups", entityType: "groups", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "groups" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Group>({
    queryKey: ["studio-groups", studioId, includeSubStudios, objectFilter],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubStudios
      ? groups.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredSingleId(objectFilter as GroupFilterCriteria, "studiosCriterion", studioId, includeSubStudios ? -1 : undefined),
        })
      : groups.find(nextFilter, { studioId: String(studioId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GROUP_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="groups" selectedIds={selectedIds} onDone={selectNone} removeFromParent={{ type: "studio", id: studioId }} />} criteriaDefinitions={GROUP_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="groups" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />
  );

  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading groups..." />;
  if (!data || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Layers className="h-12 w-12" />} message="No groups from this studio" /></>;

  return (
    <>
      {toolbar}
      <RelatedEntityListView entityType="groups" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
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
