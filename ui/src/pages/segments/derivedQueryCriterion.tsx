import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { segmentLibrary } from "../../api/client";
import { EntityMultiSelector } from "../../components/EntityMultiSelector";
import type { FilterDialogCustomSection } from "../../components/filterCriteriaTypes";
import type { SegmentDerivedQueryDescriptor, SegmentSpanOperand, SegmentSpanOperator } from "../../api/types";
import type { AppliedDerivedQuery, DerivedSpanOperandFilterValue, DerivedSpanQueryFilterValue } from "./types";

export function createDerivedSpanCustomFilterSection(scopeVideoIds: number[]): FilterDialogCustomSection {
  return {
    id: "derivedSpanQuery",
    label: "Derived Segments",
    filterKey: "derivedSpanQuery",
    defaultValue: createDefaultDerivedSpanQueryFilter(),
    isActive: isDerivedSpanQueryFilterActive,
    shouldKeepDraft: () => true,
    summarize: summarizeDerivedSpanQuery,
    renderEditor: (value, onChange) => (
      <DerivedSpanQueryEditor
        value={readDerivedSpanQueryFilter(value)}
        onChange={(nextValue) => onChange(nextValue)}
        scopeVideoIds={scopeVideoIds}
      />
    ),
  };
}

export function createDefaultDerivedSpanQueryFilter(): DerivedSpanQueryFilterValue {
  return {
    operator: "intersection",
    operands: [createEmptyDerivedSpanOperand(), createEmptyDerivedSpanOperand()],
    mergeGapSec: undefined,
    minDurationSec: undefined,
  };
}

export function readDerivedSpanQueryFilter(value: unknown): DerivedSpanQueryFilterValue {
  const fallback = createDefaultDerivedSpanQueryFilter();
  if (!value || typeof value !== "object") {
    return fallback;
  }

  const candidate = value as {
    operator?: unknown;
    operands?: unknown;
    mergeGapSec?: unknown;
    minDurationSec?: unknown;
  };
  const operator = candidate.operator === "union" || candidate.operator === "difference" || candidate.operator === "intersection"
    ? candidate.operator
    : fallback.operator;
  const operands = Array.isArray(candidate.operands)
    ? candidate.operands.map(readDerivedSpanOperandFilter)
    : fallback.operands;

  return {
    operator,
    operands: operands.length > 0 ? operands : fallback.operands,
    mergeGapSec: normalizeFiniteNumber(candidate.mergeGapSec),
    minDurationSec: normalizeFiniteNumber(candidate.minDurationSec),
  };
}

export function isDerivedSpanQueryFilterActive(value: unknown) {
  const filter = readDerivedSpanQueryFilter(value);
  return filter.operands.some(isDerivedSpanOperandFilterActive);
}

export function summarizeDerivedSpanQuery(value: unknown) {
  const filter = readDerivedSpanQueryFilter(value);
  const activeOperandCount = filter.operands.filter(isDerivedSpanOperandFilterActive).length;
  if (activeOperandCount === 0) {
    return "Segments";
  }

  return `${formatOperatorLabel(filter.operator)} · ${activeOperandCount} operand${activeOperandCount === 1 ? "" : "s"}`;
}

export function buildAppliedDerivedQuery(
  filter: DerivedSpanQueryFilterValue,
  performerFaceIdsByPerformer: Map<number, number[]>,
): AppliedDerivedQuery | null {
  const operands = filter.operands
    .map((operand) => buildAppliedDerivedOperand(operand, performerFaceIdsByPerformer))
    .filter((operand): operand is SegmentSpanOperand => operand != null);

  if (operands.length === 0) {
    return null;
  }

  return {
    operator: filter.operator,
    operands,
    mergeGapSec: filter.mergeGapSec,
    minDurationSec: filter.minDurationSec,
  };
}

