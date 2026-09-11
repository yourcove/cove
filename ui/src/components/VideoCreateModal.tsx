import { useEffect, useState, useRef } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { videos } from "../api/client";
import type { Video, VideoCreate } from "../api/types";
import { IsoDateInput } from "../components/IsoDateInput";
import { CreateModalActions, EditModal, Field, TextArea, TextInput } from "../components/EditModal";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { FileBackedCreateSource, type CreateSourceMode } from "../components/FileBackedCreateSource";
import { useFileBackedCreatePreferences } from "../hooks/useFileBackedCreatePreferences";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import {
  createFromUrlWithOptionalDownload,
  mergeUrlLists,
  NoDownloaderFoundError,
  type UrlDownloadMode,
} from "../utils/createFromUrlDownload";
import { CustomFieldsEditor } from "../components/shared";
export function VideoCreateModal({
  open,
  initialTitle = "",
  onClose,
  onCreated,
  split,
}: {
  split?: { source: Video; ownerId: number; fileId: number; filename: string };
  open: boolean;
  initialTitle?: string;
  onClose: () => void;
  onCreated: (id: number) => void;
}) {
  const qc = useQueryClient();
  const [title, setTitle] = useState("");
  const [code, setCode] = useState("");
  const [date, setDate] = useState("");
  const [details, setDetails] = useState("");
  const [director, setDirector] = useState("");
  const [isVr, setIsVr] = useState(false);
  const [urls, setUrls] = useState<string[]>([""]);
  const [studioId, setStudioId] = useState<number | undefined>(undefined);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [createAnother, setCreateAnother] = useState(false);
  const [sourceMode, setSourceMode] = useState<CreateSourceMode>("metadata");
  const [filePath, setFilePath] = useState("");
  const [url, setUrl] = useState("");
  const { urlDownloadMode, setUrlDownloadMode, scrapeMetadata, setScrapeMetadata } =
    useFileBackedCreatePreferences("Video");
  const [noDownloaderFound, setNoDownloaderFound] = useState(false);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>([]);
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>([]);
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>([]);

  useEffect(() => {
    if (open && !split) setTitle(initialTitle.trim());
  }, [initialTitle, open, split]);

  const touched = useRef(new Set<string>());
  const touchedCustomFields = useRef(new Set<string>());
  const editTitle = (value: Parameters<typeof setTitle>[0]) => {
    touched.current.add("title");
    setTitle(value);
  };
  const editCode = (value: Parameters<typeof setCode>[0]) => {
    touched.current.add("code");
    setCode(value);
  };
  const editDate = (value: Parameters<typeof setDate>[0]) => {
    touched.current.add("date");
    setDate(value);
  };
  const editDetails = (value: Parameters<typeof setDetails>[0]) => {
    touched.current.add("details");
    setDetails(value);
  };
  const editDirector = (value: Parameters<typeof setDirector>[0]) => {
    touched.current.add("director");
    setDirector(value);
  };
  const editIsVr = (value: Parameters<typeof setIsVr>[0]) => {
    touched.current.add("isVr");
    setIsVr(value);
  };
  const editUrls = (value: Parameters<typeof setUrls>[0]) => {
    touched.current.add("urls");
    setUrls(value);
  };
  const editStudioId = (value: Parameters<typeof setStudioId>[0]) => {
    touched.current.add("studioId");
    setStudioId(value);
  };
  const editSelectedTagIds = (value: Parameters<typeof setSelectedTagIds>[0]) => {
    touched.current.add("selectedTagIds");
    setSelectedTagIds(value);
  };
  const editSelectedPerformerIds = (value: Parameters<typeof setSelectedPerformerIds>[0]) => {
    touched.current.add("selectedPerformerIds");
    setSelectedPerformerIds(value);
  };
  const editSelectedGalleryIds = (value: Parameters<typeof setSelectedGalleryIds>[0]) => {
    touched.current.add("selectedGalleryIds");
    setSelectedGalleryIds(value);
  };
  const prepopulate = () => {
    if (!split) return;
    const source = split.source;
    if (!touched.current.has("title")) setTitle(source.title ?? "");
    if (!touched.current.has("code")) setCode(source.code ?? "");
    if (!touched.current.has("date")) setDate(source.date ?? "");
    if (!touched.current.has("details")) setDetails(source.details ?? "");
    if (!touched.current.has("director")) setDirector(source.director ?? "");
    if (!touched.current.has("isVr")) setIsVr(source.isVr ?? false);
    if (!touched.current.has("urls")) setUrls([...source.urls]);
    if (!touched.current.has("studioId")) setStudioId(source.studioId);
    if (!touched.current.has("selectedTagIds")) setSelectedTagIds(source.tags.map((item) => item.id));
    if (!touched.current.has("selectedPerformerIds")) setSelectedPerformerIds(source.performers.map((item) => item.id));
    if (!touched.current.has("selectedGalleryIds")) setSelectedGalleryIds(source.galleries.map((item) => item.id));
    setCustomFields((current) => {
      const next = { ...current };
      for (const [key, value] of Object.entries(source.customFields ?? {})) {
        if (!touchedCustomFields.current.has(key)) next[key] = structuredClone(value);
      }
      return next;
    });
  };
  const splitMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: VideoCreate) => videos.splitFile(split!.ownerId, split!.fileId, undefined, data),
    onSuccess: (created) => {
      void qc.invalidateQueries({ queryKey: ["videos"] });
      void qc.invalidateQueries({ queryKey: ["video", split!.source.id] });
      void qc.invalidateQueries({ queryKey: ["video", split!.ownerId] });
      onClose();
      onCreated(created.videoId);
    },
  });

  const resetForm = () => {
    setTitle("");
    setCode("");
    setDate("");
    setDetails("");
    setDirector("");
    setIsVr(false);
    setUrls([""]);
    setStudioId(undefined);
    setCustomFields({});
    setSourceMode("metadata");
    setFilePath("");
    setUrl("");
    setNoDownloaderFound(false);
    setSelectedTagIds([]);
    setSelectedPerformerIds([]);
    setSelectedGalleryIds([]);
  };

  const createMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: VideoCreate) => videos.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const createFromFileMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ path, data }: { path: string; data: VideoCreate }) => {
      const created = await videos.createFromFile({ filePath: path });
      return created?.id ? videos.update(created.id, data) : created;
    },
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const createFromUrlMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({
      requestedUrl,
      data,
      downloadMode,
      scrapeMetadata,
    }: {
      requestedUrl: string;
      data: VideoCreate;
      downloadMode: UrlDownloadMode;
      scrapeMetadata: boolean;
    }) =>
      createFromUrlWithOptionalDownload({
        requestedUrl,
        data,
        entity: "Video",
        downloadMode,
        scrapeMetadata,
        create: videos.create,
      }),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      qc.invalidateQueries({ queryKey: ["jobs"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
    onError: (err) => {
      if (err instanceof NoDownloaderFoundError) setNoDownloaderFound(true);
    },
  });

  const buildPayload = (extraUrls: string[] = []): VideoCreate => ({
    title: title || undefined,
    code: code || undefined,
    date: date || undefined,
    details: details || undefined,
    director: director || undefined,
    isVr,
    studioId,
    urls: mergeUrlLists(urls, extraUrls),
    tagIds: selectedTagIds,
    performerIds: selectedPerformerIds,
    galleryIds: selectedGalleryIds,
    customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
  });

  const handleSourceModeChange = (mode: CreateSourceMode) => {
    setSourceMode(mode);
    setNoDownloaderFound(false);
  };

  const handleUrlChange = (value: string) => {
    setUrl(value);
    setNoDownloaderFound(false);
  };

  const handleCreateWithoutDownload = () => {
    const requestedUrl = url.trim();
    if (requestedUrl) createMut.mutate(buildPayload([requestedUrl]));
  };

  const handleSave = () => {
    if (split) {
      if (!splitMut.isPending) splitMut.mutate(buildPayload());
      return;
    }
    if (sourceMode === "file") {
      const trimmedPath = filePath.trim();
      if (trimmedPath) createFromFileMut.mutate({ path: trimmedPath, data: buildPayload() });
      return;
    }

    if (sourceMode === "url") {
      const requestedUrl = url.trim();
      if (requestedUrl)
        createFromUrlMut.mutate({ requestedUrl, data: buildPayload(), downloadMode: urlDownloadMode, scrapeMetadata });
      return;
    }

    createMut.mutate(buildPayload());
  };

  const pending =
    splitMut.isPending || createMut.isPending || createFromFileMut.isPending || createFromUrlMut.isPending;
  const error = (splitMut.error ??
    createMut.error ??
    createFromFileMut.error ??
    createFromUrlMut.error) as Error | null;

  return (
    <EditModal
      title={split ? "Split into a new scene" : "Create Video"}
      open={open}
      onClose={() => {
        if (!pending) onClose();
      }}
    >
      {split && (
        <div className="mb-4 space-y-3">
          <p className="text-sm text-secondary">Creating this scene moves {split.filename} out of the current video.</p>
          <button
            type="button"
            disabled={pending}
            onClick={prepopulate}
            className="rounded border border-border px-3 py-2 text-sm"
          >
            Prepopulate from video
          </button>
        </div>
      )}
      {!split && (
        <FileBackedCreateSource
          mode={sourceMode}
          onModeChange={handleSourceModeChange}
          filePath={filePath}
          onFilePathChange={setFilePath}
          url={url}
          onUrlChange={handleUrlChange}
          urlDownloadMode={urlDownloadMode}
          onUrlDownloadModeChange={setUrlDownloadMode}
          scrapeMetadata={scrapeMetadata}
          onScrapeMetadataChange={setScrapeMetadata}
          noDownloaderFound={noDownloaderFound}
          onCreateWithoutDownload={handleCreateWithoutDownload}
          onDismissNoDownloader={() => setNoDownloaderFound(false)}
          modes={["metadata", "file", "url"]}
          filePlaceholder="C:\\Media\\video.mp4"
          urlPlaceholder="https://example.com/video"
        />
      )}

      <fieldset disabled={pending}>
        <div className="grid grid-cols-2 gap-4">
          <Field label="Title">
            <TextInput value={title} onChange={editTitle} placeholder="Video title" />
          </Field>
          <Field label="Date">
            <IsoDateInput
              value={date}
              onChange={(e) => editDate(e.target.value)}
              className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
            />
          </Field>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label="Studio Code">
            <TextInput value={code} onChange={editCode} placeholder="Studio code" />
          </Field>
          <Field label="Director">
            <TextInput value={director} onChange={editDirector} placeholder="Director" />
          </Field>
        </div>

        <Field label="Details">
          <TextArea value={details} onChange={editDetails} placeholder="Video description" rows={3} />
        </Field>

        <Field label="Studio">
          <StudioSelector value={studioId} onChange={editStudioId} />
        </Field>

        <Field label="URLs">
          <StringListEditor
            values={urls}
            onChange={editUrls}
            placeholder="https://..."
            addLabel="Add URL"
            inputType="url"
          />
        </Field>

        <div className="mb-2 flex flex-wrap items-center gap-4 text-sm">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={isVr}
              onChange={(e) => editIsVr(e.target.checked)}
              className="rounded bg-card border-border"
            />
            VR
          </label>
        </div>

        <Field label="Tags">
          <EntityReferenceMultiSelector
            entityType="tag"
            values={selectedTagIds}
            onChange={editSelectedTagIds}
            placeholder="Search tags..."
          />
        </Field>

        <Field label="Performers">
          <EntityReferenceMultiSelector
            entityType="performer"
            values={selectedPerformerIds}
            onChange={editSelectedPerformerIds}
            placeholder="Search performers..."
          />
        </Field>

        <Field label="Galleries">
          <EntityReferenceMultiSelector
            entityType="gallery"
            values={selectedGalleryIds}
            onChange={editSelectedGalleryIds}
            placeholder="Search galleries..."
          />
        </Field>

        <Field label="Custom Fields">
          <CustomFieldsEditor
            value={customFields}
            onChange={setCustomFields}
            onFieldChange={(key) => touchedCustomFields.current.add(key)}
            onValidityChange={setCustomFieldsValid}
            entityType="video"
          />
        </Field>

        {error && (
          <p role="alert" className="mt-4 text-sm text-red-400">
            {error.message}
          </p>
        )}
        {split ? (
          <div className="mt-6 flex justify-end gap-3">
            <button type="button" onClick={onClose} disabled={pending}>
              Cancel
            </button>
            <button
              type="button"
              onClick={handleSave}
              disabled={pending || !customFieldsValid}
              className="rounded bg-accent px-4 py-2 text-sm text-white disabled:opacity-50"
            >
              {pending ? "Creating…" : "Create scene and split"}
            </button>
          </div>
        ) : (
          <CreateModalActions
            loading={pending}
            disabled={!customFieldsValid}
            onCancel={onClose}
            onSave={handleSave}
            createAnother={createAnother}
            onCreateAnotherChange={setCreateAnother}
          />
        )}
      </fieldset>
    </EditModal>
  );
}
