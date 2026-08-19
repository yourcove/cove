import { useState, useEffect } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { studios } from "../api/client";
import type { Studio, StudioUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { EntityReferenceMultiSelector, EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { getApiValidationFailureDetail } from "../utils/requestFailure";

interface Props {
  studio: Studio;
  open: boolean;
  onClose: () => void;
}

export function StudioEditModal({ studio, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(studio.name);
  const [details, setDetails] = useState(studio.details ?? "");
  const [urls, setUrls] = useState(studio.urls.length > 0 ? studio.urls : [""]);
  const [aliases, setAliases] = useState(studio.aliases.length > 0 ? studio.aliases : [""]);
  const [parentId, setParentId] = useState<number | undefined>(studio.parentId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(studio.tags.map((t) => t.id));

  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...(studio.customFields ?? {}) });
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(studio.remoteIds.map((remoteId) => ({ ...remoteId })));
  const tagProvenanceById = buildTagProvenanceById(studio.tags, studio.fieldProvenance);

  useEffect(() => {
    setName(studio.name);
    setDetails(studio.details ?? "");
    setUrls(studio.urls.length > 0 ? studio.urls : [""]);
    setAliases(studio.aliases.length > 0 ? studio.aliases : [""]);
    setParentId(studio.parentId ?? undefined);
    setSelectedTagIds(studio.tags.map((t) => t.id));
    setCustomFields({ ...(studio.customFields ?? {}) });
    setRemoteIds(studio.remoteIds.map((remoteId) => ({ ...remoteId })));
  }, [studio]);

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: StudioUpdate) => studios.update(studio.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studio", studio.id] });
      queryClient.invalidateQueries({ queryKey: ["studios"] });
      onClose();
    },
  });
  const handleClose = () => {
    mutation.reset();
    onClose();
  };

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    const aliasList = aliases.map((alias) => alias.trim()).filter(Boolean);
    const clearFields = [
      !details && "details",
      parentId === undefined && "parentId",
    ].filter((field): field is string => Boolean(field));
    mutation.mutate({
      name,
      details: details || undefined,
      parentId,
      urls: urlList,
      aliases: aliasList,
      tagIds: selectedTagIds,
      customFields,
      remoteIds: normalizeRemoteIds(remoteIds),
      clearFields,
    });
  };

  return (
    <EditModal title={`Edit Studio: ${studio.name}`} open={open} onClose={handleClose}>
      <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <Field label="Name *" fieldProvenance={studio.fieldProvenance} fieldKey="name">
          <TextInput value={name} onChange={setName} placeholder="Studio name" />
        </Field>
        <Field label="Parent Studio" fieldProvenance={studio.fieldProvenance} fieldKey={["parent", "parentId"]}>
          <EntityReferenceSelector entityType="studio" value={parentId} onChange={setParentId} placeholder="Search parent studios..." excludeIds={[studio.id]} />
        </Field>
      </div>

      <Field label="Details" fieldProvenance={studio.fieldProvenance} fieldKey="details">
        <TextArea value={details} onChange={setDetails} placeholder="Studio description" rows={3} />
      </Field>

      <Field label="URLs" fieldProvenance={studio.fieldProvenance} fieldKey="urls">
        <StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      <Field label="Aliases" fieldProvenance={studio.fieldProvenance} fieldKey="aliases">
        <StringListEditor values={aliases} onChange={setAliases} placeholder="Alternate name" addLabel="Add Alias" />
      </Field>

      {/* Tags */}
      <Field label="Tags" fieldProvenance={studio.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector entityType="tag" values={selectedTagIds} onChange={setSelectedTagIds} placeholder="Search tags..." selectedProvenanceById={tagProvenanceById} />
      </Field>

      <Field label="Remote IDs" fieldProvenance={studio.fieldProvenance} fieldKey="remoteIds">
        <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
      </Field>

      <Field label="Custom Fields" fieldProvenance={studio.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="studio" />
      </Field>
  </div>
      {mutation.error ? (
        <div role="alert" className="rounded border border-red-700 bg-red-900/50 p-2 text-sm text-red-300">
          {getApiValidationFailureDetail(mutation.error)}
        </div>
      ) : null}
      <div className="flex justify-end gap-3 mt-4">
        <button onClick={handleClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={mutation.isPending} onClick={handleSave} />
      </div>
    </EditModal>
  );
}