export function buildDerivedQueryDescriptor(filter: DerivedSpanQueryFilterValue): SegmentDerivedQueryDescriptor | undefined {
  const operands = filter.operands
    .filter(isDerivedSpanOperandFilterActive)
    .map((operand) => ({
      sourceKey: operand.sourceKey,
      kind: operand.kind,
      tagIds: operand.tagIds.length > 0 ? operand.tagIds : undefined,
      performerIds: operand.performerIds.length > 0 ? operand.performerIds : undefined,
      faceIds: operand.faceIds.length > 0 ? operand.faceIds : undefined,
      minConfidence: operand.minConfidence,
    }));

  if (operands.length === 0) {
    return undefined;
  }

  return {
    operator: filter.operator,
    operands,
    mergeGapSec: filter.mergeGapSec,
    minDurationSec: filter.minDurationSec,
  };
}

export function formatOperatorLabel(operator: SegmentSpanOperator) {
  switch (operator) {
    case "union":
      return "Union";
    case "difference":
      return "Difference";
    case "intersection":
    default:
      return "Intersection";
  }
}

function createEmptyDerivedSpanOperand(): DerivedSpanOperandFilterValue {
  return {
    sourceKey: undefined,
    kind: undefined,
    tagIds: [],
    performerIds: [],
    faceIds: [],
    minConfidence: undefined,
  };
}

function readDerivedSpanOperandFilter(value: unknown): DerivedSpanOperandFilterValue {
  if (!value || typeof value !== "object") {
    return createEmptyDerivedSpanOperand();
  }

  const operand = value as {
    sourceKey?: unknown;
    kind?: unknown;
    tagIds?: unknown;
    performerIds?: unknown;
    faceIds?: unknown;
    minConfidence?: unknown;
  };

  return {
    sourceKey: typeof operand.sourceKey === "string" && operand.sourceKey.trim().length > 0 ? operand.sourceKey.trim() : undefined,
    kind: typeof operand.kind === "string" && operand.kind.trim().length > 0 ? operand.kind.trim() : undefined,
    tagIds: normalizeIdArray(operand.tagIds),
    performerIds: normalizeIdArray(operand.performerIds),
    faceIds: normalizeIdArray(operand.faceIds),
    minConfidence: normalizeFiniteNumber(operand.minConfidence),
  };
}

function isDerivedSpanOperandFilterActive(operand: DerivedSpanOperandFilterValue) {
  return Boolean(
    operand.sourceKey
    || operand.kind
    || operand.tagIds.length > 0
    || operand.performerIds.length > 0
    || operand.faceIds.length > 0
    || operand.minConfidence != null,
  );
}

function buildAppliedDerivedOperand(
  operand: DerivedSpanOperandFilterValue,
  performerFaceIdsByPerformer: Map<number, number[]>,
): SegmentSpanOperand | null {
  const linkedFaceIds = operand.performerIds.flatMap((performerId) => performerFaceIdsByPerformer.get(performerId) ?? []);
  const refIds = Array.from(new Set([...operand.faceIds, ...linkedFaceIds]));

  if (operand.performerIds.length > 0 && refIds.length === 0) {
    refIds.push(-1);
  }

  if (!operand.sourceKey && !operand.kind && operand.tagIds.length === 0 && refIds.length === 0 && operand.minConfidence == null) {
    return null;
  }

  return {
    sourceKey: operand.sourceKey,
    kind: operand.kind,
    tagIds: operand.tagIds.length > 0 ? operand.tagIds : undefined,
    refIds: refIds.length > 0 ? refIds : undefined,
    minConfidence: operand.minConfidence,
  };
}

function normalizeIdArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is number => typeof item === "number" && Number.isFinite(item) && item > 0)
    : [];
}

