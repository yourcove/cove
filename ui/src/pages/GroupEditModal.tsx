import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { groups } from "../api/client";
import type { Group, GroupUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { IsoDateInput } from "../components/IsoDateInput";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import {
  DynamicGroupFilterEditor,
  FILTER_DYNAMIC_SOURCE_KEY,
  defaultDynamicGroupFilterQueryJson,
} from "../components/DynamicGroupFilterEditor";
import { changedUpdateFields } from "../utils/changedUpdateFields";
import { refreshSavedEntity } from "../utils/refreshSavedEntity";

interface Props {
  group: Group;
  open: boolean;
  onClose: () => void;
}

function groupFormValues(group: Group) {
  return {
    name: group.name,
    aliases: splitAliases(group.aliases),
    director: group.director ?? "",
    date: group.date ?? "",
    studioId: group.studioId ?? undefined,
    description: group.description ?? "",
    urls: group.urls.length > 0 ? group.urls : [""],
    selectedTagIds: group.tags.map((t) => t.id),
    customFields: { ...group.customFields } as Record<string, unknown>,
    kind: (group.kind ?? "static") as "static" | "dynamic",
    querySourceKey: group.querySourceKey ?? FILTER_DYNAMIC_SOURCE_KEY,
    queryJson: group.queryJson ?? defaultDynamicGroupFilterQueryJson(),
    showInVideoLists: group.showInVideoLists ?? false,
  };
}

function groupUpdatePayload(values: ReturnType<typeof groupFormValues>): GroupUpdate {
  const joinedAliases = joinAliases(values.aliases);
  const clearFields = [
    !joinedAliases && "aliases",
    !values.director && "director",
    !values.date && "date",
    values.studioId === undefined && "studioId",
    !values.description && "description",
  ].filter((field): field is string => Boolean(field));
  return {
    name: values.name,
    aliases: joinedAliases || undefined,
    director: values.director || undefined,
    date: values.date || undefined,
    studioId: values.studioId,
    description: values.description || undefined,
    urls: values.urls.map((url) => url.trim()).filter(Boolean),
    tagIds: values.selectedTagIds,
    customFields: values.customFields,
    kind: values.kind,
    querySourceKey: values.kind === "dynamic" ? values.querySourceKey : undefined,
    queryJson:
      values.kind === "dynamic" && values.querySourceKey === FILTER_DYNAMIC_SOURCE_KEY ? values.queryJson : undefined,
    showInVideoLists: values.showInVideoLists,
    clearFields,
  };
}

export function GroupEditModal({ group, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(group.name);
  const [aliases, setAliases] = useState<string[]>(() => splitAliases(group.aliases));
  const [director, setDirector] = useState(group.director ?? "");
  const [date, setDate] = useState(group.date ?? "");
  const [studioId, setStudioId] = useState<number | undefined>(group.studioId ?? undefined);
  const [description, setDescription] = useState(group.description ?? "");
  const [urls, setUrls] = useState(group.urls.length > 0 ? group.urls : [""]);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(group.tags.map((t) => t.id));
  const [selectedParentGroupIds, setSelectedParentGroupIds] = useState<number[]>([]);
  const [kind, setKind] = useState<"static" | "dynamic">(group.kind ?? "static");
  const [querySourceKey, setQuerySourceKey] = useState(group.querySourceKey ?? FILTER_DYNAMIC_SOURCE_KEY);
  const [queryJson, setQueryJson] = useState(group.queryJson ?? defaultDynamicGroupFilterQueryJson());
  const [showInVideoLists, setShowInVideoLists] = useState(group.showInVideoLists ?? false);

  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...group.customFields });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const tagProvenanceById = buildTagProvenanceById(group.tags, group.fieldProvenance);
  const { data: dynamicSources } = useQuery({
    queryKey: ["group-dynamic-sources"],
    queryFn: () => groups.dynamicSources(),
    enabled: open,
  });
  const { data: containingGroups } = useQuery({
    queryKey: ["group-containinggroups", group.id],
    queryFn: () => groups.containingGroups(group.id),
    enabled: open,
  });

  // The group the form was last filled from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(group);

  const [prevOpen, setPrevOpen] = useState(open);
  const [prevGroupId, setPrevGroupId] = useState(group.id);
  if (open !== prevOpen || group.id !== prevGroupId) {
    setPrevOpen(open);
    setPrevGroupId(group.id);
    if (open) {
      setBaseline(group);
      setName(group.name);
      setAliases(splitAliases(group.aliases));
      setDirector(group.director ?? "");
      setDate(group.date ?? "");
      setStudioId(group.studioId ?? undefined);
      setDescription(group.description ?? "");
      setUrls(group.urls.length > 0 ? group.urls : [""]);
      setSelectedTagIds(group.tags.map((t) => t.id));
      setCustomFields({ ...group.customFields });
      setKind(group.kind ?? "static");
      setQuerySourceKey(group.querySourceKey ?? FILTER_DYNAMIC_SOURCE_KEY);
      setQueryJson(group.queryJson ?? defaultDynamicGroupFilterQueryJson());
      setShowInVideoLists(group.showInVideoLists ?? false);
    }
  }

  // A group without a stored source starts on the filter source; when the server does not offer it, switch to
  // the first source it does offer. The select lists only offered sources, so the filter source can only come
  // back through the refill above, which this check then catches again.
  if (open && !group.querySourceKey && dynamicSources?.length && querySourceKey === FILTER_DYNAMIC_SOURCE_KEY) {
    const fallbackKey =
      dynamicSources.find((source) => source.key === FILTER_DYNAMIC_SOURCE_KEY)?.key ?? dynamicSources[0].key;
    if (fallbackKey !== FILTER_DYNAMIC_SOURCE_KEY) setQuerySourceKey(fallbackKey);
  }

  // Starts undefined so a dialog that mounts open with parent groups already cached still takes them.
  const [prevContainingGroups, setPrevContainingGroups] = useState<typeof containingGroups>(undefined);
  if (open !== prevOpen || containingGroups !== prevContainingGroups) {
    setPrevContainingGroups(containingGroups);
    if (open && containingGroups) setSelectedParentGroupIds(containingGroups.map((parent) => parent.id));
  }

  const mutation = useMutation({
    mutationFn: async (data: GroupUpdate) => {
      const originalParentIds = new Set((containingGroups ?? []).map((parent) => parent.id));
      const nextParentIds = new Set(selectedParentGroupIds);
      const updated = await groups.update(group.id, data);

      for (const parentId of nextParentIds) {
        if (!originalParentIds.has(parentId)) {
          await groups.addSubGroup(parentId, group.id);
        }
      }

      for (const parentId of originalParentIds) {
        if (!nextParentIds.has(parentId)) {
          await groups.removeSubGroup(parentId, group.id);
        }
      }

      return updated;
    },
    onSuccess: async () => {
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      queryClient.invalidateQueries({ queryKey: ["group-containinggroups", group.id] });
      const changedParentIds = new Set([
        ...(containingGroups ?? []).map((parent) => parent.id),
        ...selectedParentGroupIds,
      ]);
      for (const parentId of changedParentIds) {
        queryClient.invalidateQueries({ queryKey: ["group-subgroups", parentId] });
      }
      // Close once the saved group is loaded, so reopening the dialog starts from it.
      await refreshSavedEntity(queryClient, ["group", group.id]);
      onClose();
    },
  });

  const handleSave = () => {
    const current = groupUpdatePayload({
      name,
      aliases,
      director,
      date,
      studioId,
      description,
      urls,
      selectedTagIds,
      customFields,
      kind,
      querySourceKey,
      queryJson,
      showInVideoLists,
    });
    mutation.mutate(changedUpdateFields(groupUpdatePayload(groupFormValues(baseline)), current));
  };

  return (
    <EditModal title={`Edit Group: ${group.name}`} open={open} onClose={onClose}>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Name *" fieldProvenance={group.fieldProvenance} fieldKey="name">
          <TextInput value={name} onChange={setName} placeholder="Group name" />
        </Field>
        <Field label="Studio" fieldProvenance={group.fieldProvenance} fieldKey={["studio", "studioId"]}>
          <StudioSelector value={studioId} onChange={setStudioId} />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Kind" fieldProvenance={group.fieldProvenance} fieldKey="kind">
          <div className="inline-flex rounded-lg border border-border bg-card p-1">
            {(["static", "dynamic"] as const).map((nextKind) => (
              <button
                key={nextKind}
                type="button"
                onClick={() => setKind(nextKind)}
                className={`rounded-md px-3 py-1.5 text-sm capitalize transition-colors ${kind === nextKind ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
              >
                {nextKind}
              </button>
            ))}
          </div>
        </Field>
        <Field label="Video Browsing" fieldProvenance={group.fieldProvenance} fieldKey="showInVideoLists">
          <label className="inline-flex items-center gap-2 text-sm text-foreground">
            <input
              type="checkbox"
              checked={showInVideoLists}
              onChange={(event) => setShowInVideoLists(event.target.checked)}
              className="h-4 w-4 accent-accent"
            />
            Show in video browsing
          </label>
        </Field>
      </div>

      {kind === "dynamic" ? (
        <div className="grid grid-cols-1 gap-4">
          <Field label="Dynamic source" fieldProvenance={group.fieldProvenance} fieldKey="querySourceKey">
            <select
              value={querySourceKey}
              onChange={(event) => {
                setQuerySourceKey(event.target.value);
                if (event.target.value === FILTER_DYNAMIC_SOURCE_KEY && !queryJson) {
                  setQueryJson(defaultDynamicGroupFilterQueryJson());
                }
              }}
              className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            >
              {(dynamicSources ?? []).map((source) => (
                <option key={source.key} value={source.key}>
                  {source.displayName}
                </option>
              ))}
            </select>
          </Field>
        </div>
      ) : null}

      {kind === "dynamic" && querySourceKey === FILTER_DYNAMIC_SOURCE_KEY ? (
        <DynamicGroupFilterEditor queryJson={queryJson} onChange={setQueryJson} />
      ) : null}

      <div className="grid grid-cols-2 gap-4">
        <Field label="Director" fieldProvenance={group.fieldProvenance} fieldKey="director">
          <TextInput value={director} onChange={setDirector} placeholder="Director name" />
        </Field>
        <Field label="Date" fieldProvenance={group.fieldProvenance} fieldKey="date">
          <IsoDateInput
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <Field
        label="Description"
        fieldProvenance={group.fieldProvenance}
        fieldKey={["description", "details", "synopsis"]}
      >
        <TextArea value={description} onChange={setDescription} placeholder="Group description" rows={4} />
      </Field>

      <Field label="Aliases" fieldProvenance={group.fieldProvenance} fieldKey="aliases">
        <StringListEditor values={aliases} onChange={setAliases} placeholder="Alias" addLabel="Add Alias" />
      </Field>

      <Field label="URLs" fieldProvenance={group.fieldProvenance} fieldKey="urls">
        <StringListEditor
          values={urls}
          onChange={setUrls}
          placeholder="https://..."
          addLabel="Add URL"
          inputType="url"
        />
      </Field>

      <Field label="Parent Groups" fieldProvenance={group.fieldProvenance} fieldKey="containingGroups">
        <EntityReferenceMultiSelector
          entityType="group"
          values={selectedParentGroupIds}
          onChange={setSelectedParentGroupIds}
          placeholder="Search parent groups..."
          excludeIds={[group.id]}
        />
      </Field>

      {/* Tags */}
      <Field label="Tags" fieldProvenance={group.fieldProvenance} fieldKey="tags">
        <EntityReferenceMultiSelector
          entityType="tag"
          values={selectedTagIds}
          onChange={setSelectedTagIds}
          placeholder="Search tags..."
          selectedProvenanceById={tagProvenanceById}
        />
      </Field>

      <Field label="Custom Fields" fieldProvenance={group.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor
          value={customFields}
          onChange={setCustomFields}
          onValidityChange={setCustomFieldsValid}
          entityType="group"
        />
      </Field>

      <div className="flex justify-end gap-3 mt-4">
        <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">
          Cancel
        </button>
        <SaveButton loading={mutation.isPending} disabled={!customFieldsValid} onClick={handleSave} />
      </div>
    </EditModal>
  );
}

function splitAliases(value?: string) {
  return (
    value
      ?.split(/[\r\n,]+/)
      .map((alias) => alias.trim())
      .filter(Boolean) ?? []
  );
}

function joinAliases(values: string[]) {
  return values
    .map((alias) => alias.trim())
    .filter(Boolean)
    .join(", ");
}
