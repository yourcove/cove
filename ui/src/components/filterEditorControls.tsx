import type { ReactNode } from "react";

export function LabeledControl({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block space-y-1.5 text-sm font-medium text-secondary">
      <span>{label}</span>
      {children}
    </label>
  );
}
