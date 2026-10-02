import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { audios } from "../api/client";
import type { Audio, AudioUpdate, VideoGroupInput } from "../api/types";
import { Field } from "../components/EditModal";
import {
  PerformerContextTagEditor,
  applyPerformerContextTagEdits,
  buildPerformerContextTagIds,
  syncPerformerContextTags,
} from "../components/PerformerContextTags";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { changedUpdateFields } from "../utils/changedUpdateFields";
import { applyFormFields, untouchedFieldUpdates, type FormFieldSetters } from "../utils/rebaseEditForm";
import { IsoDateInput } from "../components/IsoDateInput";
import { EntityReferenceMultiSelector, EntityReferenceValue } from "../components/EntityReferenceSelector";
import { getEditableTagIds, getLockedTagIds, mergeTagIds } from "../utils/tags";

interface Props {
  audio: Audio;
  onSaved: () => void;
}

function audioFormValues(audio: Audio) {
  return {
    title: audio.title ?? "",
    code: audio.code ?? "",
    details: audio.details ?? "",
    date: audio.date ?? "",
    studioId: audio.studioId ?? undefined,
    urls: audio.urls.length > 0 ? audio.urls : [""],
    customFields: { ...audio.customFields } as Record<string, unknown>,
    selectedTagIds: getEditableTagIds(audio.tags),
    selectedPerformerIds: audio.performers.map((performer) => performer.id),
    selectedGroups: audio.groups.map((group) => ({ groupId: group.id, videoIndex: 0 })) as VideoGroupInput[],
    contextTagIdsByPerformer: buildPerformerContextTagIds(audio.contextTagApplications),
  };
}

type AudioFormValues = ReturnType<typeof audioFormValues>;

function audioUpdatePayload(values: AudioFormValues): AudioUpdate {
  return {
    title: values.title.trim(),
    code: values.code.trim(),
    details: values.details.trim(),
    studioId: values.studioId,
    date: values.date,
    urls: values.urls.map((url) => url.trim()).filter(Boolean),
    tagIds: values.selectedTagIds,
    performerIds: values.selectedPerformerIds,
    customFields: values.customFields,
    groupIds: values.selectedGroups,
    clearFields: values.studioId === undefined ? ["studioId"] : [],
  };
}

