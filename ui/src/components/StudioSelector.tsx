import { EntityReferenceSelector } from "./EntityReferenceSelector";

interface StudioSelectorProps {
  value?: number;
  onChange: (value: number | undefined) => void;
  placeholder?: string;
  inputClassName?: string;
}

export function StudioSelector({
  value,
  onChange,
  placeholder = "Search studios...",
  inputClassName,
}: StudioSelectorProps) {
  return (
    <EntityReferenceSelector
      entityType="studio"
      value={value}
      onChange={onChange}
      placeholder={placeholder}
      inputClassName={inputClassName}
      resultsMaxHeight={128}
    />
  );
}
