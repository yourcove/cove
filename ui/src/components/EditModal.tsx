import { X } from "lucide-react";
import { useEffect } from "react";

interface Props {
  title: string;
  open: boolean;
  onClose: () => void;
  children: React.ReactNode;
}

export function EditModal({ title, open, onClose, children }: Props) {
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
      <div className="relative bg-surface sm:rounded-lg shadow-xl w-full sm:max-w-2xl h-full sm:h-auto sm:max-h-[85vh] flex flex-col sm:mx-4">
        <div className="flex items-center justify-between px-4 sm:px-6 py-3 sm:py-4 border-b border-border">
          <h2 className="text-lg font-semibold">{title}</h2>
          <button onClick={onClose} className="text-secondary hover:text-foreground p-1">
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="px-4 sm:px-6 overflow-y-auto flex-1">
          {children}
        </div>
      </div>
    </div>
  );
}

// Reusable field components
export function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="mb-4">
      <label className="block text-xs text-secondary mb-1">{label}</label>
      {children}
    </div>
  );
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
