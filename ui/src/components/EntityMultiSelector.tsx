import { EntityReferenceMultiSelector, type EntityReferenceType } from "./EntityReferenceSelector";

type EntitySelectorType = "tags" | "performers" | "faces";

interface EntityMultiSelectorProps {
  entityType: EntitySelectorType;
  values: number[];
  onChange: (values: number[]) => void;
  placeholder?: string;
  emptyMessage?: string;
}

const REFERENCE_TYPE: Record<EntitySelectorType, EntityReferenceType> = {
  tags: "tag",
  performers: "performer",
  faces: "face",
};

export function EntityMultiSelector({
  entityType,
  values,
  onChange,
  placeholder,
  emptyMessage,
}: EntityMultiSelectorProps) {
  return (
    <EntityReferenceMultiSelector
      entityType={REFERENCE_TYPE[entityType]}
      values={values}
      onChange={onChange}
      placeholder={placeholder}
      emptyMessage={emptyMessage}
      creatable={false}
    />
  );
}
