import type { Dispatch, SetStateAction } from "react";
import { EntityMultiSelector } from "../../../components/EntityMultiSelector";
import { EditModal, Field, NumberInput, SaveButton, SelectInput, TextInput } from "../../../components/EditModal";
import type { BulkWizardState } from "./types";

interface Props {
  open: boolean;
  form: BulkWizardState;
  setForm: Dispatch<SetStateAction<BulkWizardState>>;
  onClose: () => void;
  onSave: () => void;
  saving: boolean;
}

export function BulkFromTagsWizard({ open, form, setForm, onClose, onSave, saving }: Props) {
  return (
    <EditModal title="Generate Rules From Tags" open={open} onClose={onClose}>
      <div className="space-y-4 py-4">
        <Field label="Tags">
          <EntityMultiSelector
            entityType="tags"
            values={form.tagIds}
            onChange={(values) => setForm((current) => ({ ...current, tagIds: values }))}
            placeholder="Search tags to generate rules..."
            emptyMessage="No tags found"
          />
        </Field>
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Visibility">
            <SelectInput
              value={form.visible ? "visible" : "hidden"}
              onChange={(value) => setForm((current) => ({ ...current, visible: value !== "hidden" }))}
              options={[
                { value: "visible", label: "Visible" },
                { value: "hidden", label: "Hidden" },
              ]}
            />
          </Field>
          <Field label="Merge Gap (sec)">
            <NumberInput
              value={form.mergeGapSec}
              onChange={(value) => setForm((current) => ({ ...current, mergeGapSec: value }))}
              min={0}
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
          <Field label="Lane">
            <NumberInput
              value={form.lane}
              onChange={(value) => setForm((current) => ({ ...current, lane: value }))}
              min={0}
            />
          </Field>
        </div>

        <div className="rounded-xl border border-border bg-surface/40 p-4">
          <label className="flex items-center gap-2 text-sm text-foreground">
            <input
              type="checkbox"
              checked={form.useCustomColor}
              onChange={(event) => setForm((current) => ({ ...current, useCustomColor: event.target.checked }))}
              className="h-4 w-4 rounded border-border accent-accent"
            />
            Use one shared color for the generated rules
          </label>
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

        <div className="flex justify-end pb-4">
          <SaveButton loading={saving} onClick={onSave} />
        </div>
      </div>
    </EditModal>
  );
}
