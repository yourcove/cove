import type {
  BoolCriterion,
  CriterionModifier,
  DateCriterion,
  FingerprintCriterion,
  IntCriterion,
  MultiIdCriterion,
  StringCriterion,
  TagDurationCriterion,
  TimestampCriterion,
} from "../api/types";
import { MultiIdEditor } from "./MultiIdFilterEditor";
import {
  BoolEditor,
  DateEditor,
  EnumEditor,
  HashEditor,
  MultiEnumEditor,
  NumberEditor,
  PathEditor,
  RatingFilterEditor,
  RemoteIdFilterEditor,
  StringEditor,
  TimestampEditor,
} from "./PrimitiveCriterionEditors";
import { TagDurationEditor } from "./TagDurationFilterEditor";
import type { CriterionDefinition, CriterionType } from "./filterCriteriaTypes";

const TYPE_MODIFIERS: Record<CriterionType, CriterionModifier[]> = {
  string: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  path: ["UNDER_PATH", "NOT_UNDER_PATH", "EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  remoteId: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  hash: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  number: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  bool: ["EQUALS"],
  date: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  timestamp: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  duration: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  tagDuration: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  careerLength: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  rating: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  resolution: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN"],
  multiId: ["INCLUDES", "INCLUDES_ALL", "EXCLUDES", "EXCLUDES_ALL", "IS_NULL", "NOT_NULL"],
  enum: ["EQUALS", "NOT_EQUALS", "IS_NULL", "NOT_NULL"],
  related: [],
};


export function CriterionEditor({
  criterion,
  value,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
  onChange,
}: {
  criterion: CriterionDefinition;
  value: unknown;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
  onChange: (v: unknown) => void;
}) {
  const { type, entityType } = criterion;
  const modifiers = criterion.modifiers ?? TYPE_MODIFIERS[type];

  switch (type) {
    case "related":
      return null;
    case "bool":
      return <BoolEditor value={value as BoolCriterion | undefined} onChange={onChange} />;
    case "rating":
      return <RatingFilterEditor value={value as IntCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "number":
    case "duration":
    case "careerLength":
    case "resolution":
      return (
        <NumberEditor
          value={value as IntCriterion | undefined}
          onChange={onChange}
          type={type}
          modifiers={modifiers}
          defaultModifier={criterion.defaultModifier}
          min={criterion.min}
          max={criterion.max}
          step={criterion.step}
          hint={criterion.hint}
          auxiliaryToggleLabel={criterion.auxiliaryToggleLabel}
          auxiliaryToggleChecked={auxiliaryToggleChecked}
          onAuxiliaryToggleChange={onAuxiliaryToggleChange}
        />
      );
    case "tagDuration":
      return <TagDurationEditor value={value as TagDurationCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "hash":
      return <HashEditor value={value as FingerprintCriterion | undefined} onChange={onChange} modifiers={modifiers} options={criterion.options ?? []} />;
    case "string":
      return <StringEditor value={value as StringCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "path":
      return <PathEditor value={value as StringCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "remoteId":
      return <RemoteIdFilterEditor value={value as (StringCriterion & { endpoint?: string }) | undefined} onChange={onChange} modifiers={modifiers} />;
    case "enum":
      return criterion.multiSelectOptions
        ? <MultiEnumEditor value={value as StringCriterion | undefined} onChange={onChange} options={criterion.options ?? []} />
        : <EnumEditor value={value as StringCriterion | undefined} onChange={onChange} options={criterion.options ?? []} modifiers={modifiers} />;
    case "date":
      return <DateEditor value={value as DateCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "timestamp":
      return <TimestampEditor value={value as TimestampCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "multiId":
      return <MultiIdEditor value={value as MultiIdCriterion | undefined} onChange={onChange} entityType={entityType!} modifiers={modifiers} hierarchyToggleLabel={criterion.hierarchyToggleLabel} />;
    default:
      return null;
  }
}
