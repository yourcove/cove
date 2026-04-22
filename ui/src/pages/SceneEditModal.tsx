import { useState, useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { scenes, tags as tagsApi, performers as performersApi, galleries as galleriesApi, groups as groupsApi } from "../api/client";
import type { Scene, SceneUpdate, Tag, Performer, Studio } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { RatingField } from "../components/Rating";
import { CustomFieldsEditor } from "../components/shared";
import { StudioSelector } from "../components/StudioSelector";

interface Props {
  scene: Scene;
  open: boolean;
  onClose: () => void;
}

export function SceneEditModal({ scene, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [title, setTitle] = useState(scene.title || "");
  const [code, setCode] = useState(scene.code || "");
  const [details, setDetails] = useState(scene.details || "");
  const [director, setDirector] = useState(scene.director || "");
  const [date, setDate] = useState(scene.date || "");
  const [rating, setRating] = useState<number | undefined>(scene.rating ?? undefined);
  const [urls, setUrls] = useState<string[]>(scene.urls.length > 0 ? scene.urls : [""]);
  const addUrl = () => setUrls([...urls, ""]);
  const removeUrl = (i: number) => setUrls(urls.filter((_, idx) => idx !== i));
  const updateUrl = (i: number, val: string) => setUrls(urls.map((u, idx) => idx === i ? val : u));
  const [studioId, setStudioId] = useState<number | undefined>(scene.studioId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(scene.tags.map((t) => t.id));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(scene.performers.map((p) => p.id));
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>(scene.galleries.map((g) => g.id));
  const [selectedGroups, setSelectedGroups] = useState<{ groupId: number; sceneIndex: number }[]>(
    scene.groups.map((g) => ({ groupId: g.id, sceneIndex: g.sceneIndex }))
  );
  const [customFields, setCustomFields] = useState<Record<string, string>>(
    Object.fromEntries(Object.entries(scene.customFields ?? {}).map(([k, v]) => [k, String(v ?? "")]))
  );

  // Tag search
  const [tagSearch, setTagSearch] = useState("");
  const { data: allTags } = useQuery({
    queryKey: ["tags-all"],
    queryFn: () => tagsApi.find({ perPage: 500, sort: "name", direction: "asc" }),
  });

  // Performer search
  const [perfSearch, setPerfSearch] = useState("");
  const { data: allPerformers } = useQuery({
    queryKey: ["performers-all"],
    queryFn: () => performersApi.find({ perPage: 500, sort: "name", direction: "asc" }),
  });

  // Gallery search
  const [gallerySearch, setGallerySearch] = useState("");
  const { data: allGalleries } = useQuery({
    queryKey: ["galleries-all"],
    queryFn: () => galleriesApi.find({ perPage: 500, sort: "title", direction: "asc" }),
  });

  // Group search
  const [groupSearch, setGroupSearch] = useState("");
  const { data: allGroups } = useQuery({
    queryKey: ["groups-all"],
    queryFn: () => groupsApi.find({ perPage: 500, sort: "name", direction: "asc" }),
  });

  useEffect(() => {
    setTitle(scene.title || "");
    setCode(scene.code || "");
    setDetails(scene.details || "");
    setDirector(scene.director || "");
    setDate(scene.date || "");
    setRating(scene.rating ?? undefined);
    setUrls(scene.urls.length > 0 ? scene.urls : [""]);
    setStudioId(scene.studioId ?? undefined);
    setSelectedTagIds(scene.tags.map((t) => t.id));
    setSelectedPerformerIds(scene.performers.map((p) => p.id));
    setSelectedGalleryIds(scene.galleries.map((g) => g.id));
    setSelectedGroups(scene.groups.map((g) => ({ groupId: g.id, sceneIndex: g.sceneIndex })));
    setCustomFields(Object.fromEntries(Object.entries(scene.customFields ?? {}).map(([k, v]) => [k, String(v ?? "")])));
  }, [scene]);

  const mutation = useMutation({
    mutationFn: (data: SceneUpdate) => scenes.update(scene.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["scene", scene.id] });
      queryClient.invalidateQueries({ queryKey: ["scenes"] });
      onClose();
    },
  });

  const handleSave = () => {
    const urlList = urls.map((u) => u.trim()).filter(Boolean);
    mutation.mutate({
      title: title || undefined,
      code: code || undefined,
      details: details || undefined,
      director: director || undefined,
      date: date || undefined,
      rating,
      studioId,
      urls: urlList,
      tagIds: selectedTagIds,
      performerIds: selectedPerformerIds,
      galleryIds: selectedGalleryIds,
      groups: selectedGroups,
      customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
    });
  };

  const filteredTags = allTags?.items.filter(
    (t) => !selectedTagIds.includes(t.id) && t.name.toLowerCase().includes(tagSearch.toLowerCase())
  ) ?? [];

  const filteredPerformers = allPerformers?.items.filter(
    (p) => !selectedPerformerIds.includes(p.id) && p.name.toLowerCase().includes(perfSearch.toLowerCase())
  ) ?? [];

  const filteredGalleries = allGalleries?.items.filter(
    (g) => !selectedGalleryIds.includes(g.id) && (g.title || "").toLowerCase().includes(gallerySearch.toLowerCase())
  ) ?? [];

  const selectedGroupIds = selectedGroups.map((g) => g.groupId);
  const filteredGroups = allGroups?.items.filter(
    (g) => !selectedGroupIds.includes(g.id) && g.name.toLowerCase().includes(groupSearch.toLowerCase())
  ) ?? [];

  const selectedTags = allTags?.items.filter((t) => selectedTagIds.includes(t.id)) ?? scene.tags;
  const selectedPerformers = allPerformers?.items.filter((p) => selectedPerformerIds.includes(p.id)) ??
    scene.performers.map((p) => ({ ...p, disambiguation: p.disambiguation, favorite: p.favorite }));
  const selectedGalleries = allGalleries?.items.filter((g) => selectedGalleryIds.includes(g.id)) ?? scene.galleries;

  return (
    <EditModal title="Edit Scene" open={open} onClose={onClose}>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Title">
          <TextInput value={title} onChange={setTitle} placeholder="Scene title" />
        </Field>
        <Field label="Date">
          <input
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Studio Code">
          <TextInput value={code} onChange={setCode} placeholder="Studio code" />
        </Field>
        <Field label="Director">
          <TextInput value={director} onChange={setDirector} placeholder="Director name" />
        </Field>
      </div>

      <Field label="Details">
        <TextArea value={details} onChange={setDetails} placeholder="Scene description" />
      </Field>

      <div className="grid grid-cols-2 gap-4">
        <RatingField value={rating} onChange={setRating} />
        <Field label="Studio">
          <StudioSelector value={studioId} onChange={setStudioId} />
        </Field>
      </div>

      <Field label="URLs">
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

      {/* Performers */}
      <Field label="Performers">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedPerformers.map((p) => (
            <span
              key={p.id}
              className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-accent/10 text-accent-hover"
            >
              {p.name}
              <button onClick={() => setSelectedPerformerIds(selectedPerformerIds.filter((id) => id !== p.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={perfSearch}
          onChange={(e) => setPerfSearch(e.target.value)}
          placeholder="Search performers..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {perfSearch && filteredPerformers.length > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {filteredPerformers.slice(0, 10).map((p) => (
              <button
                key={p.id}
                onClick={() => { setSelectedPerformerIds([...selectedPerformerIds, p.id]); setPerfSearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {p.name}{p.disambiguation ? ` (${p.disambiguation})` : ""}
              </button>
            ))}
          </div>
        )}
      </Field>

      <Field label="Galleries">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedGalleries.map((gallery) => (
            <span
              key={gallery.id}
              className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-emerald-900 text-emerald-300"
            >
              {gallery.title || "Untitled"}
              <button onClick={() => setSelectedGalleryIds(selectedGalleryIds.filter((id) => id !== gallery.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={gallerySearch}
          onChange={(e) => setGallerySearch(e.target.value)}
          placeholder="Search galleries..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {gallerySearch && filteredGalleries.length > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {filteredGalleries.slice(0, 10).map((gallery) => (
              <button
                key={gallery.id}
                onClick={() => { setSelectedGalleryIds([...selectedGalleryIds, gallery.id]); setGallerySearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {gallery.title || "Untitled"}
              </button>
            ))}
          </div>
        )}
      </Field>

      {/* Groups */}
      <Field label="Groups">
        <div className="space-y-1.5 mb-2">
          {selectedGroups.map((sg) => {
            const group = allGroups?.items.find((g) => g.id === sg.groupId);
            return (
              <div key={sg.groupId} className="flex items-center gap-2">
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-orange-900 text-orange-300">
                  {group?.name || `Group #${sg.groupId}`}
                  <button onClick={() => setSelectedGroups(selectedGroups.filter((g) => g.groupId !== sg.groupId))} className="hover:text-white">×</button>
                </span>
                <label className="flex items-center gap-1 text-xs text-secondary">
                  Scene #
                  <input
                    type="number"
                    min={0}
                    value={sg.sceneIndex}
                    onChange={(e) => setSelectedGroups(selectedGroups.map((g) => g.groupId === sg.groupId ? { ...g, sceneIndex: Number(e.target.value) || 0 } : g))}
                    className="w-16 bg-card border border-border rounded px-2 py-0.5 text-xs text-foreground focus:outline-none focus:border-accent"
                  />
                </label>
              </div>
            );
          })}
        </div>
        <input
          type="text"
          value={groupSearch}
          onChange={(e) => setGroupSearch(e.target.value)}
          placeholder="Search groups..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {groupSearch && filteredGroups.length > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {filteredGroups.slice(0, 10).map((group) => (
              <button
                key={group.id}
                onClick={() => { setSelectedGroups([...selectedGroups, { groupId: group.id, sceneIndex: 0 }]); setGroupSearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {group.name}
              </button>
            ))}
          </div>
        )}
      </Field>

      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} />
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
