import type {
  SegmentDisplayRule,
  SegmentDisplayRuleCreate,
  SegmentDistinctValue,
  SegmentHostType,
} from "../../../api/types";

export interface ProfileFormState {
  name: string;
  description: string;
  isDefault: boolean;
}

export interface RuleFormState {
  sourceKey: string;
  kind: string;
  tagId?: number;
  hostType: "" | SegmentHostType;
  visible: boolean;
  minConfidence?: number;
  minDurationSec?: number;
  mergeGapSec?: number;
  collapseToInstant: boolean;
  useCustomColor: boolean;
  colorOverride: string;
  lane?: number;
}

export interface BulkWizardState {
  tagIds: number[];
  visible: boolean;
  minConfidence?: number;
  mergeGapSec?: number;
  minDurationSec?: number;
  lane?: number;
  useCustomColor: boolean;
  colorOverride: string;
}

export const HOST_TYPE_OPTIONS = [
  { value: "video", label: "Video" },
  { value: "image", label: "Image" },
  { value: "audio", label: "Audio" },
];

export function emptyProfileForm(): ProfileFormState {
  return {
    name: "",
    description: "",
    isDefault: false,
  };
}

export function emptyRuleForm(): RuleFormState {
  return {
    sourceKey: "",
    kind: "",
    tagId: undefined,
    hostType: "",
    visible: true,
    minConfidence: undefined,
    minDurationSec: undefined,
    mergeGapSec: undefined,
    collapseToInstant: false,
    useCustomColor: false,
    colorOverride: "#3b82f6",
    lane: undefined,
  };
}

export function emptyBulkWizardForm(): BulkWizardState {
  return {
    tagIds: [],
    visible: true,
    minConfidence: undefined,
    mergeGapSec: undefined,
    minDurationSec: undefined,
    lane: undefined,
    useCustomColor: false,
    colorOverride: "#3b82f6",
  };
}

export function buildDistinctOptions(items: SegmentDistinctValue[], currentValue: string) {
  const options = items.map((item) => ({ value: item.value, label: `${item.value} (${item.count})` }));
  if (currentValue && !options.some((option) => option.value === currentValue)) {
    return [{ value: currentValue, label: currentValue }, ...options];
  }

  return options;
}

export function formatRuleTitle(rule: SegmentDisplayRule) {
  const parts = [
    rule.tagName ?? (rule.tagId != null ? `Tag #${rule.tagId}` : undefined),
    rule.sourceKey,
    rule.kind,
    rule.hostType,
  ].filter(Boolean);
  return parts.length > 0 ? parts.join(" · ") : `Rule #${rule.id}`;
}

export function ruleToPayload(rule: SegmentDisplayRule): SegmentDisplayRuleCreate {
  return {
    sourceKey: rule.sourceKey,
    kind: rule.kind,
    tagId: rule.tagId,
    tagCategory: rule.tagCategory,
    hostType: rule.hostType,
    visible: rule.visible,
    minConfidence: rule.minConfidence,
    minDurationSec: rule.minDurationSec,
    mergeGapSec: rule.mergeGapSec,
    collapseToInstant: rule.collapseToInstant,
    colorOverride: rule.colorOverride,
    lane: rule.lane,
    priority: rule.priority,
  };
}

export function ruleFormToPayload(form: RuleFormState, priority: number): SegmentDisplayRuleCreate {
  return {
    sourceKey: form.sourceKey || undefined,
    kind: form.kind || undefined,
    tagId: form.tagId,
    hostType: form.hostType || undefined,
    visible: form.visible,
    minConfidence: form.minConfidence,
    minDurationSec: form.minDurationSec,
    mergeGapSec: form.mergeGapSec,
    collapseToInstant: form.collapseToInstant,
    colorOverride: form.useCustomColor ? form.colorOverride : undefined,
    lane: form.lane,
    priority,
  };
}

export function isRulePayloadMeaningful(rule: SegmentDisplayRuleCreate) {
  return !!(rule.sourceKey || rule.kind || rule.tagId != null || rule.hostType);
}

export function normalizeRulePayloads(rules: SegmentDisplayRuleCreate[]) {
  const total = rules.length;
  return rules.map((rule, index) => ({ ...rule, priority: total - index }));
}

export function getNextPriority(rules: SegmentDisplayRule[]) {
  return Math.max(1, ...rules.map((rule) => (rule.priority ?? 0) + 1));
}

export function formatSeconds(value: number) {
  return `${value.toFixed(2)}s`;
}
