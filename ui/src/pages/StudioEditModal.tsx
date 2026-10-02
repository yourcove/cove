import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { studios } from "../api/client";
import type { Studio, StudioUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { EntityReferenceMultiSelector, EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { getApiValidationFailureDetail } from "../utils/requestFailure";
import { refreshSavedEntity } from "../utils/refreshSavedEntity";
import { changedUpdateFields } from "../utils/changedUpdateFields";
import { applyFormFields, untouchedFieldUpdates, type FormFieldSetters } from "../utils/rebaseEditForm";

interface Props {
  studio: Studio;
  open: boolean;
  onClose: () => void;
}

function studioFormValues(studio: Studio) {
  return {
    name: studio.name,
    details: studio.details ?? "",
    urls: studio.urls.length > 0 ? studio.urls : [""],
    aliases: studio.aliases.length > 0 ? studio.aliases : [""],
    parentId: studio.parentId ?? undefined,
    selectedTagIds: studio.tags.map((t) => t.id),
    customFields: { ...studio.customFields } as Record<string, unknown>,
    remoteIds: studio.remoteIds.map((remoteId) => ({ ...remoteId })) as RemoteIdValue[],
  };
}

type StudioFormValues = ReturnType<typeof studioFormValues>;

function studioUpdatePayload(values: StudioFormValues): StudioUpdate {
  const clearFields = [!values.details && "details", values.parentId === undefined && "parentId"].filter(
    (field): field is string => Boolean(field),
  );
  return {
    name: values.name,
    details: values.details || undefined,
    parentId: values.parentId,
    urls: values.urls.map((url) => url.trim()).filter(Boolean),
    aliases: values.aliases.map((alias) => alias.trim()).filter(Boolean),
    tagIds: values.selectedTagIds,
    customFields: values.customFields,
    remoteIds: normalizeRemoteIds(values.remoteIds),
    clearFields,
  };
}

export function StudioEditModal({ studio, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(studio.name);
  const [details, setDetails] = useState(studio.details ?? "");
  const [urls, setUrls] = useState(studio.urls.length > 0 ? studio.urls : [""]);
  const [aliases, setAliases] = useState(studio.aliases.length > 0 ? studio.aliases : [""]);
  const [parentId, setParentId] = useState<number | undefined>(studio.parentId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(studio.tags.map((t) => t.id));

  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...studio.customFields });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(studio.remoteIds.map((remoteId) => ({ ...remoteId })));
  const tagProvenanceById = buildTagProvenanceById(studio.tags, studio.fieldProvenance);

  // The studio the form was last filled from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(studio);

  // Fill the form each time the dialog opens. A refetch while it is open keeps the user's edits, and
  // reopening after Cancel discards them.
  const [prevOpen, setPrevOpen] = useState(open);
  const [prevStudioId, setPrevStudioId] = useState(studio.id);
  const openOrStudioChanged = open !== prevOpen || studio.id !== prevStudioId;
  if (openOrStudioChanged) {
    setPrevOpen(open);
    setPrevStudioId(studio.id);
    if (open) {
      setBaseline(studio);
      setName(studio.name);
      setDetails(studio.details ?? "");
      setUrls(studio.urls.length > 0 ? studio.urls : [""]);
      setAliases(studio.aliases.length > 0 ? studio.aliases : [""]);
      setParentId(studio.parentId ?? undefined);
      setSelectedTagIds(studio.tags.map((t) => t.id));
      setCustomFields({ ...studio.customFields });
      setRemoteIds(studio.remoteIds.map((remoteId) => ({ ...remoteId })));
    }
  }

  const currentValues: StudioFormValues = {
    name,
    details,
    urls,
    aliases,
    parentId,
    selectedTagIds,
    customFields,
    remoteIds,
  };
  const formSetters: FormFieldSetters<StudioFormValues> = {
    name: setName,
    details: setDetails,
    urls: setUrls,
    aliases: setAliases,
    parentId: setParentId,
    selectedTagIds: setSelectedTagIds,
    customFields: setCustomFields,
    remoteIds: setRemoteIds,
  };
  // When the studio refetches while the dialog is open, untouched fields follow it and the user's edits stay.
  if (open && !openOrStudioChanged && studio !== baseline) {
    applyFormFields(
      untouchedFieldUpdates(currentValues, studioFormValues(baseline), studioFormValues(studio)),
      formSetters,
    );
    setBaseline(studio);
  }

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: StudioUpdate) => studios.update(studio.id, data),
    onSuccess: async () => {
      queryClient.invalidateQueries({ queryKey: ["studios"] });
      // Close once the saved studio is loaded, so reopening the dialog starts from it.
      await refreshSavedEntity(queryClient, ["studio", studio.id]);
      onClose();
    },
  });
  const handleClose = () => {
    mutation.reset();
    onClose();
  };

  const handleSave = () => {
    mutation.mutate(
      changedUpdateFields(studioUpdatePayload(studioFormValues(baseline)), studioUpdatePayload(currentValues)),
    );
  };

  return (
    <EditModal title={`Edit Studio: ${studio.name}`} open={open} onClose={handleClose}>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <Field label="Name *" fieldProvenance={studio.fieldProvenance} fieldKey="name">
            <TextInput value={name} onChange={setName} placeholder="Studio name" />
          </Field>
          <Field label="Parent Studio" fieldProvenance={studio.fieldProvenance} fieldKey={["parent", "parentId"]}>
            <EntityReferenceSelector
              entityType="studio"
              value={parentId}
              onChange={setParentId}
              placeholder="Search parent studios..."
              excludeIds={[studio.id]}
            />
          </Field>
        </div>

        <Field label="Details" fieldProvenance={studio.fieldProvenance} fieldKey="details">
          <TextArea value={details} onChange={setDetails} placeholder="Studio description" rows={3} />
        </Field>

        <Field label="URLs" fieldProvenance={studio.fieldProvenance} fieldKey="urls">
          <StringListEditor
            values={urls}
            onChange={setUrls}
            placeholder="https://..."
            addLabel="Add URL"
            inputType="url"
          />
        </Field>

        <Field label="Aliases" fieldProvenance={studio.fieldProvenance} fieldKey="aliases">
          <StringListEditor values={aliases} onChange={setAliases} placeholder="Alternate name" addLabel="Add Alias" />
        </Field>

        {/* Tags */}
        <Field label="Tags" fieldProvenance={studio.fieldProvenance} fieldKey="tags">
          <EntityReferenceMultiSelector
            entityType="tag"
            values={selectedTagIds}
            onChange={setSelectedTagIds}
            placeholder="Search tags..."
            selectedProvenanceById={tagProvenanceById}
          />
        </Field>

        <Field label="Remote IDs" fieldProvenance={studio.fieldProvenance} fieldKey="remoteIds">
          <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
        </Field>

        <Field label="Custom Fields" fieldProvenance={studio.fieldProvenance} fieldKey="customFields">
          <CustomFieldsEditor
            value={customFields}
            onChange={setCustomFields}
            onValidityChange={setCustomFieldsValid}
            entityType="studio"
          />
        </Field>
      </div>
      {mutation.error ? (
        <div role="alert" className="rounded border border-red-700 bg-red-900/50 p-2 text-sm text-red-300">
          {getApiValidationFailureDetail(mutation.error)}
        </div>
      ) : null}
      <div className="flex justify-end gap-3 mt-4">
        <button onClick={handleClose} className="px-4 py-2 text-sm text-secondary hover:text-white">
          Cancel
        </button>
        <SaveButton loading={mutation.isPending} disabled={!customFieldsValid} onClick={handleSave} />
      </div>
    </EditModal>
  );
}
