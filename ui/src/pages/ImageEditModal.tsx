import { useState, useEffect } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { images, system } from "../api/client";
import type { DownloaderMatch, Image, ImageCreate, VideoGroupInput } from "../api/types";
import { CreateModalActions, EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { IsoDateInput } from "../components/IsoDateInput";
import { RatingField } from "../components/Rating";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { EntityReferenceMultiSelector, EntityReferenceValue } from "../components/EntityReferenceSelector";
import { PerformerContextTagEditor, buildPerformerContextTagIds, syncPerformerContextTags } from "../components/PerformerContextTags";
import { FileBackedCreateSource, type CreateSourceMode } from "../components/FileBackedCreateSource";
import { createFromUrlWithOptionalDownload, mergeUrlLists, NoDownloaderFoundError, type UrlDownloadMode } from "../utils/createFromUrlDownload";
import { useFileBackedCreatePreferences } from "../hooks/useFileBackedCreatePreferences";
import { ImageSourceDownloadDialog } from "../components/ImageSourceDownloadDialog";

interface ImageEditProps {
  image: Image;
  open: boolean;
  onClose: () => void;
}

interface ImageCreateProps {
  open: boolean;
  onClose: () => void;
  onCreated: (id: number) => void;
}

interface ImageFormState {
  title: string;
  code: string;
  details: string;
  photographer: string;
  date: string;
  rating: number | undefined;
  urls: string[];
  studioId: number | undefined;
  selectedTagIds: number[];
  selectedPerformerIds: number[];
  contextTagIdsByPerformer: Record<number, number[]>;
  selectedGalleryIds: number[];
  selectedGroups: VideoGroupInput[];
  customFields: Record<string, unknown>;
}

interface ImageMetadataModalProps {
  title: string;
  open: boolean;
  onClose: () => void;
  initialState: ImageFormState;
  onSubmit: (data: ImageCreate, contextTagIdsByPerformer: Record<number, number[]>, selectedPerformerIds: number[]) => void;
  isPending: boolean;
  error: Error | null;
  image?: Image;
  resetSignal?: number;
  createAnother?: boolean;
  onCreateAnotherChange?: (value: boolean) => void;
  sourceMode?: CreateSourceMode;
  onSourceModeChange?: (value: CreateSourceMode) => void;
  filePath?: string;
  onFilePathChange?: (value: string) => void;
  url?: string;
  onUrlChange?: (value: string) => void;
  urlDownloadMode?: UrlDownloadMode;
  onUrlDownloadModeChange?: (value: UrlDownloadMode) => void;
  scrapeMetadata?: boolean;
  onScrapeMetadataChange?: (value: boolean) => void;
  noDownloaderFound?: boolean;
  onCreateWithoutDownload?: (data: ImageCreate, contextTagIdsByPerformer: Record<number, number[]>, selectedPerformerIds: number[]) => void;
  onDismissNoDownloader?: () => void;
  onCreateFromFile?: (filePath: string, data: ImageCreate, contextTagIdsByPerformer: Record<number, number[]>, selectedPerformerIds: number[]) => void;
  onCreateFromUrl?: (url: string, data: ImageCreate, contextTagIdsByPerformer: Record<number, number[]>, selectedPerformerIds: number[], downloadMode: UrlDownloadMode, scrapeMetadata: boolean) => void;
  renderMode?: "modal" | "panel";
}

const EMPTY_FORM_STATE: ImageFormState = {
  title: "",
  code: "",
  details: "",
  photographer: "",
  date: "",
  rating: undefined,
  urls: [""],
  studioId: undefined,
  selectedTagIds: [],
  selectedPerformerIds: [],
  contextTagIdsByPerformer: {},
  selectedGalleryIds: [],
  selectedGroups: [],
  customFields: {},
};

function toFormState(image?: Image): ImageFormState {
  if (!image) {
    return {
      ...EMPTY_FORM_STATE,
      selectedTagIds: [],
      selectedPerformerIds: [],
    };
  }

  return {
    title: image.title || "",
    code: image.code || "",
    details: image.details || "",
    photographer: image.photographer || "",
    date: image.date || "",
    rating: undefined,
    urls: image.urls.length > 0 ? image.urls : [""],
    studioId: image.studioId ?? undefined,
    selectedTagIds: image.tags.map((tag) => tag.id),
    selectedPerformerIds: image.performers.map((performer) => performer.id),
    contextTagIdsByPerformer: buildPerformerContextTagIds(image.contextTagApplications),
    selectedGalleryIds: image.galleryIds ?? [],
    selectedGroups: (image.groups ?? []).map((group) => ({ groupId: group.id, videoIndex: group.videoIndex ?? 0 })),
    customFields: { ...(image.customFields ?? {}) },
  };
}

function cloneFormState(state: ImageFormState): ImageFormState {
  return {
    ...state,
    urls: [...state.urls],
    selectedTagIds: [...state.selectedTagIds],
    selectedPerformerIds: [...state.selectedPerformerIds],
    contextTagIdsByPerformer: Object.fromEntries(Object.entries(state.contextTagIdsByPerformer).map(([performerId, tagIds]) => [performerId, [...tagIds]])),
    selectedGalleryIds: [...state.selectedGalleryIds],
    selectedGroups: state.selectedGroups.map((group) => ({ ...group })),
    customFields: { ...state.customFields },
  };
}

function ImageMetadataModal({ title, open, onClose, initialState, onSubmit, isPending, error, image, resetSignal, createAnother, onCreateAnotherChange, sourceMode = "metadata", onSourceModeChange, filePath = "", onFilePathChange, url = "", onUrlChange, urlDownloadMode = "now", onUrlDownloadModeChange, scrapeMetadata = false, onScrapeMetadataChange, noDownloaderFound = false, onCreateWithoutDownload, onDismissNoDownloader, onCreateFromFile, onCreateFromUrl, renderMode = "modal" }: ImageMetadataModalProps) {
  const [form, setForm] = useState<ImageFormState>(() => cloneFormState(initialState));
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  useEffect(() => {
    if (!open) return;
    setForm(cloneFormState(initialState));
    setCustomFieldsValid(true);
  }, [initialState, open, resetSignal]);
  const showRating = Boolean(image);
  const tagProvenanceById = buildTagProvenanceById(image?.tags ?? [], image?.fieldProvenance);
  // Seed chip labels from the loaded image so selected chips don't each re-fetch their name by id.
  const tagSeedOptions = (image?.tags ?? []).map((tag) => ({ id: tag.id, label: tag.name }));
  const performerSeedOptions = (image?.performers ?? []).map((performer) => ({
    id: performer.id,
    label: performer.name,
    secondaryLabel: performer.disambiguation ? `(${performer.disambiguation})` : undefined,
  }));

  const buildPayload = (): ImageCreate & { clearFields?: string[] } => {
    const urlList = form.urls.map((url) => url.trim()).filter(Boolean);
    // On edit, send raw (trimmed) strings including "" so cleared fields persist.
    // On create, omit empties to avoid sending empty noise.
    const isEdit = Boolean(image);
    const text = (value: string) => {
      const trimmed = value.trim();
      return isEdit ? trimmed : trimmed || undefined;
    };
    const clearFields = isEdit
      ? [
          !form.date && "date",
          form.studioId === undefined && "studioId",
        ].filter((field): field is string => Boolean(field))
      : undefined;
    return {
      title: text(form.title),
      code: text(form.code),
      details: text(form.details),
      photographer: text(form.photographer),
      date: form.date || undefined,
      ...(showRating ? { rating: form.rating } : {}),
      studioId: form.studioId,
      urls: urlList,
      tagIds: form.selectedTagIds,
      performerIds: form.selectedPerformerIds,
      galleryIds: form.selectedGalleryIds,
      groupIds: form.selectedGroups,
      customFields: image ? form.customFields : Object.keys(form.customFields).length > 0 ? form.customFields : undefined,
      clearFields,
    };
  };

  const handleSave = () => {
    const payload = buildPayload();
    if (sourceMode === "file" && onCreateFromFile) {
      const trimmedPath = filePath.trim();
      if (trimmedPath) onCreateFromFile(trimmedPath, payload, form.contextTagIdsByPerformer, form.selectedPerformerIds);
      return;
    }

    if (sourceMode === "url" && onCreateFromUrl) {
      const requestedUrl = url.trim();
      if (requestedUrl) onCreateFromUrl(requestedUrl, payload, form.contextTagIdsByPerformer, form.selectedPerformerIds, urlDownloadMode, scrapeMetadata);
      return;
    }

    onSubmit(payload, form.contextTagIdsByPerformer, form.selectedPerformerIds);
  };

  const handleCreateWithoutDownload = () => {
    const requestedUrl = url.trim();
    if (requestedUrl && onCreateWithoutDownload) {
      const payload = buildPayload();
      onCreateWithoutDownload({ ...payload, urls: mergeUrlLists(payload.urls, [requestedUrl]) }, form.contextTagIdsByPerformer, form.selectedPerformerIds);
    }
  };

  const setSelectedGroupIds = (groupIds: number[]) => {
    setForm({
      ...form,
      selectedGroups: groupIds.map((groupId) => form.selectedGroups.find((group) => group.groupId === groupId) ?? { groupId, videoIndex: 0 }),
    });
  };

  const formContent = (
    <>
      {onSourceModeChange && onFilePathChange ? (
        <FileBackedCreateSource
          mode={sourceMode}
          onModeChange={onSourceModeChange}
          filePath={filePath}
          onFilePathChange={onFilePathChange}
          url={url}
          onUrlChange={onUrlChange}
          urlDownloadMode={urlDownloadMode}
          onUrlDownloadModeChange={onUrlDownloadModeChange}
          scrapeMetadata={scrapeMetadata}
          onScrapeMetadataChange={onScrapeMetadataChange}
          noDownloaderFound={noDownloaderFound}
          onCreateWithoutDownload={onCreateWithoutDownload ? handleCreateWithoutDownload : undefined}
          onDismissNoDownloader={onDismissNoDownloader}
          modes={["metadata", "file", "url"]}
          filePlaceholder="C:\\Media\\image.jpg"
          urlPlaceholder="https://example.com/image.jpg"
        />
      ) : null}

      <>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Title" fieldProvenance={image?.fieldProvenance} fieldKey="title">
          <TextInput value={form.title} onChange={(value) => setForm({ ...form, title: value })} placeholder="Image title" />
        </Field>
        <Field label="Date" fieldProvenance={image?.fieldProvenance} fieldKey="date">
          <IsoDateInput
            value={form.date}
            onChange={(e) => setForm({ ...form, date: e.target.value })}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Studio Code" fieldProvenance={image?.fieldProvenance} fieldKey="code">
          <TextInput value={form.code} onChange={(value) => setForm({ ...form, code: value })} placeholder="Image code" />
        </Field>
        <Field label="Photographer" fieldProvenance={image?.fieldProvenance} fieldKey="photographer">
          <TextInput value={form.photographer} onChange={(value) => setForm({ ...form, photographer: value })} placeholder="Photographer name" />
        </Field>
      </div>

      <Field label="Details" fieldProvenance={image?.fieldProvenance} fieldKey="details">
        <TextArea value={form.details} onChange={(value) => setForm({ ...form, details: value })} placeholder="Image description" />
      </Field>

      {renderMode === "panel" || !showRating ? (
        <Field label="Studio" fieldProvenance={image?.fieldProvenance} fieldKey={["studio", "studioId"]}>
          <StudioSelector value={form.studioId} onChange={(studioId) => setForm({ ...form, studioId })} />
        </Field>
      ) : (
        <div className="grid grid-cols-2 gap-4">
          <RatingField value={form.rating} onChange={(value) => setForm({ ...form, rating: value })} fieldProvenance={image?.fieldProvenance} />
          <Field label="Studio" fieldProvenance={image?.fieldProvenance} fieldKey={["studio", "studioId"]}>
            <StudioSelector value={form.studioId} onChange={(studioId) => setForm({ ...form, studioId })} />
          </Field>
        </div>
      )}

      <Field label="URLs" fieldProvenance={image?.fieldProvenance} fieldKey="urls">
        <StringListEditor values={form.urls} onChange={(value) => setForm({ ...form, urls: value })} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      <Field label="Tags" fieldProvenance={image?.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector entityType="tag" values={form.selectedTagIds} onChange={(selectedTagIds) => setForm({ ...form, selectedTagIds })} placeholder="Search tags..." selectedProvenanceById={tagProvenanceById} seedOptions={tagSeedOptions} />
      </Field>

      <Field label="Performers" fieldProvenance={image?.fieldProvenance} fieldKey="performers">
        <EntityReferenceMultiSelector entityType="performer" values={form.selectedPerformerIds} onChange={(selectedPerformerIds) => setForm({ ...form, selectedPerformerIds })} placeholder="Search performers..." seedOptions={performerSeedOptions} />
      </Field>

      {form.selectedPerformerIds.length > 0 ? (
        <Field label="Performer Occurrence Tags" fieldProvenance={image?.fieldProvenance} fieldKey="contextTags">
          <PerformerContextTagEditor
            performerIds={form.selectedPerformerIds}
            contextTagIdsByPerformer={form.contextTagIdsByPerformer}
            onChange={(performerId, tagIds) => setForm({
              ...form,
              contextTagIdsByPerformer: { ...form.contextTagIdsByPerformer, [performerId]: tagIds },
            })}
          />
        </Field>
      ) : null}

      {/* Galleries */}
      <Field label="Galleries" fieldProvenance={image?.fieldProvenance} fieldKey="galleries">
        <EntityReferenceMultiSelector entityType="gallery" values={form.selectedGalleryIds} onChange={(selectedGalleryIds) => setForm({ ...form, selectedGalleryIds })} placeholder="Search galleries..." />
      </Field>

      <Field label="Groups" fieldProvenance={image?.fieldProvenance} fieldKey="groups">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {form.selectedGroups.map((selectedGroup) => {
            return (
              <span key={selectedGroup.groupId} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-orange-900 text-orange-300">
                <EntityReferenceValue entityType="group" value={selectedGroup.groupId} />
                <button onClick={() => setForm({ ...form, selectedGroups: form.selectedGroups.filter((item) => item.groupId !== selectedGroup.groupId) })} className="hover:text-white">×</button>
              </span>
            );
          })}
        </div>
        <EntityReferenceMultiSelector entityType="group" values={form.selectedGroups.map((group) => group.groupId)} onChange={setSelectedGroupIds} placeholder="Search groups..." />
      </Field>

      <Field label="Custom Fields" fieldProvenance={image?.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor value={form.customFields} onChange={(v) => setForm({ ...form, customFields: v })} onValidityChange={setCustomFieldsValid} entityType="image" />
      </Field>

      {error && (
        <div className="bg-red-900/50 border border-red-700 text-red-300 rounded p-2 mb-4 text-sm">
          {error.message}
        </div>
      )}

      {onCreateAnotherChange ? (
        <CreateModalActions
          loading={isPending}
          disabled={!customFieldsValid}
          onCancel={onClose}
          onSave={handleSave}
          createAnother={createAnother ?? false}
          onCreateAnotherChange={onCreateAnotherChange}
        />
      ) : (
        <div className="flex justify-end gap-3">
          <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
          <SaveButton loading={isPending} disabled={!customFieldsValid} onClick={handleSave} />
        </div>
      )}
      </>
    </>
  );

  if (renderMode === "panel") {
    return <div className="space-y-4">{formContent}</div>;
  }

  return (
    <EditModal title={title} open={open} onClose={onClose}>
      {formContent}
    </EditModal>
  );
}

export function ImageEditPanel({ image, onSaved }: { image: Image; onSaved?: () => void }) {
  const queryClient = useQueryClient();

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ data, contextTagIdsByPerformer, selectedPerformerIds }: { data: ImageCreate; contextTagIdsByPerformer: Record<number, number[]>; selectedPerformerIds: number[] }) => {
      await images.update(image.id, data);
      await syncPerformerContextTags("image", image.id, image.contextTagApplications ?? [], contextTagIdsByPerformer, selectedPerformerIds);
      return images.get(image.id);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["image", image.id] });
      queryClient.invalidateQueries({ queryKey: ["images"] });
      onSaved?.();
    },
  });

  return (
    <ImageMetadataModal
      title="Edit Image"
      open
      onClose={() => onSaved?.()}
      initialState={toFormState(image)}
      onSubmit={(data, contextTagIdsByPerformer, selectedPerformerIds) => mutation.mutate({ data, contextTagIdsByPerformer, selectedPerformerIds })}
      isPending={mutation.isPending}
      error={mutation.error as Error | null}
      image={image}
      renderMode="panel"
    />
  );
}

