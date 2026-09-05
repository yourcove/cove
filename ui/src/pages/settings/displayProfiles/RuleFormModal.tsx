import type { Dispatch, ReactNode, SetStateAction } from "react";
import type { SegmentDisplayRule, TagDetail } from "../../../api/types";
import { EntityMultiSelector } from "../../../components/EntityMultiSelector";
import { EditModal, Field, NumberInput, SaveButton, SelectInput, TextInput } from "../../../components/EditModal";
import { HOST_TYPE_OPTIONS, type RuleFormState } from "./types";

interface Props {
  open: boolean;
  editingRule: SegmentDisplayRule | null;
  form: RuleFormState;
  setForm: Dispatch<SetStateAction<RuleFormState>>;
  sourceKeyOptions: Array<{ value: string; label: string }>;
  kindOptions: Array<{ value: string; label: string }>;
  selectedRuleTag?: TagDetail;
  previewPane: ReactNode;
  onClose: () => void;
  onSave: () => void;
  onOpenTagDetail: (tagId: number) => void;
  saving: boolean;
}

export function RuleFormModal({
  open,
  editingRule,
  form,
  setForm,
  sourceKeyOptions,
  kindOptions,
  selectedRuleTag,
  previewPane,
  onClose,
  onSave,
  onOpenTagDetail,
  saving,
}: Props) {
  return (
    <EditModal title={editingRule ? "Edit Rule" : "Create Rule"} open={open} onClose={onClose}>
      <div className="space-y-4 py-4">
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Source Key">
            <SelectInput
              value={form.sourceKey}
              onChange={(value) => setForm((current) => ({ ...current, sourceKey: value }))}
              options={sourceKeyOptions}
            />
          </Field>
          <Field label="Kind">
            <SelectInput
              value={form.kind}
              onChange={(value) => setForm((current) => ({ ...current, kind: value }))}
              options={kindOptions}
            />
          </Field>
          <Field label="Tag">
            <EntityMultiSelector
              entityType="tags"
              values={form.tagId != null ? [form.tagId] : []}
              onChange={(values) =>
                setForm((current) => ({ ...current, tagId: values.length > 0 ? values[values.length - 1] : undefined }))
              }
              placeholder="Search tags..."
              emptyMessage="No tags found"
            />
          </Field>
          <Field label="Host Type">
            <SelectInput
              value={form.hostType}
              onChange={(value) => setForm((current) => ({ ...current, hostType: value as RuleFormState["hostType"] }))}
              options={HOST_TYPE_OPTIONS}
            />
          </Field>
          <Field label="Visible">
            <SelectInput
              value={form.visible ? "visible" : "hidden"}
              onChange={(value) => setForm((current) => ({ ...current, visible: value !== "hidden" }))}
              options={[
                { value: "visible", label: "Visible" },
                { value: "hidden", label: "Hidden" },
              ]}
            />
          </Field>
          <Field label="Min Confidence">
            <NumberInput
              value={form.minConfidence}
              onChange={(value) => setForm((current) => ({ ...current, minConfidence: value }))}
              min={0}
              max={1}
            />
          </Field>
          <Field label="Min Duration (sec)">
            <NumberInput
              value={form.minDurationSec}
              onChange={(value) => setForm((current) => ({ ...current, minDurationSec: value }))}
              min={0}
            />
          </Field>
          <Field label="Merge Gap (sec)">
            <NumberInput
              value={form.mergeGapSec}
              onChange={(value) => setForm((current) => ({ ...current, mergeGapSec: value }))}
              min={0}
            />
          </Field>
          <Field label="Lane">
            <div className="space-y-2">
              <input
                type="range"
                min={0}
                max={8}
                value={form.lane ?? 0}
                onChange={(event) => setForm((current) => ({ ...current, lane: Number(event.target.value) }))}
                className="w-full accent-accent"
              />
              <div className="text-xs text-secondary">{form.lane != null ? `Lane ${form.lane}` : "Default lane"}</div>
            </div>
          </Field>
        </div>

        <div className="rounded-xl border border-border bg-surface/40 p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-sm font-medium text-foreground">Color override</div>
              <div className="mt-1 text-xs text-secondary">
                Use a manual swatch when this rule should stand out from the tag or source defaults.
              </div>
            </div>
            <label className="flex items-center gap-2 text-sm text-foreground">
              <input
                type="checkbox"
                checked={form.useCustomColor}
                onChange={(event) => setForm((current) => ({ ...current, useCustomColor: event.target.checked }))}
                className="h-4 w-4 rounded border-border accent-accent"
              />
              Use custom color
            </label>
          </div>
          {form.useCustomColor ? (
            <div className="mt-3 flex items-center gap-3">
              <input
                type="color"
                value={form.colorOverride}
                onChange={(event) => setForm((current) => ({ ...current, colorOverride: event.target.value }))}
                className="h-10 w-14 rounded border border-border bg-card"
              />
              <TextInput
                value={form.colorOverride}
                onChange={(value) => setForm((current) => ({ ...current, colorOverride: value || "#3b82f6" }))}
                placeholder="#3b82f6"
              />
            </div>
          ) : null}
        </div>

        <div className="space-y-2 text-sm text-foreground">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={form.collapseToInstant}
              onChange={(event) => setForm((current) => ({ ...current, collapseToInstant: event.target.checked }))}
              className="h-4 w-4 rounded border-border accent-accent"
            />
            Collapse to instant
          </label>
        </div>

        {selectedRuleTag?.showAsSegment != null ? (
          <div className="rounded-xl border border-amber-400/30 bg-amber-500/10 p-4 text-sm text-amber-100">
            <div className="font-medium">Tag-level override active</div>
            <p className="mt-1 text-xs text-amber-100/80">
              {selectedRuleTag.name} is forced {selectedRuleTag.showAsSegment ? "visible" : "hidden"} on the player bar,
              so this rule will not control that tag&apos;s visibility.
            </p>
            <button
              type="button"
              onClick={() => onOpenTagDetail(selectedRuleTag.id)}
              className="mt-3 rounded-lg border border-amber-300/30 px-3 py-2 text-xs text-amber-100 transition-colors hover:border-amber-200"
            >
              Open tag settings
            </button>
          </div>
        ) : null}

        {previewPane}

        <div className="flex justify-end pb-4">
          <SaveButton loading={saving} onClick={onSave} />
        </div>
      </div>
    </EditModal>
  );
}
