import type { Dispatch, SetStateAction } from "react";
import type { SegmentDisplayProfile } from "../../../api/types";
import { EditModal, Field, SaveButton, TextInput } from "../../../components/EditModal";
import type { ProfileFormState } from "./types";

interface Props {
  open: boolean;
  editingProfile: SegmentDisplayProfile | null;
  form: ProfileFormState;
  setForm: Dispatch<SetStateAction<ProfileFormState>>;
  onClose: () => void;
  onSave: () => void;
  saving: boolean;
}

export function ProfileFormModal({ open, editingProfile, form, setForm, onClose, onSave, saving }: Props) {
  return (
    <EditModal title={editingProfile ? "Edit Display Profile" : "Create Display Profile"} open={open} onClose={onClose}>
      <div className="py-4">
        <Field label="Name">
          <TextInput value={form.name} onChange={(value) => setForm((current) => ({ ...current, name: value }))} />
        </Field>
        <Field label="Description">
          <TextInput
            value={form.description}
            onChange={(value) => setForm((current) => ({ ...current, description: value }))}
          />
        </Field>
        {!editingProfile ? (
          <label className="flex items-center gap-2 text-sm text-foreground">
            <input
              type="checkbox"
              checked={form.isDefault}
              onChange={(event) => setForm((current) => ({ ...current, isDefault: event.target.checked }))}
              className="h-4 w-4 rounded border-border accent-accent"
            />
            Make default profile
          </label>
        ) : null}
        <div className="mt-4 flex justify-end">
          <SaveButton loading={saving} onClick={onSave} />
        </div>
      </div>
    </EditModal>
  );
}
