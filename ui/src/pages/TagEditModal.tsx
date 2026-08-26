import { useState, useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { tags, tagGroups } from "../api/client";
import type { TagDetail, TagUpdate, Tag } from "../api/types";
import { EditModal, Field, NumberInput, SaveButton, SelectInput, TextArea, TextInput } from "../components/EditModal";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { StringListEditor } from "../components/StringListEditor";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { getApiValidationFailureDetail } from "../utils/requestFailure";

interface Props {
  tag: TagDetail;
  open: boolean;
  onClose: () => void;
}

type PlayerBarMode = "default" | "always" | "never";

function clampOptionalPercent(value: number | undefined) {
  if (value == null || !Number.isFinite(value)) return undefined;
  return Math.min(100, Math.max(0, value));
}

export function TagEditModal({ tag, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(tag.name);
  const [sortName, setSortName] = useState(tag.sortName ?? "");
  const [description, setDescription] = useState(tag.description ?? "");
  const [color, setColor] = useState(tag.color ?? "");
  const [tagGroupId, setTagGroupId] = useState<number | undefined>(tag.tagGroupId ?? undefined);
  const [minOccurrenceSec, setMinOccurrenceSec] = useState<number | undefined>(tag.minOccurrenceSec ?? undefined);
  const [minOccurrencePercent, setMinOccurrencePercent] = useState<number | undefined>(tag.minOccurrencePercent ?? undefined);
  const [playerBarMode, setPlayerBarMode] = useState<PlayerBarMode>(() => readPlayerBarMode(tag.showAsSegment));
  const [segmentColorOverride, setSegmentColorOverride] = useState(tag.segmentColorOverride ?? "");
  const [segmentLaneOverride, setSegmentLaneOverride] = useState<number | undefined>(tag.segmentLaneOverride ?? undefined);
  const [aliases, setAliases] = useState(tag.aliases);
  const [selectedParentIds, setSelectedParentIds] = useState<number[]>(tag.parents.map((t) => t.id));
  const [selectedChildIds, setSelectedChildIds] = useState<number[]>(tag.children.map((t) => t.id));
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(tag.remoteIds?.length ? tag.remoteIds : []);

  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...(tag.customFields ?? {}) });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);

  const { data: groups = [] } = useQuery({
    queryKey: ["tag-groups"],
    queryFn: tagGroups.list,
  });
  const parentTagProvenanceById = buildTagProvenanceById(tag.parents, tag.fieldProvenance, "parents");
  const childTagProvenanceById = buildTagProvenanceById(tag.children, tag.fieldProvenance, "children");

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: TagUpdate) => tags.update(tag.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["tag", tag.id] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
      onClose();
    },
  });

  useEffect(() => {
    mutation.reset();
    setName(tag.name);
    setSortName(tag.sortName ?? "");
    setDescription(tag.description ?? "");
    setColor(tag.color ?? "");
    setTagGroupId(tag.tagGroupId ?? undefined);
    setMinOccurrenceSec(tag.minOccurrenceSec ?? undefined);
    setMinOccurrencePercent(tag.minOccurrencePercent ?? undefined);
    setPlayerBarMode(readPlayerBarMode(tag.showAsSegment));
    setSegmentColorOverride(tag.segmentColorOverride ?? "");
    setSegmentLaneOverride(tag.segmentLaneOverride ?? undefined);
    setAliases(tag.aliases);
    setSelectedParentIds(tag.parents.map((t) => t.id));
    setSelectedChildIds(tag.children.map((t) => t.id));
    setRemoteIds(tag.remoteIds?.length ? tag.remoteIds : []);
    setCustomFields({ ...(tag.customFields ?? {}) });
  }, [tag]);

  const handleClose = () => {
    mutation.reset();
    onClose();
  };

  const handleSave = () => {
    const aliasList = aliases.map((alias) => alias.trim()).filter(Boolean);
    const clearFields = [
      !sortName && "sortName",
      !description && "description",
    ].filter((field): field is string => Boolean(field));
    mutation.mutate({
      name,
      sortName: sortName || undefined,
      description: description || undefined,
      color: color.trim() || null,
      tagGroupId: tagGroupId ?? null,
      minOccurrenceSec: minOccurrenceSec ?? null,
      minOccurrencePercent: clampOptionalPercent(minOccurrencePercent) ?? null,
      showAsSegment: playerBarMode === "default" ? null : playerBarMode === "always",
      segmentColorOverride: playerBarMode === "always" ? (segmentColorOverride.trim() || null) : null,
      segmentLaneOverride: playerBarMode === "always" ? (segmentLaneOverride ?? null) : null,
      aliases: aliasList,
      parentIds: selectedParentIds,
      childIds: selectedChildIds,
      remoteIds: normalizeRemoteIds(remoteIds),
      customFields,
      clearFields,
    });
  };

  return (
    <EditModal title={`Edit Tag: ${tag.name}`} open={open} onClose={handleClose}>
      <Field label="Name *" fieldProvenance={tag.fieldProvenance} fieldKey="name">
        <TextInput value={name} onChange={setName} placeholder="Tag name" />
      </Field>

      <Field label="Sort Name" fieldProvenance={tag.fieldProvenance} fieldKey="sortName">
        <TextInput value={sortName} onChange={setSortName} placeholder="Custom sort name (optional)" />
      </Field>

      <Field label="Description" fieldProvenance={tag.fieldProvenance} fieldKey="description">
        <TextArea value={description} onChange={setDescription} placeholder="Tag description" rows={3} />
      </Field>

      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Badge Color" fieldProvenance={tag.fieldProvenance} fieldKey="color">
          <div className="flex items-center gap-2">
            <input
              type="color"
              value={/^#[0-9a-fA-F]{6}$/.test(color) ? color : "#6ee7b7"}
              onChange={(event) => setColor(event.target.value)}
              className="h-9 w-11 rounded border border-border bg-card p-1"
            />
            <TextInput value={color} onChange={setColor} placeholder="#6ee7b7" />
          </div>
        </Field>
        <Field label="Tag Group" fieldProvenance={tag.fieldProvenance} fieldKey={["tagGroup", "tagGroupId"]}>
          <SelectInput
            value={tagGroupId?.toString() ?? ""}
            onChange={(value) => setTagGroupId(value ? Number(value) : undefined)}
            options={groups.map((group) => ({ value: group.id.toString(), label: group.name }))}
          />
        </Field>
      </div>

      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Min Seconds" fieldProvenance={tag.fieldProvenance} fieldKey="minOccurrenceSec">
          <NumberInput value={minOccurrenceSec} onChange={setMinOccurrenceSec} min={0} />
        </Field>
        <Field label="Min Percent" fieldProvenance={tag.fieldProvenance} fieldKey="minOccurrencePercent">
          <NumberInput value={minOccurrencePercent} onChange={(value) => setMinOccurrencePercent(clampOptionalPercent(value))} min={0} max={100} />
        </Field>
      </div>

      <Field label="Aliases" fieldProvenance={tag.fieldProvenance} fieldKey="aliases">
        <StringListEditor
          values={aliases}
          onChange={setAliases}
          placeholder="Alternate name"
          addLabel="Add Alias"
        />
      </Field>


      <Field label="Player Bar" fieldProvenance={tag.fieldProvenance} fieldKey="showAsSegment">
        <div className="space-y-3 rounded-xl border border-border bg-surface/40 p-3">
          <SelectInput
            value={playerBarMode}
            onChange={(value) => setPlayerBarMode(value as PlayerBarMode)}
            options={[
              { value: "default", label: "Default - follow display profiles" },
              { value: "always", label: "Always - force visible on the player bar" },
              { value: "never", label: "Never - suppress this tag on the player bar" },
            ]}
          />
          <p className="text-xs text-secondary">
            Tag-level overrides win over profile visibility. Use Default to hand control back to display profiles.
          </p>
          {playerBarMode === "always" ? (
            <div className="grid gap-3 md:grid-cols-2">
              <div>
                <div className="mb-1 text-xs font-medium uppercase tracking-wide text-muted">Color override</div>
                <TextInput value={segmentColorOverride} onChange={setSegmentColorOverride} placeholder="#ffaa00" />
              </div>
              <div>
                <div className="mb-1 text-xs font-medium uppercase tracking-wide text-muted">Lane override</div>
                <NumberInput value={segmentLaneOverride} onChange={setSegmentLaneOverride} min={0} />
              </div>
            </div>
          ) : null}
        </div>
      </Field>

      {/* Parent Tags */}
      <Field label="Parent Tags" fieldProvenance={tag.fieldProvenance} fieldKey="parents">
        <EntityReferenceMultiSelector entityType="tag" values={selectedParentIds} onChange={setSelectedParentIds} placeholder="Search parent tags..." excludeIds={[tag.id, ...selectedChildIds]} selectedProvenanceById={parentTagProvenanceById} />
      </Field>

      {/* Child Tags */}
      <Field label="Child Tags" fieldProvenance={tag.fieldProvenance} fieldKey="children">
        <EntityReferenceMultiSelector entityType="tag" values={selectedChildIds} onChange={setSelectedChildIds} placeholder="Search child tags..." excludeIds={[tag.id, ...selectedParentIds]} selectedProvenanceById={childTagProvenanceById} />
      </Field>

      <Field label="Remote IDs" fieldProvenance={tag.fieldProvenance} fieldKey="remoteIds">
        <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
      </Field>

      <Field label="Custom Fields" fieldProvenance={tag.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="tag" />
      </Field>

      {mutation.error ? (
        <div role="alert" className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">
          {getApiValidationFailureDetail(mutation.error)}
        </div>
      ) : null}

      <div className="flex justify-end gap-3 mt-4">
        <button onClick={handleClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={mutation.isPending} disabled={!customFieldsValid} onClick={handleSave} />
      </div>
    </EditModal>
  );
}

function readPlayerBarMode(showAsSegment?: boolean | null): PlayerBarMode {
  if (showAsSegment === true) {
    return "always";
  }

  if (showAsSegment === false) {
    return "never";
  }

  return "default";
}
