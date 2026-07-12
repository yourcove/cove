import { EntityReferenceSelector } from "./EntityReferenceSelector";

interface StudioSelectorProps {
  value?: number;
  onChange: (value: number | undefined) => void;
  placeholder?: string;
}

export function StudioSelector({ value, onChange, placeholder = "Search studios..." }: StudioSelectorProps) {
  return (
    <EntityReferenceSelector
      entityType="studio"
      value={value}
      onChange={onChange}
      placeholder={placeholder}
      resultsMaxHeight={128}
    />
  );
}
