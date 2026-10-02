import { useState, useEffect, useEffectEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { tags, tagGroups } from "../api/client";
import type { TagDetail, TagUpdate } from "../api/types";
import { EditModal, Field, NumberInput, SaveButton, SelectInput, TextArea, TextInput } from "../components/EditModal";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { StringListEditor } from "../components/StringListEditor";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { getApiValidationFailureDetail } from "../utils/requestFailure";
import { refreshSavedEntity } from "../utils/refreshSavedEntity";
import { changedUpdateFields } from "../utils/changedUpdateFields";
import { applyFormFields, untouchedFieldUpdates, type FormFieldSetters } from "../utils/rebaseEditForm";

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

function tagFormValues(tag: TagDetail) {
  return {
    name: tag.name,
    sortName: tag.sortName ?? "",
    description: tag.description ?? "",
    color: tag.color ?? "",
    tagGroupId: tag.tagGroupId ?? undefined,
    minOccurrenceSec: tag.minOccurrenceSec ?? undefined,
    minOccurrencePercent: tag.minOccurrencePercent ?? undefined,
    playerBarMode: readPlayerBarMode(tag.showAsSegment),
    segmentColorOverride: tag.segmentColorOverride ?? "",
    segmentLaneOverride: tag.segmentLaneOverride ?? undefined,
    aliases: tag.aliases,
    selectedParentIds: tag.parents.map((t) => t.id),
    selectedChildIds: tag.children.map((t) => t.id),
    remoteIds: (tag.remoteIds?.length ? tag.remoteIds : []) as RemoteIdValue[],
    customFields: { ...tag.customFields } as Record<string, unknown>,
  };
}

type TagFormValues = ReturnType<typeof tagFormValues>;

// Segment overrides are only editable while the tag always shows as a segment, but the server keeps (and
// renders) them in every mode. Leave hidden ones as stored, and clear them when the user leaves "always".
function withVisibleSegmentOverrides(values: TagFormValues, baseline: TagFormValues): TagFormValues {
  if (values.playerBarMode === "always") return values;
  if (baseline.playerBarMode === "always") {
    return { ...values, segmentColorOverride: "", segmentLaneOverride: undefined };
  }
  return {
    ...values,
    segmentColorOverride: baseline.segmentColorOverride,
    segmentLaneOverride: baseline.segmentLaneOverride,
  };
}

function tagUpdatePayload(values: TagFormValues): TagUpdate {
  const color = values.color.trim() || undefined;
  const minOccurrencePercent = clampOptionalPercent(values.minOccurrencePercent);
  const showAsSegment = values.playerBarMode === "default" ? undefined : values.playerBarMode === "always";
  const segmentColorOverride = values.segmentColorOverride.trim() || undefined;
  const segmentLaneOverride = values.segmentLaneOverride;
  const clearFields = [
    !values.sortName && "sortName",
    !values.description && "description",
    color === undefined && "color",
    values.tagGroupId === undefined && "tagGroupId",
    values.minOccurrenceSec === undefined && "minOccurrenceSec",
    minOccurrencePercent === undefined && "minOccurrencePercent",
    showAsSegment === undefined && "showAsSegment",
    segmentColorOverride === undefined && "segmentColorOverride",
    segmentLaneOverride === undefined && "segmentLaneOverride",
  ].filter((field): field is string => Boolean(field));
  return {
    name: values.name,
    sortName: values.sortName || undefined,
    description: values.description || undefined,
    color,
    tagGroupId: values.tagGroupId,
    minOccurrenceSec: values.minOccurrenceSec,
    minOccurrencePercent,
    showAsSegment,
    segmentColorOverride,
    segmentLaneOverride,
    aliases: values.aliases.map((alias) => alias.trim()).filter(Boolean),
    parentIds: values.selectedParentIds,
    childIds: values.selectedChildIds,
    remoteIds: normalizeRemoteIds(values.remoteIds),
    customFields: values.customFields,
    clearFields,
  };
}

export function TagEditModal({ tag, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(tag.name);
  const [sortName, setSortName] = useState(tag.sortName ?? "");
  const [description, setDescription] = useState(tag.description ?? "");
  const [color, setColor] = useState(tag.color ?? "");
  const [tagGroupId, setTagGroupId] = useState<number | undefined>(tag.tagGroupId ?? undefined);
  const [minOccurrenceSec, setMinOccurrenceSec] = useState<number | undefined>(tag.minOccurrenceSec ?? undefined);
  const [minOccurrencePercent, setMinOccurrencePercent] = useState<number | undefined>(
    tag.minOccurrencePercent ?? undefined,
  );
  const [playerBarMode, setPlayerBarMode] = useState<PlayerBarMode>(() => readPlayerBarMode(tag.showAsSegment));
  const [segmentColorOverride, setSegmentColorOverride] = useState(tag.segmentColorOverride ?? "");
  const [segmentLaneOverride, setSegmentLaneOverride] = useState<number | undefined>(
    tag.segmentLaneOverride ?? undefined,
  );
  const [aliases, setAliases] = useState(tag.aliases);
  const [selectedParentIds, setSelectedParentIds] = useState<number[]>(tag.parents.map((t) => t.id));
  const [selectedChildIds, setSelectedChildIds] = useState<number[]>(tag.children.map((t) => t.id));
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(tag.remoteIds?.length ? tag.remoteIds : []);

  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...tag.customFields });
  const [customFieldsValid, setCustomFieldsValid] = useState(true);

  const { data: groups = [] } = useQuery({
    queryKey: ["tag-groups"],
    queryFn: tagGroups.list,
  });
  const parentTagProvenanceById = buildTagProvenanceById(tag.parents, tag.fieldProvenance, "parents");
  const childTagProvenanceById = buildTagProvenanceById(tag.children, tag.fieldProvenance, "children");

  const currentValues: TagFormValues = {
    name,
    sortName,
    description,
    color,
    tagGroupId,
    minOccurrenceSec,
    minOccurrencePercent,
    playerBarMode,
    segmentColorOverride,
    segmentLaneOverride,
    aliases,
    selectedParentIds,
    selectedChildIds,
    remoteIds,
    customFields,
  };
  const formSetters: FormFieldSetters<TagFormValues> = {
    name: setName,
    sortName: setSortName,
    description: setDescription,
    color: setColor,
    tagGroupId: setTagGroupId,
    minOccurrenceSec: setMinOccurrenceSec,
    minOccurrencePercent: setMinOccurrencePercent,
    playerBarMode: setPlayerBarMode,
    segmentColorOverride: setSegmentColorOverride,
    segmentLaneOverride: setSegmentLaneOverride,
    aliases: setAliases,
    selectedParentIds: setSelectedParentIds,
    selectedChildIds: setSelectedChildIds,
    remoteIds: setRemoteIds,
    customFields: setCustomFields,
  };

  // The tag the form was last filled from; saving sends only the fields changed since.
  const [baseline, setBaseline] = useState(tag);

  // Fill the form each time the dialog opens. A refetch while it is open keeps the user's edits, and
  // reopening after Cancel discards them.
  const [prevOpen, setPrevOpen] = useState(open);
  const [prevTagId, setPrevTagId] = useState(tag.id);
  const openOrTagChanged = open !== prevOpen || tag.id !== prevTagId;
  if (openOrTagChanged) {
    setPrevOpen(open);
    setPrevTagId(tag.id);
    if (open) {
      setBaseline(tag);
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
      setCustomFields({ ...tag.customFields });
    }
  }

  // When the tag refetches while the dialog is open, untouched fields follow it and the user's edits stay.
  if (open && !openOrTagChanged && tag !== baseline) {
    applyFormFields(untouchedFieldUpdates(currentValues, tagFormValues(baseline), tagFormValues(tag)), formSetters);
    setBaseline(tag);
  }

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: TagUpdate) => tags.update(tag.id, data),
    onSuccess: async () => {
      queryClient.invalidateQueries({ queryKey: ["tags"] });
      // Close once the saved tag is loaded, so reopening the dialog starts from it.
      await refreshSavedEntity(queryClient, ["tag", tag.id]);
      onClose();
    },
  });

  // Each opening also clears the result of the previous save attempt, alongside the form refill above.
  const resetSaveAttempt = useEffectEvent(() => mutation.reset());
  useEffect(() => {
    if (!open) return;
    resetSaveAttempt();
  }, [tag.id, open]);

  const handleClose = () => {
    mutation.reset();
    onClose();
  };

  const handleSave = () => {
    const baselineValues = tagFormValues(baseline);
    const current = tagUpdatePayload(withVisibleSegmentOverrides(currentValues, baselineValues));
    mutation.mutate(changedUpdateFields(tagUpdatePayload(baselineValues), current));
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
          <NumberInput
            value={minOccurrencePercent}
            onChange={(value) => setMinOccurrencePercent(clampOptionalPercent(value))}
            min={0}
            max={100}
          />
        </Field>
      </div>

      <Field label="Aliases" fieldProvenance={tag.fieldProvenance} fieldKey="aliases">
        <StringListEditor values={aliases} onChange={setAliases} placeholder="Alternate name" addLabel="Add Alias" />
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
        <EntityReferenceMultiSelector
          entityType="tag"
          values={selectedParentIds}
          onChange={setSelectedParentIds}
          placeholder="Search parent tags..."
          excludeIds={[tag.id, ...selectedChildIds]}
          selectedProvenanceById={parentTagProvenanceById}
        />
      </Field>

      {/* Child Tags */}
      <Field label="Child Tags" fieldProvenance={tag.fieldProvenance} fieldKey="children">
        <EntityReferenceMultiSelector
          entityType="tag"
          values={selectedChildIds}
          onChange={setSelectedChildIds}
          placeholder="Search child tags..."
          excludeIds={[tag.id, ...selectedParentIds]}
          selectedProvenanceById={childTagProvenanceById}
        />
      </Field>

      <Field label="Remote IDs" fieldProvenance={tag.fieldProvenance} fieldKey="remoteIds">
        <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
      </Field>

      <Field label="Custom Fields" fieldProvenance={tag.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor
          value={customFields}
          onChange={setCustomFields}
          onValidityChange={setCustomFieldsValid}
          entityType="tag"
        />
      </Field>

      {mutation.error ? (
        <div role="alert" className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">
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

function readPlayerBarMode(showAsSegment?: boolean | null): PlayerBarMode {
  if (showAsSegment === true) {
    return "always";
  }

  if (showAsSegment === false) {
    return "never";
  }

  return "default";
}
