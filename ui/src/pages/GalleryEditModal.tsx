import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { galleries } from "../api/client";
import type { Gallery, GalleryUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { IsoDateInput } from "../components/IsoDateInput";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { invalidateVideosForGalleryLinkChange } from "../utils/galleryVideoLinks";
import { refreshSavedEntity } from "../utils/refreshSavedEntity";
import { changedUpdateFields } from "../utils/changedUpdateFields";
import { untouchedFieldUpdates } from "../utils/rebaseEditForm";

interface Props {
  gallery: Gallery;
  open: boolean;
  onClose: () => void;
}

function buildFormState(gallery: Gallery) {
  return {
    title: gallery.title ?? "",
    code: gallery.code ?? "",
    date: gallery.date ?? "",
    details: gallery.details ?? "",
    photographer: gallery.photographer ?? "",
    studioId: gallery.studioId,
    urls: gallery.urls.length > 0 ? gallery.urls : [""],
    tagIds: gallery.tags.map((t) => t.id),
    performerIds: gallery.performers.map((p) => p.id),
    videoIds: gallery.videoIds,
    customFields: { ...gallery.customFields } as Record<string, unknown>,
  };
}

type GalleryForm = ReturnType<typeof buildFormState>;

function buildUpdatePayload(form: GalleryForm): GalleryUpdate {
  const clearFields = [!form.date && "date", form.studioId === undefined && "studioId"].filter(
    (field): field is string => Boolean(field),
  );
  return {
    title: form.title,
    code: form.code,
    date: form.date || undefined,
    details: form.details,
    photographer: form.photographer,
    studioId: form.studioId,
    urls: form.urls.map((url) => url.trim()).filter(Boolean),
    tagIds: form.tagIds,
    performerIds: form.performerIds,
    videoIds: form.videoIds,
    customFields: form.customFields,
    clearFields,
  };
}

export function GalleryEditModal({ gallery, open, onClose }: Props) {
  const qc = useQueryClient();
  // The gallery this edit started from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(gallery);
  const [form, setForm] = useState(() => buildFormState(gallery));
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  // The detail page keeps this modal mounted while the gallery refetches, so start every edit from the
  // latest gallery rather than the one first loaded.
  const [prevOpen, setPrevOpen] = useState(open);
  const [prevGalleryId, setPrevGalleryId] = useState(gallery.id);
  const openOrGalleryChanged = open !== prevOpen || gallery.id !== prevGalleryId;
  if (openOrGalleryChanged) {
    setPrevOpen(open);
    setPrevGalleryId(gallery.id);
    if (open) {
      setBaseline(gallery);
      setForm(buildFormState(gallery));
      setCustomFieldsValid(true);
    }
  }
  // When the gallery refetches while the dialog is open, untouched fields follow it and the user's edits stay.
  if (open && !openOrGalleryChanged && gallery !== baseline) {
    setForm((current) => ({
      ...current,
      ...untouchedFieldUpdates(current, buildFormState(baseline), buildFormState(gallery)),
    }));
    setBaseline(gallery);
  }
  const tagProvenanceById = buildTagProvenanceById(gallery.tags, gallery.fieldProvenance);
  // Seed chip labels from the loaded gallery so selected chips don't each re-fetch their name by id.
  const tagSeedOptions = gallery.tags.map((tag) => ({ id: tag.id, label: tag.name }));
  const performerSeedOptions = gallery.performers.map((performer) => ({
    id: performer.id,
    label: performer.name,
    secondaryLabel: performer.disambiguation ? `(${performer.disambiguation})` : undefined,
  }));

  const mutation = useMutation({
    mutationFn: (data: GalleryUpdate) => galleries.update(gallery.id, data),
    onSuccess: async (_updated, data) => {
      // Links may have changed elsewhere since the edit started, so compare against both copies.
      if (data.videoIds) {
        invalidateVideosForGalleryLinkChange(qc, baseline.videoIds, data.videoIds);
        invalidateVideosForGalleryLinkChange(qc, gallery.videoIds, data.videoIds);
      }
      qc.invalidateQueries({ queryKey: ["gallery-videos", gallery.id] });
      qc.invalidateQueries({ queryKey: ["gallery-like-count", gallery.id] });
      qc.invalidateQueries({ queryKey: ["galleries"] });
      // Close once the saved gallery is loaded, so reopening the dialog starts from it.
      await refreshSavedEntity(qc, ["gallery", gallery.id]);
      onClose();
    },
  });

  const save = () => {
    mutation.mutate(changedUpdateFields(buildUpdatePayload(buildFormState(baseline)), buildUpdatePayload(form)));
  };

  return (
    <EditModal title={`Edit Gallery: ${gallery.title || "Untitled"}`} open={open} onClose={onClose}>
      <div className="grid grid-cols-2 gap-4">
        <div className="col-span-2">
          <Field label="Title" fieldProvenance={gallery.fieldProvenance} fieldKey="title">
            <TextInput value={form.title} onChange={(v) => setForm({ ...form, title: v })} />
          </Field>
        </div>
        <Field label="Studio Code" fieldProvenance={gallery.fieldProvenance} fieldKey="code">
          <TextInput value={form.code} onChange={(v) => setForm({ ...form, code: v })} />
        </Field>
        <Field label="Date" fieldProvenance={gallery.fieldProvenance} fieldKey="date">
          <IsoDateInput
            value={form.date}
            onChange={(event) => setForm({ ...form, date: event.target.value })}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
        <Field label="Photographer" fieldProvenance={gallery.fieldProvenance} fieldKey="photographer">
          <TextInput value={form.photographer} onChange={(v) => setForm({ ...form, photographer: v })} />
        </Field>
        <Field label="Studio" fieldProvenance={gallery.fieldProvenance} fieldKey={["studio", "studioId"]}>
          <StudioSelector value={form.studioId} onChange={(studioId) => setForm({ ...form, studioId })} />
        </Field>
      </div>
      <Field label="Details" fieldProvenance={gallery.fieldProvenance} fieldKey="details">
        <TextArea value={form.details} onChange={(v) => setForm({ ...form, details: v })} rows={3} />
      </Field>
      <Field label="URLs" fieldProvenance={gallery.fieldProvenance} fieldKey="urls">
        <StringListEditor
          values={form.urls}
          onChange={(value) => setForm({ ...form, urls: value })}
          placeholder="https://..."
          addLabel="Add URL"
          inputType="url"
        />
      </Field>

      {/* Tags picker */}
      <Field label="Tags" fieldProvenance={gallery.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector
          entityType="tag"
          values={form.tagIds}
          onChange={(tagIds) => setForm({ ...form, tagIds })}
          placeholder="Search tags..."
          selectedProvenanceById={tagProvenanceById}
          seedOptions={tagSeedOptions}
        />
      </Field>

      {/* Performers picker */}
      <Field label="Performers" fieldProvenance={gallery.fieldProvenance} fieldKey="performers">
        <EntityReferenceMultiSelector
          entityType="performer"
          values={form.performerIds}
          onChange={(performerIds) => setForm({ ...form, performerIds })}
          placeholder="Search performers..."
          seedOptions={performerSeedOptions}
        />
      </Field>

      <Field label="Videos" fieldProvenance={gallery.fieldProvenance} fieldKey="videos">
        <EntityReferenceMultiSelector
          entityType="video"
          values={form.videoIds}
          onChange={(videoIds) => setForm({ ...form, videoIds })}
          placeholder="Search videos..."
        />
      </Field>

      <Field label="Custom Fields" fieldProvenance={gallery.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor
          value={form.customFields}
          onChange={(customFields) => setForm((current) => ({ ...current, customFields }))}
          onValidityChange={setCustomFieldsValid}
          entityType="gallery"
        />
      </Field>

      <div className="flex justify-end mt-4">
        <SaveButton loading={mutation.isPending} disabled={!customFieldsValid} onClick={save} />
      </div>
    </EditModal>
  );
}
