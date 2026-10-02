import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { texts } from "../api/client";
import type { VideoGroupInput, TextDocument, TextUpdate } from "../api/types";
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

interface Props {
  text: TextDocument;
  onSaved: () => void;
}

function textFormValues(text: TextDocument) {
  return {
    title: text.title ?? "",
    code: text.code ?? "",
    details: text.details ?? "",
    date: text.date ?? "",
    studioId: text.studioId ?? undefined,
    urls: text.urls.length > 0 ? text.urls : [""],
    customFields: { ...text.customFields } as Record<string, unknown>,
    selectedTagIds: text.tags.map((tag) => tag.id),
    selectedPerformerIds: text.performers.map((performer) => performer.id),
    selectedGroups: text.groups.map((group) => ({ groupId: group.id, videoIndex: 0 })) as VideoGroupInput[],
    contextTagIdsByPerformer: buildPerformerContextTagIds(text.contextTagApplications),
  };
}

type TextFormValues = ReturnType<typeof textFormValues>;

function textUpdatePayload(values: TextFormValues): TextUpdate {
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

export function TextEditPanel({ text, onSaved }: Props) {
  const queryClient = useQueryClient();
  const inputCls =
    "w-full rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none";

  const [title, setTitle] = useState(text.title ?? "");
  const [code, setCode] = useState(text.code ?? "");
  const [details, setDetails] = useState(text.details ?? "");
  const [date, setDate] = useState(text.date ?? "");
  const [studioId, setStudioId] = useState<number | undefined>(text.studioId ?? undefined);
  const [urls, setUrls] = useState<string[]>(text.urls.length > 0 ? text.urls : [""]);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...text.customFields });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(text.tags.map((tag) => tag.id));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(
    text.performers.map((performer) => performer.id),
  );
  const [contextTagIdsByPerformer, setContextTagIdsByPerformer] = useState<Record<number, number[]>>(() =>
    buildPerformerContextTagIds(text.contextTagApplications),
  );
  const [selectedGroups, setSelectedGroups] = useState<VideoGroupInput[]>(
    text.groups.map((group) => ({ groupId: group.id, videoIndex: 0 })),
  );
  // The text the form was last filled from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(text);
  const currentValues: TextFormValues = {
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
  const formSetters: FormFieldSetters<TextFormValues> = {
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
  // When the text refetches (after Mark organized, a scrape or a finished job), untouched fields follow it
  // and the user's edits stay.
  if (text !== baseline) {
    const next = textFormValues(text);
    applyFormFields(
      text.id === baseline.id ? untouchedFieldUpdates(currentValues, textFormValues(baseline), next) : next,
      formSetters,
    );
    setBaseline(text);
  }

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (data: TextUpdate) => {
      await texts.update(text.id, data);
      await syncPerformerContextTags(
        "text",
        text.id,
        text.contextTagApplications ?? [],
        // Apply only the user's context tag and performer edits, so ones changed elsewhere are kept.
        applyPerformerContextTagEdits(
          buildPerformerContextTagIds(text.contextTagApplications),
          buildPerformerContextTagIds(baseline.contextTagApplications),
          contextTagIdsByPerformer,
        ),
        data.performerIds ?? text.performers.map((performer) => performer.id),
      );
      return texts.get(text.id);
    },
    onSuccess: (saved) => {
      // Reopening Edit before the refetch lands must start from the saved text.
      queryClient.setQueryData(["text", text.id], saved);
      queryClient.invalidateQueries({ queryKey: ["text", text.id] });
      queryClient.invalidateQueries({ queryKey: ["texts"] });
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
  const tagProvenanceById = buildTagProvenanceById(text.tags, text.fieldProvenance);

  const handleSave = () => {
    mutation.mutate(changedUpdateFields(textUpdatePayload(textFormValues(baseline)), textUpdatePayload(currentValues)));
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Title" fieldProvenance={text.fieldProvenance} fieldKey="title">
          <input value={title} onChange={(event) => setTitle(event.target.value)} className={inputCls} />
        </Field>
        <Field label="Date" fieldProvenance={text.fieldProvenance} fieldKey="date">
          <IsoDateInput value={date} onChange={(event) => setDate(event.target.value)} className={inputCls} />
        </Field>
      </div>

      <Field label="Code" fieldProvenance={text.fieldProvenance} fieldKey="code">
        <input value={code} onChange={(event) => setCode(event.target.value)} className={inputCls} />
      </Field>

      <Field label="Description" fieldProvenance={text.fieldProvenance} fieldKey="details">
        <textarea value={details} onChange={(event) => setDetails(event.target.value)} rows={4} className={inputCls} />
      </Field>

      <Field label="Studio" fieldProvenance={text.fieldProvenance} fieldKey={["studio", "studioId"]}>
        <StudioSelector value={studioId} onChange={setStudioId} placeholder="Search studios..." />
      </Field>

      <Field label="URLs" fieldProvenance={text.fieldProvenance} fieldKey="urls">
        <StringListEditor
          values={urls}
          onChange={setUrls}
          placeholder="https://..."
          addLabel="Add URL"
          inputType="url"
        />
      </Field>

      <Field label="Tags" fieldProvenance={text.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector
          entityType="tag"
          values={selectedTagIds}
          onChange={setSelectedTagIds}
          placeholder="Search tags..."
          inputClassName={inputCls}
          selectedProvenanceById={tagProvenanceById}
        />
      </Field>

      <Field label="Performers" fieldProvenance={text.fieldProvenance} fieldKey="performers">
        <EntityReferenceMultiSelector
          entityType="performer"
          values={selectedPerformerIds}
          onChange={setSelectedPerformerIds}
          placeholder="Search performers..."
          inputClassName={inputCls}
        />
      </Field>

      {selectedPerformerIds.length > 0 ? (
        <Field label="Performer Occurrence Tags" fieldProvenance={text.fieldProvenance} fieldKey="contextTags">
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

      <Field label="Groups" fieldProvenance={text.fieldProvenance} fieldKey="groups">
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

      <Field label="Custom Fields" fieldProvenance={text.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor
          value={customFields}
          onChange={setCustomFields}
          onValidityChange={setCustomFieldsValid}
          entityType="text"
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