function normalizeFiniteNumber(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function DerivedSpanQueryEditor({
  value,
  onChange,
  scopeVideoIds,
}: {
  value: DerivedSpanQueryFilterValue;
  onChange: (value: DerivedSpanQueryFilterValue) => void;
  scopeVideoIds: number[];
}) {
  const optionsQuery = useQuery({
    queryKey: ["segments-page", "operand-options", scopeVideoIds.join(",")],
    queryFn: async () => {
      const response = await segmentLibrary.list({
        videoIds: scopeVideoIds.length > 0 ? scopeVideoIds.join(",") : undefined,
        perPage: 5000,
      });

      const sourceKeys = Array.from(new Set(response.items.map((segment) => segment.sourceKey?.trim()).filter((option): option is string => Boolean(option)))).sort((left, right) => left.localeCompare(right));
      const kinds = Array.from(new Set(response.items.map((segment) => segment.kind?.trim()).filter((option): option is string => Boolean(option)))).sort((left, right) => left.localeCompare(right));

      return { sourceKeys, kinds };
    },
    staleTime: 60_000,
  });

  const sourceOptions = optionsQuery.data?.sourceKeys ?? [];
  const kindOptions = useMemo(() => Array.from(new Set(["tag", "performer", "face", ...(optionsQuery.data?.kinds ?? [])])), [optionsQuery.data?.kinds]);
  const optionsLoading = optionsQuery.isLoading;

  const updateOperand = (index: number, patch: Partial<DerivedSpanOperandFilterValue>) => {
    onChange({
      ...value,
      operands: value.operands.map((operand, operandIndex) => (
        operandIndex === index ? { ...operand, ...patch } : operand
      )),
    });
  };

  return (
    <div className="space-y-4">
      <p className="text-xs text-secondary">Build derived span combinations inside Filters so intersections, unions, and performer or face matches stay part of the page’s filter state.</p>

      <div className="grid gap-3 md:grid-cols-3">
        <label className="space-y-1.5 text-sm font-medium text-secondary">
          <span>Operator</span>
          <select
            value={value.operator}
            onChange={(event) => onChange({ ...value, operator: event.target.value as SegmentSpanOperator })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
          >
            <option value="intersection">Intersection</option>
            <option value="union">Union</option>
            <option value="difference">Difference</option>
          </select>
        </label>
        <label className="space-y-1.5 text-sm font-medium text-secondary">
          <span>Merge gap (sec)</span>
          <input
            type="number"
            min="0"
            step="0.1"
            value={value.mergeGapSec ?? ""}
            onChange={(event) => onChange({ ...value, mergeGapSec: parseOptionalNumber(event.target.value) })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            placeholder="Optional"
          />
        </label>
        <label className="space-y-1.5 text-sm font-medium text-secondary">
          <span>Minimum duration (sec)</span>
          <input
            type="number"
            min="0"
            step="0.1"
            value={value.minDurationSec ?? ""}
            onChange={(event) => onChange({ ...value, minDurationSec: parseOptionalNumber(event.target.value) })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            placeholder="Optional"
          />
        </label>
      </div>

      <div className="space-y-3">
        {value.operands.map((operand, index) => (
          <div key={index} className="rounded-xl border border-border/70 bg-input/30 p-3">
            <div className="flex items-center justify-between gap-3">
              <div className="text-sm font-medium text-secondary">Operand {index + 1}</div>
              {value.operands.length > 2 ? (
                <button
                  type="button"
                  onClick={() => onChange({ ...value, operands: value.operands.filter((_, operandIndex) => operandIndex !== index) })}
                  className="min-h-9 rounded-lg px-3 py-1.5 text-sm text-secondary hover:bg-card hover:text-foreground"
                >
                  Remove operand
                </button>
              ) : null}
            </div>

            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <label className="space-y-1.5 text-sm font-medium text-secondary">
                <span>Source</span>
                <select
                  value={operand.sourceKey ?? ""}
                  onChange={(event) => updateOperand(index, { sourceKey: event.target.value || undefined })}
                  className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base font-normal text-foreground focus:border-accent focus:outline-none md:text-sm"
                >
                  <option value="">Any source</option>
                  {optionsLoading && sourceOptions.length === 0 ? <option value="" disabled>Loading sources...</option> : null}
                  {sourceOptions.map((sourceKey) => (
                    <option key={sourceKey} value={sourceKey}>{sourceKey}</option>
                  ))}
                </select>
              </label>
              <label className="space-y-1.5 text-sm font-medium text-secondary">
                <span>Kind</span>
                <select
                  value={operand.kind ?? ""}
                  onChange={(event) => {
                    const nextKind = event.target.value || undefined;
                    const selectorKind = normalizeOperandSelectorKind(nextKind);
                    updateOperand(index, {
                      kind: nextKind,
                      tagIds: selectorKind === "tag" ? operand.tagIds : [],
                      performerIds: selectorKind === "performer" ? operand.performerIds : [],
                      faceIds: selectorKind === "face" ? operand.faceIds : [],
                    });
                  }}
                  className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base font-normal text-foreground focus:border-accent focus:outline-none md:text-sm"
                >
                  <option value="">Any kind</option>
                  {optionsLoading && kindOptions.length === 0 ? <option value="" disabled>Loading kinds...</option> : null}
                  {kindOptions.map((kind) => (
                    <option key={kind} value={kind}>{kind}</option>
                  ))}
                </select>
              </label>
              <label className="space-y-1.5 text-sm font-medium text-secondary">
                <span>Minimum confidence</span>
                <input
                  type="number"
                  min="0"
                  max="1"
                  step="0.01"
                  value={operand.minConfidence ?? ""}
                  onChange={(event) => updateOperand(index, { minConfidence: parseOptionalNumber(event.target.value) })}
                  className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base font-normal text-foreground focus:border-accent focus:outline-none md:text-sm"
                  placeholder="Optional"
                />
              </label>
            </div>

            <OperandEntitySelector operand={operand} onChange={(patch) => updateOperand(index, patch)} />
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={() => onChange({ ...value, operands: [...value.operands, createEmptyDerivedSpanOperand()] })}
        className="min-h-9 rounded-lg border border-border px-3 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground"
      >
        Add operand
      </button>
    </div>
  );
}

function OperandEntitySelector({ operand, onChange }: { operand: DerivedSpanOperandFilterValue; onChange: (patch: Partial<DerivedSpanOperandFilterValue>) => void }) {
  const selectorKind = normalizeOperandSelectorKind(operand.kind);

  if (selectorKind === "tag") {
    return (
      <div className="mt-3 max-w-xl space-y-1">
        <div className="text-sm font-medium text-secondary">Tags</div>
        <EntityMultiSelector entityType="tags" values={operand.tagIds} onChange={(tagIds) => onChange({ tagIds })} placeholder="Search tags..." emptyMessage="No tags found" />
      </div>
    );
  }

  if (selectorKind === "performer") {
    return (
      <div className="mt-3 max-w-xl space-y-1">
        <div className="text-sm font-medium text-secondary">Performers</div>
        <EntityMultiSelector entityType="performers" values={operand.performerIds} onChange={(performerIds) => onChange({ performerIds })} placeholder="Search performers..." emptyMessage="No performers found" />
        <p className="text-[11px] text-muted">Performer matches use linked faces automatically.</p>
      </div>
    );
  }

  if (selectorKind === "face") {
    return (
      <div className="mt-3 max-w-xl space-y-1">
        <div className="text-sm font-medium text-secondary">Faces</div>
        <EntityMultiSelector entityType="faces" values={operand.faceIds} onChange={(faceIds) => onChange({ faceIds })} placeholder="Search faces..." emptyMessage="No faces found" />
      </div>
    );
  }

  return (
    <div className="mt-3 rounded-lg border border-border bg-surface/40 px-3 py-2 text-sm text-secondary">
      Select a segment type to choose matching tags, performers, or faces.
    </div>
  );
}

function normalizeOperandSelectorKind(kind?: string) {
  const normalized = kind?.trim().toLowerCase();
  return normalized === "tag" || normalized === "performer" || normalized === "face" ? normalized : undefined;
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return undefined;
  }

  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : undefined;
}
