import { useState, useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { studios, tags as tagsApi, entityImages } from "../api/client";
import type { Studio, StudioUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { ImageInput } from "../components/ImageInput";
import { InteractiveRatingField } from "../components/Rating";
import { CustomFieldsEditor } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";

interface Props {
  studio: Studio;
  open: boolean;
  onClose: () => void;
}

export function StudioEditModal({ studio, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(studio.name);
  const [details, setDetails] = useState(studio.details ?? "");
  const [rating, setRating] = useState<number | undefined>(studio.rating ?? undefined);
  const [ignoreAutoTag, setIgnoreAutoTag] = useState(studio.ignoreAutoTag);
  const [urls, setUrls] = useState(studio.urls.length > 0 ? studio.urls : [""]);
  const [aliases, setAliases] = useState(studio.aliases.length > 0 ? studio.aliases : [""]);
  const [parentId, setParentId] = useState<number | undefined>(studio.parentId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(studio.tags.map((t) => t.id));

  // Tag search
  const [tagSearch, setTagSearch] = useState("");
  const [customFields, setCustomFields] = useState<Record<string, string>>(
    Object.fromEntries(Object.entries(studio.customFields ?? {}).map(([k, v]) => [k, String(v ?? "")]))
  );
  const { data: allTags } = useQuery({
    queryKey: ["tags-all"],
    queryFn: () => tagsApi.find({ perPage: 500, sort: "name", direction: "asc" }),
  });

  // Parent studio list
  const { data: allStudios } = useQuery({
    queryKey: ["studios-all"],
    queryFn: () => studios.find({ perPage: 500, sort: "name", direction: "asc" }),
  });

  useEffect(() => {
    setName(studio.name);
    setDetails(studio.details ?? "");
    setRating(studio.rating ?? undefined);
    setIgnoreAutoTag(studio.ignoreAutoTag);
    setUrls(studio.urls.length > 0 ? studio.urls : [""]);
    setAliases(studio.aliases.length > 0 ? studio.aliases : [""]);
    setParentId(studio.parentId ?? undefined);
    setSelectedTagIds(studio.tags.map((t) => t.id));
    setCustomFields(Object.fromEntries(Object.entries(studio.customFields ?? {}).map(([k, v]) => [k, String(v ?? "")])));
  }, [studio]);

  const mutation = useMutation({
    mutationFn: (data: StudioUpdate) => studios.update(studio.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studio", studio.id] });
      queryClient.invalidateQueries({ queryKey: ["studios"] });
      onClose();
    },
  });

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    const aliasList = aliases.map((alias) => alias.trim()).filter(Boolean);
    mutation.mutate({
      name,
      details: details || undefined,
      rating,
      ignoreAutoTag,
      parentId,
      urls: urlList,
      aliases: aliasList,
      tagIds: selectedTagIds,
      customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
    });
  };

  const filteredTags = allTags?.items.filter(
    (t) => !selectedTagIds.includes(t.id) && t.name.toLowerCase().includes(tagSearch.toLowerCase())
  ) ?? [];
  const selectedTags = allTags?.items.filter((t) => selectedTagIds.includes(t.id)) ?? studio.tags;

  // Exclude self from parent studio options
  const parentStudioOptions = allStudios?.items.filter((s) => s.id !== studio.id) ?? [];

  return (
    <EditModal title={`Edit Studio: ${studio.name}`} open={open} onClose={onClose}>
      <div className="flex gap-6 mb-4">
        <ImageInput
          currentImageUrl={entityImages.studioImageUrl(studio.id, studio.updatedAt)}
          onUpload={(file) => entityImages.uploadStudioImage(studio.id, file)}
          onDelete={() => entityImages.deleteStudioImage(studio.id)}
          onSuccess={() => queryClient.invalidateQueries({ queryKey: ["studio", studio.id] })}
          label="Logo"
          aspectRatio="1/1"
          className="w-40"
          objectFit="contain"
        />
        <div className="flex-1 space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <Field label="Name *">
          <TextInput value={name} onChange={setName} placeholder="Studio name" />
        </Field>
        <Field label="Parent Studio">
          <select
            value={parentId ?? ""}
            onChange={(e) => setParentId(e.target.value ? Number(e.target.value) : undefined)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          >
            <option value="">None</option>
            {parentStudioOptions.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        </Field>
      </div>

      <Field label="Details">
        <TextArea value={details} onChange={setDetails} placeholder="Studio description" rows={3} />
      </Field>

      <div className="grid grid-cols-2 gap-4">
        <InteractiveRatingField label="Rating" value={rating} onChange={setRating} />
        <div className="flex items-end gap-4 pb-4">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={ignoreAutoTag} onChange={(e) => setIgnoreAutoTag(e.target.checked)} className="rounded bg-card border-border" />
            Ignore Auto Tag
          </label>
        </div>
      </div>

      <Field label="URLs">
        <StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      <Field label="Aliases">
        <StringListEditor values={aliases} onChange={setAliases} placeholder="Alternate name" addLabel="Add Alias" />
      </Field>

      {/* Tags */}
      <Field label="Tags">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedTags.map((t) => (
            <span
              key={t.id}
              className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-accent/20 text-accent"
            >
              {t.name}
              <button onClick={() => setSelectedTagIds(selectedTagIds.filter((id) => id !== t.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={tagSearch}
          onChange={(e) => setTagSearch(e.target.value)}
          placeholder="Search tags..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {tagSearch && filteredTags.length > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {filteredTags.slice(0, 10).map((t) => (
              <button
                key={t.id}
                onClick={() => { setSelectedTagIds([...selectedTagIds, t.id]); setTagSearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {t.name}
              </button>
            ))}
          </div>
        )}
      </Field>

      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} />
      </Field>

      </div></div>
      <div className="flex justify-end gap-3 mt-4">
        <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={mutation.isPending} onClick={handleSave} />
      </div>
    </EditModal>
  );
}