export function AudioEditPanel({ audio, onSaved }: Props) {
  const queryClient = useQueryClient();
  const inputCls =
    "w-full rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none";

  const [title, setTitle] = useState(audio.title ?? "");
  const [code, setCode] = useState(audio.code ?? "");
  const [details, setDetails] = useState(audio.details ?? "");
  const [date, setDate] = useState(audio.date ?? "");
  const [studioId, setStudioId] = useState<number | undefined>(audio.studioId ?? undefined);
  const [urls, setUrls] = useState<string[]>(audio.urls.length > 0 ? audio.urls : [""]);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...audio.customFields });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(getEditableTagIds(audio.tags));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(
    audio.performers.map((performer) => performer.id),
  );
  const [contextTagIdsByPerformer, setContextTagIdsByPerformer] = useState<Record<number, number[]>>(() =>
    buildPerformerContextTagIds(audio.contextTagApplications),
  );
  const [selectedGroups, setSelectedGroups] = useState<VideoGroupInput[]>(
    audio.groups.map((group) => ({ groupId: group.id, videoIndex: 0 })),
  );
  // The audio the form was last filled from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(audio);
  const currentValues: AudioFormValues = {
    title,
    code,
    details,
    date,
    studioId,
    urls,
    customFields,
    selectedTagIds,
    selectedPerformerIds,
    selectedGroups,
    contextTagIdsByPerformer,
  };
  const formSetters: FormFieldSetters<AudioFormValues> = {
    title: setTitle,
    code: setCode,
    details: setDetails,
    date: setDate,
    studioId: setStudioId,
    urls: setUrls,
    customFields: setCustomFields,
    selectedTagIds: setSelectedTagIds,
    selectedPerformerIds: setSelectedPerformerIds,
    selectedGroups: setSelectedGroups,
    contextTagIdsByPerformer: setContextTagIdsByPerformer,
  };
  // When the audio refetches (after Mark organized, a scrape or a finished job), untouched fields follow it
  // and the user's edits stay.
  if (audio !== baseline) {
    const next = audioFormValues(audio);
    applyFormFields(
      audio.id === baseline.id ? untouchedFieldUpdates(currentValues, audioFormValues(baseline), next) : next,
      formSetters,
    );
    setBaseline(audio);
  }

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (data: AudioUpdate) => {
      await audios.update(audio.id, data);
      await syncPerformerContextTags(
        "audio",
        audio.id,
        audio.contextTagApplications ?? [],
        // Apply only the user's context tag and performer edits, so ones changed elsewhere are kept.
        applyPerformerContextTagEdits(
          buildPerformerContextTagIds(audio.contextTagApplications),
          buildPerformerContextTagIds(baseline.contextTagApplications),
          contextTagIdsByPerformer,
        ),
        data.performerIds ?? audio.performers.map((performer) => performer.id),
      );
      return audios.get(audio.id);
    },
    onSuccess: (saved) => {
      // Reopening Edit before the refetch lands must start from the saved audio.
      queryClient.setQueryData(["audio", audio.id], saved);
      queryClient.invalidateQueries({ queryKey: ["audio", audio.id] });
      queryClient.invalidateQueries({ queryKey: ["audios"] });
      onSaved();
    },
  });

  const setSelectedGroupIds = (groupIds: number[]) => {
    setSelectedGroups(
      groupIds.map(
        (groupId) => selectedGroups.find((group) => group.groupId === groupId) ?? { groupId, videoIndex: 0 },
      ),
    );
  };

  const lockedTagIds = getLockedTagIds(audio.tags);
  const displayedTagIds = mergeTagIds(lockedTagIds, selectedTagIds);
  const tagProvenanceById = buildTagProvenanceById(audio.tags, audio.fieldProvenance);
  const updateSelectedTagIds = (tagIds: number[]) => {
    const locked = new Set(lockedTagIds);
    setSelectedTagIds(tagIds.filter((tagId) => !locked.has(tagId)));
  };

  const handleSave = () => {
    mutation.mutate(
      changedUpdateFields(audioUpdatePayload(audioFormValues(baseline)), audioUpdatePayload(currentValues)),
    );
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Title" fieldProvenance={audio.fieldProvenance} fieldKey="title">
          <input value={title} onChange={(event) => setTitle(event.target.value)} className={inputCls} />
        </Field>
        <Field label="Date" fieldProvenance={audio.fieldProvenance} fieldKey="date">
          <IsoDateInput value={date} onChange={(event) => setDate(event.target.value)} className={inputCls} />
        </Field>
      </div>

      <Field label="Code" fieldProvenance={audio.fieldProvenance} fieldKey="code">
        <input value={code} onChange={(event) => setCode(event.target.value)} className={inputCls} />
      </Field>

      <Field label="Details" fieldProvenance={audio.fieldProvenance} fieldKey="details">
        <textarea value={details} onChange={(event) => setDetails(event.target.value)} rows={4} className={inputCls} />
      </Field>

      <Field label="Studio" fieldProvenance={audio.fieldProvenance} fieldKey={["studio", "studioId"]}>
        <StudioSelector value={studioId} onChange={setStudioId} placeholder="Search studios..." />
      </Field>

      <Field label="URLs" fieldProvenance={audio.fieldProvenance} fieldKey="urls">
        <StringListEditor
          values={urls}
          onChange={setUrls}
          placeholder="https://..."
          addLabel="Add URL"
          inputType="url"
        />
      </Field>

      <Field label="Tags" fieldProvenance={audio.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector
          entityType="tag"
          values={displayedTagIds}
          lockedIds={lockedTagIds}
          onChange={updateSelectedTagIds}
          placeholder="Search tags..."
          inputClassName={inputCls}
          selectedProvenanceById={tagProvenanceById}
        />
      </Field>

      <Field label="Performers" fieldProvenance={audio.fieldProvenance} fieldKey="performers">
        <EntityReferenceMultiSelector
          entityType="performer"
          values={selectedPerformerIds}
          onChange={setSelectedPerformerIds}
          placeholder="Search performers..."
          inputClassName={inputCls}
        />
      </Field>

      {selectedPerformerIds.length > 0 ? (
        <Field label="Performer Occurrence Tags" fieldProvenance={audio.fieldProvenance} fieldKey="contextTags">
          <PerformerContextTagEditor
            performerIds={selectedPerformerIds}
            contextTagIdsByPerformer={contextTagIdsByPerformer}
            onChange={(performerId, tagIds) =>
              setContextTagIdsByPerformer((current) => ({ ...current, [performerId]: tagIds }))
            }
            inputClassName={inputCls}
          />
        </Field>
      ) : null}

      <Field label="Groups" fieldProvenance={audio.fieldProvenance} fieldKey="groups">
        <div className="mb-1 flex flex-wrap gap-1.5">
          {selectedGroups.map((group) => (
            <span
              key={group.groupId}
              className="inline-flex items-center gap-1 rounded-full bg-emerald-500/10 px-2 py-0.5 text-xs text-emerald-300"
            >
              <EntityReferenceValue entityType="group" value={group.groupId} />
              <button
                type="button"
                onClick={() => setSelectedGroups(selectedGroups.filter((item) => item.groupId !== group.groupId))}
                className="hover:text-foreground"
              >
                x
              </button>
            </span>
          ))}
        </div>
        <EntityReferenceMultiSelector
          entityType="group"
          values={selectedGroups.map((group) => group.groupId)}
          onChange={setSelectedGroupIds}
          placeholder="Search groups..."
          inputClassName={inputCls}
        />
      </Field>

      <Field label="Custom Fields" fieldProvenance={audio.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor
          value={customFields}
          onChange={setCustomFields}
          onValidityChange={setCustomFieldsValid}
          entityType="audio"
        />
      </Field>

      {mutation.error ? (
        <div className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">
          {(mutation.error as Error).message}
        </div>
      ) : null}

      <div className="flex justify-end gap-3 pt-2">
        <button
          type="button"
          onClick={onSaved}
          className="px-4 py-2 text-sm text-secondary transition hover:text-foreground"
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={handleSave}
          disabled={mutation.isPending || !customFieldsValid}
          className="rounded-lg bg-accent px-4 py-2 text-sm text-white transition hover:bg-accent-hover disabled:opacity-60"
        >
          {mutation.isPending ? "Saving..." : "Save"}
        </button>
      </div>
    </div>
  );
}