export function ImageEditModal({ image, open, onClose }: ImageEditProps) {
  const queryClient = useQueryClient();

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ data, contextTagIdsByPerformer, selectedPerformerIds }: { data: ImageCreate; contextTagIdsByPerformer: Record<number, number[]>; selectedPerformerIds: number[] }) => {
      await images.update(image.id, data);
      await syncPerformerContextTags("image", image.id, image.contextTagApplications ?? [], contextTagIdsByPerformer, selectedPerformerIds);
      return images.get(image.id);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["image", image.id] });
      queryClient.invalidateQueries({ queryKey: ["images"] });
      onClose();
    },
  });

  return (
    <ImageMetadataModal
      title="Edit Image"
      open={open}
      onClose={onClose}
      initialState={toFormState(image)}
      onSubmit={(data, contextTagIdsByPerformer, selectedPerformerIds) => mutation.mutate({ data, contextTagIdsByPerformer, selectedPerformerIds })}
      isPending={mutation.isPending}
      error={mutation.error as Error | null}
      image={image}
    />
  );
}

export function ImageCreateModal({ open, onClose, onCreated }: ImageCreateProps) {
  const queryClient = useQueryClient();
  const [createAnother, setCreateAnother] = useState(false);
  const [resetSignal, setResetSignal] = useState(0);
  const [sourceMode, setSourceMode] = useState<CreateSourceMode>("metadata");
  const [filePath, setFilePath] = useState("");
  const [url, setUrl] = useState("");
  const { urlDownloadMode, setUrlDownloadMode, scrapeMetadata, setScrapeMetadata } = useFileBackedCreatePreferences("Image");
  const [noDownloaderFound, setNoDownloaderFound] = useState(false);
  const [sourceDownload, setSourceDownload] = useState<{ sourceUrl: string; data: ImageCreate; matches: DownloaderMatch[]; autoApplyMetadata: boolean } | null>(null);

  const handleCreated = (created: Image) => {
    queryClient.invalidateQueries({ queryKey: ["images"] });
    if (createAnother) {
      setResetSignal((value) => value + 1);
      setFilePath("");
      setUrl("");
      setNoDownloaderFound(false);
      setSourceMode("metadata");
      return;
    }
    onClose();
    if (created?.id) onCreated(created.id);
  };

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ data, contextTagIdsByPerformer, selectedPerformerIds }: { data: ImageCreate; contextTagIdsByPerformer: Record<number, number[]>; selectedPerformerIds: number[] }) => {
      const created = await images.create(data);
      if (!created?.id) {
        return created;
      }

      await syncPerformerContextTags("image", created.id, [], contextTagIdsByPerformer, selectedPerformerIds);
      return images.get(created.id);
    },
    onSuccess: handleCreated,
  });

  const fileMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ path, data, contextTagIdsByPerformer, selectedPerformerIds }: { path: string; data: ImageCreate; contextTagIdsByPerformer: Record<number, number[]>; selectedPerformerIds: number[] }) => {
      const created = await images.createFromFile({ filePath: path });
      if (!created?.id) {
        return created;
      }

      await images.update(created.id, data);
      await syncPerformerContextTags("image", created.id, [], contextTagIdsByPerformer, selectedPerformerIds);
      return images.get(created.id);
    },
    onSuccess: handleCreated,
  });

  const urlMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ requestedUrl, data, contextTagIdsByPerformer, selectedPerformerIds, downloadMode, scrapeMetadata }: { requestedUrl: string; data: ImageCreate; contextTagIdsByPerformer: Record<number, number[]>; selectedPerformerIds: number[]; downloadMode: UrlDownloadMode; scrapeMetadata: boolean }) => {
      if (downloadMode === "now") {
        const matches = (await system.matchDownloaders({ url: requestedUrl }))
          .filter((match) => match.supportedEntity.toLowerCase() === "image");

        if (matches.length > 1) {
          setSourceDownload({ sourceUrl: requestedUrl, data, matches, autoApplyMetadata: scrapeMetadata });
          return null;
        }

        if (matches.length === 0) {
          throw new NoDownloaderFoundError(requestedUrl);
        }
      }

      const created = await createFromUrlWithOptionalDownload({ requestedUrl, data, entity: "Image", downloadMode, scrapeMetadata, create: images.create });
      if (!created?.id) {
        return created;
      }

      await syncPerformerContextTags("image", created.id, [], contextTagIdsByPerformer, selectedPerformerIds);
      return images.get(created.id);
    },
    onSuccess: (created) => {
      if (!created) return;
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      handleCreated(created);
    },
    onError: (err) => {
      if (err instanceof NoDownloaderFoundError) setNoDownloaderFound(true);
    },
  });

  const handleSourceModeChange = (value: CreateSourceMode) => {
    setSourceMode(value);
    setNoDownloaderFound(false);
  };

  const handleUrlChange = (value: string) => {
    setUrl(value);
    setNoDownloaderFound(false);
  };

  const handleCreateWithoutDownload = (data: ImageCreate) => {
    mutation.mutate({ data, contextTagIdsByPerformer: EMPTY_FORM_STATE.contextTagIdsByPerformer, selectedPerformerIds: EMPTY_FORM_STATE.selectedPerformerIds });
  };

  const visibleError = (mutation.error ?? fileMutation.error ?? urlMutation.error) instanceof NoDownloaderFoundError
    ? null
    : (mutation.error ?? fileMutation.error ?? urlMutation.error) as Error | null;

  return (
    <>
      <ImageMetadataModal
        title="Create Image"
        open={open}
        onClose={onClose}
        initialState={EMPTY_FORM_STATE}
        onSubmit={(data, contextTagIdsByPerformer, selectedPerformerIds) => mutation.mutate({ data, contextTagIdsByPerformer, selectedPerformerIds })}
        isPending={mutation.isPending || fileMutation.isPending || urlMutation.isPending}
        error={visibleError}
        resetSignal={resetSignal}
        createAnother={createAnother}
        onCreateAnotherChange={setCreateAnother}
        sourceMode={sourceMode}
        onSourceModeChange={handleSourceModeChange}
        filePath={filePath}
        onFilePathChange={setFilePath}
        url={url}
        onUrlChange={handleUrlChange}
        urlDownloadMode={urlDownloadMode}
        onUrlDownloadModeChange={setUrlDownloadMode}
        scrapeMetadata={scrapeMetadata}
        onScrapeMetadataChange={setScrapeMetadata}
        noDownloaderFound={noDownloaderFound}
        onCreateWithoutDownload={(data, contextTagIdsByPerformer, selectedPerformerIds) => mutation.mutate({ data, contextTagIdsByPerformer, selectedPerformerIds })}
        onDismissNoDownloader={() => setNoDownloaderFound(false)}
        onCreateFromFile={(path, data, contextTagIdsByPerformer, selectedPerformerIds) => fileMutation.mutate({ path, data, contextTagIdsByPerformer, selectedPerformerIds })}
        onCreateFromUrl={(requestedUrl, data, contextTagIdsByPerformer, selectedPerformerIds, downloadMode, scrapeMetadata) => urlMutation.mutate({ requestedUrl, data, contextTagIdsByPerformer, selectedPerformerIds, downloadMode, scrapeMetadata })}
      />
      {sourceDownload ? (
        <ImageSourceDownloadDialog
          open
          sourceUrl={sourceDownload.sourceUrl}
          matches={sourceDownload.matches}
          baseTitle={sourceDownload.data.title}
          metadata={sourceDownload.data}
          autoApplyMetadata={sourceDownload.autoApplyMetadata}
          onClose={() => setSourceDownload(null)}
          onQueued={() => {
            queryClient.invalidateQueries({ queryKey: ["jobs"] });
            queryClient.invalidateQueries({ queryKey: ["images"] });
            setSourceDownload(null);
            onClose();
          }}
        />
      ) : null}
    </>
  );
}
