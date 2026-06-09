import { X } from "lucide-react";
import { useEffect, type ReactNode } from "react";
import type { FieldProvenance } from "../api/types";
import { FieldProvenanceHover } from "./FieldProvenanceHover";

interface Props {
  title: string;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  maxWidthClassName?: string;
}

export function EditModal({ title, open, onClose, children, maxWidthClassName = "sm:max-w-2xl" }: Props) {
  useEffect(() => {
    if (open) {
      document.body.style.overflow = "hidden";
      const handleEsc = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
      window.addEventListener("keydown", handleEsc);
      return () => { document.body.style.overflow = ""; window.removeEventListener("keydown", handleEsc); };
    }
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center">
      <div className="absolute inset-0 bg-black/70" onClick={onClose} />
      <div className={`relative bg-surface sm:rounded-lg shadow-xl w-full ${maxWidthClassName} h-full sm:h-auto sm:max-h-[85vh] flex flex-col sm:mx-4`}>
        <div className="flex items-center justify-between px-4 sm:px-6 py-3 sm:py-4 border-b border-border">
          <h2 className="text-lg font-semibold">{title}</h2>
          <button onClick={onClose} className="text-secondary hover:text-foreground p-1">
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-6 sm:py-5">
          {children}
        </div>
      </div>
    </div>
  );
}

// Reusable field components
export function Field({
  label,
  children,
  fieldProvenance,
  fieldKey,
}: {
  label: string;
  children: ReactNode;
  fieldProvenance?: FieldProvenance[];
  fieldKey?: string | string[];
}) {
  const content = (
    <div className="mb-4">
      <label className="block text-xs text-secondary mb-1">{label}</label>
      {children}
    </div>
  );

  return fieldKey ? (
    <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey={fieldKey} block>
      {content}
    </FieldProvenanceHover>
  ) : content;
}

export function TextInput({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) {
  return (
    <input
      type="text"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
    />
  );
}

export function TextArea({ value, onChange, placeholder, rows = 3 }: { value: string; onChange: (v: string) => void; placeholder?: string; rows?: number }) {
  return (
    <textarea
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      rows={rows}
      className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
    />
  );
}

export function NumberInput({ value, onChange, min, max }: { value: number | undefined; onChange: (v: number | undefined) => void; min?: number; max?: number }) {
  return (
    <input
      type="number"
      value={value ?? ""}
      onChange={(e) => onChange(e.target.value ? Number(e.target.value) : undefined)}
      min={min}
      max={max}
      className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
    />
  );
}

export function SelectInput({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[] }) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
    >
      <option value="">—</option>
      {options.map((o) => (
        <option key={o.value} value={o.value}>{o.label}</option>
      ))}
    </select>
  );
}

export function SaveButton({ loading, onClick }: { loading: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      disabled={loading}
      className="flex items-center gap-2 bg-accent hover:bg-accent-hover disabled:opacity-50 text-white px-6 py-2 rounded-lg transition-colors text-sm"
    >
      {loading ? "Saving..." : "Save"}
    </button>
  );
}

export function CreateModalActions({
  loading,
  onCancel,
  onSave,
  createAnother,
  onCreateAnotherChange,
}: {
  loading: boolean;
  onCancel?: () => void;
  onSave: () => void;
  createAnother: boolean;
  onCreateAnotherChange: (value: boolean) => void;
}) {
  return (
    <div className="mt-6 rounded-2xl border border-border bg-card/70 p-4">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
        <label className="flex items-start gap-3 rounded-xl border border-border/70 bg-surface/70 px-3 py-3 text-sm text-secondary">
          <input
            type="checkbox"
            checked={createAnother}
            onChange={(event) => onCreateAnotherChange(event.target.checked)}
            className="mt-0.5 rounded border-border bg-card"
          />
          <span>
            <span className="block font-medium text-foreground">Create another after save</span>
            <span className="mt-1 block text-xs text-secondary">Keep this dialog open and reset the form so you can immediately start the next item.</span>
          </span>
        </label>
        <div className="flex items-center justify-end gap-3">
          {onCancel && <button onClick={onCancel} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>}
          <SaveButton loading={loading} onClick={onSave} />
        </div>
      </div>
    </div>
  );
}
