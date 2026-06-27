import { useState, useEffect } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { videos, tagApplications } from "../api/client";
import type { Video, VideoUpdate, TagApplication } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { RatingField } from "../components/Rating";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StudioSelector } from "../components/StudioSelector";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { EntityReferenceMultiSelector, EntityReferenceValue } from "../components/EntityReferenceSelector";
import { getEditableTagIds, getLockedTagIds, mergeTagIds } from "../utils/tags";

interface Props {
  video: Video;
  open: boolean;
  onClose: () => void;
}

export function VideoEditModal({ video, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [title, setTitle] = useState(video.title || "");
  const [code, setCode] = useState(video.code || "");
  const [details, setDetails] = useState(video.details || "");
  const [captions, setCaptions] = useState(video.captions || "");
  const [director, setDirector] = useState(video.director || "");
  const [date, setDate] = useState(video.date || "");
  const [isVr, setIsVr] = useState(video.isVr ?? false);
  const [rating, setRating] = useState<number | undefined>(undefined);
  const [urls, setUrls] = useState<string[]>(video.urls.length > 0 ? video.urls : [""]);
  const addUrl = () => setUrls([...urls, ""]);
  const removeUrl = (i: number) => setUrls(urls.filter((_, idx) => idx !== i));
  const updateUrl = (i: number, val: string) => setUrls(urls.map((u, idx) => idx === i ? val : u));
  const [studioId, setStudioId] = useState<number | undefined>(video.studioId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(getEditableTagIds(video.tags));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(video.performers.map((p) => p.id));
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>(video.galleries.map((g) => g.id));
  const [selectedGroups, setSelectedGroups] = useState<{ groupId: number; videoIndex: number }[]>(
    video.groups.map((g) => ({ groupId: g.id, videoIndex: g.videoIndex }))
  );
  const [contextTagIdsByPerformer, setContextTagIdsByPerformer] = useState<Record<number, number[]>>(() => buildPerformerContextTagIds(video));
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...(video.customFields ?? {}) });
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(video.remoteIds.map((remoteId) => ({ ...remoteId })));

  useEffect(() => {
    setTitle(video.title || "");
    setCode(video.code || "");
    setDetails(video.details || "");
    setCaptions(video.captions || "");
    setDirector(video.director || "");
    setDate(video.date || "");
    setIsVr(video.isVr ?? false);
    setRating(undefined);
    setUrls(video.urls.length > 0 ? video.urls : [""]);
    setStudioId(video.studioId ?? undefined);
    setSelectedTagIds(getEditableTagIds(video.tags));
    setSelectedPerformerIds(video.performers.map((p) => p.id));
    setSelectedGalleryIds(video.galleries.map((g) => g.id));
    setSelectedGroups(video.groups.map((g) => ({ groupId: g.id, videoIndex: g.videoIndex })));
    setContextTagIdsByPerformer(buildPerformerContextTagIds(video));
    setCustomFields({ ...(video.customFields ?? {}) });
    setRemoteIds(video.remoteIds.map((remoteId) => ({ ...remoteId })));
  }, [video]);

  const mutation = useMutation({
    mutationFn: async (data: VideoUpdate) => {
      const updated = await videos.update(video.id, data);
      await syncPerformerContextTags(video.id, video.contextTagApplications ?? [], contextTagIdsByPerformer, selectedPerformerIds);
      return updated;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", video.id] });
      queryClient.invalidateQueries({ queryKey: ["tagapplications"] });
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      onClose();
    },
  });

  const handleSave = () => {
    const urlList = urls.map((u) => u.trim()).filter(Boolean);
    mutation.mutate({
      title: title,
      code: code,
      details: details,
      captions: captions,
      director: director,
      date: date || undefined,
      isVr,
      rating,
      studioId,
      urls: urlList,
      tagIds: selectedTagIds,
      performerIds: selectedPerformerIds,
      galleryIds: selectedGalleryIds,
      groups: selectedGroups,
      customFields,
      remoteIds: normalizeRemoteIds(remoteIds),
    });
  };

  const setPerformerContextTagIds = (performerId: number, tagIds: number[]) => {
    setContextTagIdsByPerformer((current) => ({ ...current, [performerId]: Array.from(new Set(tagIds)) }));
  };

  const setSelectedGroupIds = (groupIds: number[]) => {
    setSelectedGroups(groupIds.map((groupId) => selectedGroups.find((group) => group.groupId === groupId) ?? { groupId, videoIndex: 0 }));
  };

  const lockedTagIds = getLockedTagIds(video.tags);
  const displayedTagIds = mergeTagIds(lockedTagIds, selectedTagIds);
  const tagProvenanceById = buildTagProvenanceById(video.tags, video.fieldProvenance);
  const updateSelectedTagIds = (tagIds: number[]) => {
    const locked = new Set(lockedTagIds);
    setSelectedTagIds(tagIds.filter((tagId) => !locked.has(tagId)));
  };

  return (
    <EditModal title="Edit Video" open={open} onClose={onClose}>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Title" fieldProvenance={video.fieldProvenance} fieldKey="title">
          <TextInput value={title} onChange={setTitle} placeholder="Video title" />
        </Field>
        <Field label="Date" fieldProvenance={video.fieldProvenance} fieldKey="date">
          <input
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Studio Code" fieldProvenance={video.fieldProvenance} fieldKey="code">
          <TextInput value={code} onChange={setCode} placeholder="Studio code" />
        </Field>
        <Field label="Director" fieldProvenance={video.fieldProvenance} fieldKey="director">
          <TextInput value={director} onChange={setDirector} placeholder="Director name" />
        </Field>
      </div>

      <Field label="Details" fieldProvenance={video.fieldProvenance} fieldKey="details">
        <TextArea value={details} onChange={setDetails} placeholder="Video description" />
      </Field>

      <Field label="Captions" fieldProvenance={video.fieldProvenance} fieldKey="captions">
        <TextInput value={captions} onChange={setCaptions} placeholder="Subtitle languages or notes" />
      </Field>

      <div className="grid grid-cols-2 gap-4">
        <RatingField value={rating} onChange={setRating} fieldProvenance={video.fieldProvenance} />
        <Field label="VR" fieldProvenance={video.fieldProvenance} fieldKey="isVr">
          <label className="inline-flex items-center gap-2 rounded border border-border bg-card px-3 py-2 text-sm text-foreground">
            <input type="checkbox" checked={isVr} onChange={(event) => setIsVr(event.target.checked)} className="accent-accent" />
            <span>VR</span>
          </label>
        </Field>
      </div>

      <Field label="Studio" fieldProvenance={video.fieldProvenance} fieldKey={["studio", "studioId"]}>
        <StudioSelector value={studioId} onChange={setStudioId} />
      </Field>

      <Field label="URLs" fieldProvenance={video.fieldProvenance} fieldKey="urls">
        <div className="space-y-1.5">
          {urls.map((url, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <input
                type="url"
                value={url}
                onChange={(e) => updateUrl(i, e.target.value)}
                placeholder="https://..."
                className="flex-1 bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent"
              />
              <button
                type="button"
                onClick={() => removeUrl(i)}
                className="p-1 text-muted hover:text-red-400 transition-colors flex-shrink-0"
                title="Remove URL"
              >×</button>
            </div>
          ))}
        </div>
        <button
          type="button"
          onClick={addUrl}
          className="mt-1.5 flex items-center gap-1 text-xs text-accent hover:text-accent-hover"
        >+ Add URL</button>
      </Field>

      {/* Tags */}
      <Field label="Tags" fieldProvenance={video.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector entityType="tag" values={displayedTagIds} lockedIds={lockedTagIds} onChange={updateSelectedTagIds} placeholder="Search tags..." selectedProvenanceById={tagProvenanceById} />
      </Field>

      {/* Performers */}
      <Field label="Performers" fieldProvenance={video.fieldProvenance} fieldKey="performers">
        <EntityReferenceMultiSelector entityType="performer" values={selectedPerformerIds} onChange={setSelectedPerformerIds} placeholder="Search performers..." />
      </Field>

      {selectedPerformerIds.length > 0 ? (
        <Field label="Performer Occurrence Tags" fieldProvenance={video.fieldProvenance} fieldKey="contextTags">
          <div className="space-y-3 rounded-lg border border-border bg-surface/40 p-3">
            {selectedPerformerIds.map((performerId) => {
              const tagIds = contextTagIdsByPerformer[performerId] ?? [];

              return (
                <div key={performerId} className="rounded-lg border border-border bg-card/70 p-3">
                  <div className="mb-2 flex items-center justify-between gap-3">
                    <div className="min-w-0 text-sm font-medium text-foreground"><EntityReferenceValue entityType="performer" value={performerId} /></div>
                    <div className="text-xs text-muted">{tagIds.length} tag{tagIds.length === 1 ? "" : "s"}</div>
                  </div>
                  <EntityReferenceMultiSelector
                    entityType="tag"
                    values={tagIds}
                    onChange={(nextTagIds) => setPerformerContextTagIds(performerId, nextTagIds)}
                    placeholder="Search tags for this occurrence..."
                    emptyMessage="No tags found"
                    inputClassName="w-full rounded border border-border bg-card px-3 py-1.5 text-sm text-foreground outline-none focus:border-accent"
                  />
                </div>
              );
            })}
          </div>
        </Field>
      ) : null}

      <Field label="Galleries" fieldProvenance={video.fieldProvenance} fieldKey="galleries">
        <EntityReferenceMultiSelector entityType="gallery" values={selectedGalleryIds} onChange={setSelectedGalleryIds} placeholder="Search galleries..." />
      </Field>

      {/* Groups */}
      <Field label="Groups" fieldProvenance={video.fieldProvenance} fieldKey="groups">
        <div className="space-y-1.5 mb-2">
          {selectedGroups.map((sg) => {
            return (
              <div key={sg.groupId} className="flex items-center gap-2">
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-orange-900 text-orange-300">
                  <EntityReferenceValue entityType="group" value={sg.groupId} />
                  <button onClick={() => setSelectedGroups(selectedGroups.filter((g) => g.groupId !== sg.groupId))} className="hover:text-white">×</button>
                </span>
                <label className="flex items-center gap-1 text-xs text-secondary">
                  Video #
                  <input
                    type="number"
                    min={0}
                    value={sg.videoIndex}
                    onChange={(e) => setSelectedGroups(selectedGroups.map((g) => g.groupId === sg.groupId ? { ...g, videoIndex: Number(e.target.value) || 0 } : g))}
                    className="w-16 bg-card border border-border rounded px-2 py-0.5 text-xs text-foreground focus:outline-none focus:border-accent"
                  />
                </label>
              </div>
            );
          })}
        </div>
        <EntityReferenceMultiSelector entityType="group" values={selectedGroups.map((group) => group.groupId)} onChange={setSelectedGroupIds} placeholder="Search groups..." />
      </Field>

      <Field label="Remote IDs" fieldProvenance={video.fieldProvenance} fieldKey="remoteIds">
        <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
      </Field>

      <Field label="Custom Fields" fieldProvenance={video.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="video" />
      </Field>

      {mutation.error && (
        <div className="bg-red-900/50 border border-red-700 text-red-300 rounded p-2 mb-4 text-sm">
          {(mutation.error as Error).message}
        </div>
      )}

      <div className="flex justify-end gap-3">
        <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={mutation.isPending} onClick={handleSave} />
      </div>
    </EditModal>
  );
}

function buildPerformerContextTagIds(video: Video): Record<number, number[]> {
  const result: Record<number, number[]> = {};
  for (const application of video.contextTagApplications ?? []) {
    if (application.contextType !== "performer" || application.contextId == null) {
      continue;
    }

    result[application.contextId] = [...(result[application.contextId] ?? []), application.tag.id];
  }

  return result;
}

async function syncPerformerContextTags(videoId: number, existingApplications: TagApplication[], desiredByPerformer: Record<number, number[]>, selectedPerformerIds: number[]) {
  const selectedPerformers = new Set(selectedPerformerIds);
  const desiredKeys = new Set<string>();

  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      desiredKeys.add(`${performerId}:${tagId}`);
    }
  }

  const existingContextApplications = existingApplications.filter((application) => application.contextType === "performer" && application.contextId != null);

  for (const application of existingContextApplications) {
    const key = `${application.contextId}:${application.tag.id}`;
    if (!desiredKeys.has(key)) {
      await tagApplications.delete(application.id);
    }
  }

  const existingKeys = new Set(existingContextApplications.map((application) => `${application.contextId}:${application.tag.id}`));
  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      const key = `${performerId}:${tagId}`;
      if (existingKeys.has(key)) {
        continue;
      }

      await tagApplications.create({
        hostType: "video",
        hostId: videoId,
        contextType: "performer",
        contextId: performerId,
        tagId,
        sourceKey: "user",
      });
    }
  }
}

