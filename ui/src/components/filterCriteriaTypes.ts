import type { CriterionModifier } from "../api/types";
import type { ReactNode } from "react";

export type CriterionType =
  | "string"
  | "country"
  | "path"
  | "remoteId"
  | "number"
  | "bool"
  | "date"
  | "timestamp"
  | "duration"
  | "tagDuration"
  | "careerLength"
  | "rating"
  | "resolution"
  | "multiId"
  | "enum"
  | "hash"
  | "related";
export type EntityType =
  "tags" | "tagGroups" | "performers" | "studios" | "groups" | "galleries" | "videos" | "audios" | "faces";
export const MAX_DISTINCT_RELATED_CONDITIONS = 8;

export interface CriterionDefinition<TFilterKey extends string = string> {
  id: string;
  label: string;
  type: CriterionType;
  entityType?: EntityType;
  filterKey: TFilterKey;
  category?: "related";
  /** Lazily resolves the criteria available inside a related-entity workspace. */
  relatedCriteria?: () => CriterionDefinition[];
  /** Criteria evaluated against the relationship host rather than the related entity itself. */
  relatedContextCriteria?: CriterionDefinition[];
  /** Enables distinct sibling matching for repeated instances of this related criterion. */
  supportsDistinctSiblingMatches?: boolean;
  customFieldKey?: string;
  customFieldType?: string;
  modifiers?: CriterionModifier[];
  expressionSupported?: boolean;
  /** Modifier used for a new numeric criterion when the editor default is not offered. */
  defaultModifier?: CriterionModifier;
  /** Bounds and granularity for numeric inputs; declaring both bounds enables a slider. */
  min?: number;
  max?: number;
  step?: number;
  hint?: string;
  options?: { value: string; label: string }[];
  multiSelectOptions?: boolean;
  hierarchyToggleLabel?: string;
  auxiliaryToggleKey?: TFilterKey;
  auxiliaryToggleLabel?: string;
  secondaryFilterKey?: TFilterKey;
  supported?: boolean;
  unsupportedReason?: string;
}

export type CriteriaDefinitionList<TFilterCriteria> = CriterionDefinition<Extract<keyof TFilterCriteria, string>>[];

export interface FilterDialogCustomSection {
  id: string;
  label: string;
  filterKey: string;
  defaultValue: unknown;
  isActive: (value: unknown) => boolean;
  shouldKeepDraft?: (value: unknown) => boolean;
  sanitize?: (value: unknown) => unknown;
  renderEditor: (value: unknown, onChange: (value: unknown) => void) => ReactNode;
  summarize?: (value: unknown) => string;
}
