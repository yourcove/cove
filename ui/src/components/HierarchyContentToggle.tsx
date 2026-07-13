interface HierarchyContentToggleProps {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

export function HierarchyContentToggle({ checked, label, onChange }: HierarchyContentToggleProps) {
  return (
    <label className="flex cursor-pointer select-none items-center gap-2 text-sm text-secondary">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="h-4 w-4 accent-accent"
      />
      {label}
    </label>
  );
}
