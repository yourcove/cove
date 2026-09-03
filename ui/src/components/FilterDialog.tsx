import { useState, useMemo, useCallback, useEffect, useId, useRef, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { X, Search, Pin, PinOff, Plus, Star, ArrowLeft, Film, Users, Workflow, ChevronDown, ChevronUp } from "lucide-react";
import { metadata } from "../api/client";
import { IsoDateInput } from "./IsoDateInput";
import { EntityReferenceSelector } from "./EntityReferenceSelector";
import {
  convertToRatingFormat,
  convertFromRatingFormat,
  getRatingMax,
  getRatingStep,
  getRatingPrecision,
  useRatingOptions,
} from "./Rating";
import type {
  CriterionModifier,
  IntCriterion,
  StringCriterion,
  BoolCriterion,
  MultiIdCriterion,
  DateCriterion,
  TimestampCriterion,
  FingerprintCriterion,
  TagDurationCriterion,
  MetadataServer,
  RelatedFilterCriterion,
  FilterExpression,
} from "../api/types";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import {
  ActiveObjectFilterChips,
  getFilterChipTargetKey,
  removeObjectFilterChipTarget,
  type FilterChipTarget,
  type RelatedFilterChipFacet,
} from "./ActiveObjectFilterChips";
import { pushOverlay } from "../utils/overlayState";
import { LibraryFolderTree } from "./LibraryFolderTree";
import {
  FILTER_EXPRESSION_STATE_KEY,
  countFilterExpressionConditions,
  getExpressionGroup,
  getExpressionLeaf,
  isComplexFilterExpression,
  normalizeFilterExpressionForEditing,
  removeExpressionLeafAndPrune,
  removeExpressionGroup,
  replaceExpressionGroup,
  updateExpressionLeaf,
  type EditableFilterExpression,
} from "../utils/filterExpressionTree";
import type { CriterionDefinition, CriterionType, EntityType, FilterDialogCustomSection } from "./filterCriteriaTypes";
import { FilterExpressionEditor as FilterExpressionEditorView, MAX_FILTER_EXPRESSION_CONDITIONS } from "./FilterExpressionEditor";
import { describeFilterExpressionCondition } from "./filterExpressionExplanation";
import { RelatedFilterWorkspace } from "./RelatedFilterWorkspace";
import { TagDurationEditor } from "./TagDurationFilterEditor";
import { MultiIdEditor } from "./MultiIdFilterEditor";
import {
  CareerLengthInput,
  DurationInput,
  LabeledControl,
  ModifierSelector,
  PercentInput,
  ResolutionSelect,
} from "./filterEditorControls";
import { getFirstEditorControl, getFirstInlineEditorControl } from "./filterEditorFocus";
import {
  NULL_VALUE_MODIFIERS,
  countValidFilterExpressionConditions,
  expressionHasActiveCriterion,
  expressionPassthroughFilter,
  getCriterionFilterValue,
  getExpressionConditionCriterion,
  isCriterionValueValid,
  mergeFilterExpressionWithSimpleCriteria,
  migrateLegacyPerformerFavoriteCriterion,
  removeCriterionFilterValue,
  sanitizeFilterCriteria,
  sanitizeFilterExpression,
  setCriterionFilterValue,
} from "./filterCriterionState";

export {
  AUDIO_CRITERIA,
  GALLERY_CRITERIA,
  GROUP_CRITERIA,
  IMAGE_CRITERIA,
  PERFORMER_CRITERIA,
  STUDIO_CRITERIA,
  TAG_CRITERIA,
  TEXT_CRITERIA,
  VIDEO_CRITERIA,
} from "./filterCriteriaCatalogs";
export { migrateLegacyPerformerFavoriteCriterion } from "./filterCriterionState";

export type { CriterionDefinition, CriterionType, EntityType, FilterDialogCustomSection } from "./filterCriteriaTypes";

// ===== Criterion definitions =====

// Which modifiers each type supports
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


// ===== Filter Dialog =====

export type FilterDialogPreselection = string | {
  criterionId: string;
  relatedFacet?: RelatedFilterChipFacet;
  nestedCriterionId?: string;
};

interface FilterDialogProps {
  open: boolean;
  onClose: () => void;
  criteria: CriterionDefinition[];
  activeFilter: Record<string, unknown>;
  onApply: (filter: Record<string, unknown>) => void;
  preselectCriterion?: FilterDialogPreselection;
  customSections?: FilterDialogCustomSection[];
  showCustomSectionDivider?: boolean;
  supportsFilterExpressions?: boolean;
  initialView?: "simple" | "advanced";
  initialExpressionPath?: number[];
  subjectLabel?: string;
  openAtRoot?: boolean;
}

type FilterDialogView = "simple" | "expression";

interface ExpressionConditionDraft {
  filter: Record<string, unknown>;
  path?: number[];
  parentPath: number[];
  isNew: boolean;
  returnView: "simple" | "expression";
  implicitRootAnd?: boolean;
}

export function FilterDialog({ open, onClose, criteria, activeFilter, onApply, preselectCriterion, customSections, showCustomSectionDivider = true, supportsFilterExpressions = false, initialView = "simple", initialExpressionPath, subjectLabel = "items", openAtRoot = false }: FilterDialogProps) {
  const supportsExpressions = supportsFilterExpressions;
  const [editFilter, setEditFilter] = useState<Record<string, unknown>>({ ...activeFilter });
  const [dialogView, setDialogView] = useState<FilterDialogView>(() => initialView === "advanced" && isComplexFilterExpression(activeFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined) ? "expression" : "simple");
  const [selectedFiltersCollapsed, setSelectedFiltersCollapsed] = useState(false);
  const [conditionDraft, setConditionDraft] = useState<ExpressionConditionDraft | null>(null);
  const [simpleExpressionGroupPath, setSimpleExpressionGroupPath] = useState<number[]>(() => initialExpressionPath?.slice(0, -1) ?? []);
  const [inlineStackReturnsToExpression, setInlineStackReturnsToExpression] = useState(false);
  const backdropPointerDownRef = useRef(false);
  const [search, setSearch] = useState("");
  const [expandedCriterion, setExpandedCriterion] = useState<string | null>(null);
  const [relatedWorkspaceSelection, setRelatedWorkspaceSelection] = useState<{ facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null>(null);
  const [navigatorFocusId, setNavigatorFocusId] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const selectedFiltersToolbarRef = useRef<HTMLDivElement>(null);
  const selectedFiltersLastFocusedRef = useRef<HTMLButtonElement | null>(null);
  const selectedFiltersLastFocusedIndexRef = useRef(0);
  const selectedFiltersInstructionsId = useId();
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const viewReturnFocusRef = useRef<HTMLElement | null>(null);
  const simpleReturnFocusKeyRef = useRef<string | null>(null);
  const pendingRelatedWorkspaceReturnFocusRef = useRef(false);
  const expressionReturnFocusKeyRef = useRef<string | null>(null);
  const backButtonRef = useRef<HTMLButtonElement>(null);
  const criterionButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pinButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pendingPinFocusRef = useRef<string | null>(null);
  const inlineAddedConditionRef = useRef<{ path: number[]; originPath: number[]; unwrapRootOnRemove: boolean } | null>(null);
  const wasOpenRef = useRef(false);
  const activeFilterSignature = useMemo(() => JSON.stringify(activeFilter ?? {}), [activeFilter]);
  const normalizedActiveFilter = useMemo(
    () => normalizeFilterExpressionForEditing(migrateLegacyPerformerFavoriteCriterion(JSON.parse(activeFilterSignature) as Record<string, unknown>, criteria)),
    [activeFilterSignature, criteria],
  );
  const lastActiveFilterSignatureRef = useRef(activeFilterSignature);
  const [pinnedIds, setPinnedIds] = useState<Set<string>>(() => {
    try {
      const stored = localStorage.getItem("filter-pinned");
      return stored ? new Set(JSON.parse(stored)) : new Set<string>();
    } catch {
      return new Set<string>();
    }
  });

  const togglePin = useCallback(
    (id: string) => {
      pendingPinFocusRef.current = id;
      setPinnedIds((prev) => {
        const next = new Set(prev);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        localStorage.setItem("filter-pinned", JSON.stringify([...next]));
        return next;
      });
    },
    []
  );

  useEffect(() => {
    const id = pendingPinFocusRef.current;
    if (!id) return;
    pendingPinFocusRef.current = null;
    criterionButtonRefs.current.get(id)?.focus();
  }, [pinnedIds]);

  const filteredCriteria = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (q ? criteria.filter((c) => c.label.toLowerCase().includes(q)) : criteria)
      .filter((criterion) => !conditionDraft || (criterion.supported !== false && criterion.expressionSupported !== false))
      .slice()
      .sort((a, b) => {
        const aExact = Boolean(q) && a.label.toLowerCase() === q;
        const bExact = Boolean(q) && b.label.toLowerCase() === q;
        if (aExact !== bExact) return aExact ? -1 : 1;
        return a.label.localeCompare(b.label);
      });
  }, [conditionDraft, criteria, search]);

  const activeCriterionCount = useMemo(() => {
    const criteriaCount = criteria.filter((criterion) => isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)).length;
    const customCount = (customSections ?? []).filter((section) => section.isActive(editFilter[section.filterKey])).length;
    return criteriaCount + customCount;
  }, [criteria, customSections, editFilter]);

  const activeEditFilter = useMemo(() => {
    const sectionFilter: Record<string, unknown> = {};
    for (const section of customSections ?? []) {
      const value = section.sanitize ? section.sanitize(editFilter[section.filterKey]) : editFilter[section.filterKey];
      if (section.isActive(value)) sectionFilter[section.filterKey] = value;
    }
    return sanitizeFilterCriteria(editFilter, criteria, sectionFilter);
  }, [criteria, customSections, editFilter]);
  const expression = editFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
  const expressionConditionCount = countFilterExpressionConditions(expression);
  const hasComplexExpression = isComplexFilterExpression(expression);
  const simpleExpressionChildren = expression && !hasComplexExpression ? expression.children : [];
  const directlyEditableExpressionGroup = expression ? getExpressionGroup(expression, simpleExpressionGroupPath) : undefined;
  const directlyEditableExpressionChildren = directlyEditableExpressionGroup?.children ?? [];
  const validSimpleExpressionEntries = simpleExpressionChildren.flatMap((child, index) => child.filter
    && Object.keys(sanitizeFilterCriteria(child.filter, criteria)).length > 0 ? [{ child, index }] : []);
  const displayedActiveCount = activeCriterionCount + countValidFilterExpressionConditions(expression, criteria);

  type NavigatorItem =
    | { kind: "criterion"; id: string; label: string; active: boolean; pinned: boolean; criterion: CriterionDefinition }
    | { kind: "custom"; id: string; label: string; active: boolean; pinned: false; section: FilterDialogCustomSection };

  const navigatorGroups = useMemo(() => {
    const q = search.trim().toLowerCase();
    const sortItems = (a: NavigatorItem, b: NavigatorItem) => {
      const aExact = Boolean(q) && a.label.toLowerCase() === q;
      const bExact = Boolean(q) && b.label.toLowerCase() === q;
      if (aExact !== bExact) return aExact ? -1 : 1;
      return a.label.localeCompare(b.label);
    };
    const criterionItems: NavigatorItem[] = filteredCriteria.map((criterion) => ({
      kind: "criterion",
      id: criterion.id,
      label: criterion.label,
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)
        || expressionHasActiveCriterion(expression, criterion),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    }));
    const customItems: NavigatorItem[] = (conditionDraft ? [] : customSections ?? [])
      .filter((section) => !q || section.label.toLowerCase().includes(q))
      .map((section) => ({
        kind: "custom",
        id: section.id,
        label: section.label,
        active: section.isActive(editFilter[section.filterKey]),
        pinned: false,
        section,
      }));
    const items = [...customItems, ...criterionItems];
    const pinned = items.filter((item) => item.pinned).sort(sortItems);
    const related = items.filter((item) => !item.pinned && item.kind === "criterion" && item.criterion.category === "related").sort(sortItems);
    const remaining = items.filter((item) => !item.pinned && !(item.kind === "criterion" && item.criterion.category === "related")).sort(sortItems);
    return [
      { label: "Pinned", items: pinned },
      { label: "Related items", items: related },
      { label: "All filters", items: remaining },
    ].filter((group) => group.items.length > 0);
  }, [criteria, customSections, editFilter, expression, filteredCriteria, pinnedIds, search]);

  const visibleNavigatorItems = useMemo(() => navigatorGroups.flatMap((group) => group.items), [navigatorGroups]);
  const rovingNavigatorId = navigatorFocusId && visibleNavigatorItems.some((item) => item.id === navigatorFocusId)
    ? navigatorFocusId
    : expandedCriterion && visibleNavigatorItems.some((item) => item.id === expandedCriterion)
    ? expandedCriterion
    : visibleNavigatorItems[0]?.id;
  const selectedItem = useMemo(() => {
    if (!expandedCriterion) return undefined;
    const section = (customSections ?? []).find((item) => item.id === expandedCriterion);
    if (section) {
      return {
        kind: "custom" as const,
        id: section.id,
        label: section.label,
        active: section.isActive(editFilter[section.filterKey]),
        pinned: false as const,
        section,
      };
    }
    const criterion = criteria.find((item) => item.id === expandedCriterion);
    return criterion ? {
      kind: "criterion" as const,
      id: criterion.id,
      label: criterion.label,
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)
        || expressionHasActiveCriterion(expression, criterion),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    } : undefined;
  }, [criteria, customSections, editFilter, expandedCriterion, expression, pinnedIds]);
  const relatedWorkspaceCriterion = selectedItem?.kind === "criterion" && selectedItem.criterion.type === "related"
    ? selectedItem.criterion
    : undefined;
  const relatedExpressionInstances = relatedWorkspaceCriterion ? directlyEditableExpressionChildren.flatMap((child, index) =>
    child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === relatedWorkspaceCriterion.id
      ? [{ index, path: [...simpleExpressionGroupPath, index], filter: child.filter }]
      : []) : [];
  const relatedWorkspaceObjectFilter = conditionDraft && relatedWorkspaceCriterion
    ? conditionDraft.filter
    : relatedExpressionInstances.length > 0 ? relatedExpressionInstances[0].filter : editFilter;
  const selectedCompactCriterion = selectedItem?.kind === "criterion" && selectedItem.criterion.type !== "related"
    ? selectedItem.criterion
    : undefined;
  const selectedExpressionInstances = selectedCompactCriterion ? directlyEditableExpressionChildren.flatMap((child, index) =>
    child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === selectedCompactCriterion.id
      ? [{ index, path: [...simpleExpressionGroupPath, index], filter: child.filter }]
      : []) : [];
  const selectedSingleExpressionInstance = selectedExpressionInstances.length === 1 ? selectedExpressionInstances[0] : undefined;
  const selectedHasIncompleteExpressionInstance = Boolean(selectedCompactCriterion && selectedExpressionInstances.some((instance) =>
    !isCriterionValueValid(getCriterionFilterValue(instance.filter, selectedCompactCriterion), selectedCompactCriterion)));
  const showInlineConditionStack = Boolean(!conditionDraft && selectedCompactCriterion && (
    selectedExpressionInstances.length > 1
    || selectedHasIncompleteExpressionInstance
  ));

  const cloneActiveFilter = useCallback(
    () => JSON.parse(JSON.stringify(normalizedActiveFilter)) as Record<string, unknown>,
    [normalizedActiveFilter],
  );

  const focusFirstEditorControl = useCallback(() => {
    window.setTimeout(() => {
      const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstEditorControl(panel)?.focus();
    }, 0);
  }, []);

  const focusFirstConditionEditorControl = useCallback(() => {
    window.setTimeout(() => window.setTimeout(() => {
      const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstInlineEditorControl(panel)?.focus();
    }, 0), 0);
  }, []);

  const focusInlineCondition = (index: number) => {
    window.setTimeout(() => window.setTimeout(() => {
      const condition = dialogRef.current?.querySelector<HTMLElement>(`[data-inline-condition-index="${index}"]`);
      getFirstInlineEditorControl(condition?.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
    }, 0), 0);
  };

  const selectNavigatorItem = useCallback((id: string) => {
    const criterion = criteria.find((item) => item.id === id);
    const parentPath = conditionDraft?.parentPath ?? [];
    const parentGroup = expression ? getExpressionGroup(expression, parentPath) : undefined;
    const hasMatchingSibling = Boolean(criterion && criterion.type !== "related" && parentGroup?.children.some((child) => child.filter
      && getExpressionConditionCriterion(child.filter, criteria)?.id === id));
    if (conditionDraft?.isNew && conditionDraft.returnView === "expression" && hasMatchingSibling && parentGroup) {
      const newIndex = parentGroup.children.length;
      inlineAddedConditionRef.current = {
        path: [...parentPath, newIndex],
        originPath: parentPath,
        unwrapRootOnRemove: false,
      };
      setEditFilter((current) => {
        const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
        if (!currentExpression) return current;
        return {
          ...current,
          [FILTER_EXPRESSION_STATE_KEY]: replaceExpressionGroup(currentExpression, parentPath, (group) => ({
            ...group,
            children: [...group.children, { filter: { _criterionId: id } }],
          })),
        };
      });
      setRelatedWorkspaceSelection(null);
      setSimpleExpressionGroupPath(parentPath);
      setExpandedCriterion(id);
      setConditionDraft(null);
      setInlineStackReturnsToExpression(true);
      focusInlineCondition(newIndex);
      return;
    }
    setRelatedWorkspaceSelection(null);
    setExpandedCriterion(id);
    setConditionDraft((current) => current
      ? getExpressionConditionCriterion(current.filter, criteria)?.id === id
        ? current
        : { ...current, filter: { _criterionId: id } }
      : current);
    if (conditionDraft) focusFirstConditionEditorControl();
    else focusFirstEditorControl();
  }, [conditionDraft, criteria, expression, focusFirstConditionEditorControl, focusFirstEditorControl]);

  useEffect(() => {
    if (lastActiveFilterSignatureRef.current !== activeFilterSignature) {
      lastActiveFilterSignatureRef.current = activeFilterSignature;
      setEditFilter(cloneActiveFilter());
    }
  }, [activeFilterSignature, cloneActiveFilter]);

  useEffect(() => {
    if (!open) return;
    return pushOverlay();
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  useEffect(() => {
    if (!open) {
      inlineAddedConditionRef.current = null;
      if (wasOpenRef.current) {
        setEditFilter(cloneActiveFilter());
        setSearch("");
        setExpandedCriterion(null);
        setRelatedWorkspaceSelection(null);
        setDialogView("simple");
        setSelectedFiltersCollapsed(false);
        setConditionDraft(null);
        setSimpleExpressionGroupPath([]);
        setInlineStackReturnsToExpression(false);
        setNavigatorFocusId(null);
        previousFocusRef.current?.focus();
      }
      wasOpenRef.current = false;
      return;
    }

    if (!wasOpenRef.current) {
      inlineAddedConditionRef.current = null;
      previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const openingFilter = cloneActiveFilter();
      const openingExpression = normalizedActiveFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const openingLeaf = initialExpressionPath && openingExpression ? getExpressionLeaf(openingExpression, initialExpressionPath) : undefined;
      const openingLeafCriterion = openingLeaf ? getExpressionConditionCriterion(openingLeaf, criteria) : undefined;
      const openLeafInline = Boolean(openingLeafCriterion && openingLeafCriterion.type !== "related" && openingExpression);
      const openingView = openingLeaf ? "simple" : initialView === "advanced" && isComplexFilterExpression(openingExpression) ? "expression" : "simple";
      if (openingView === "expression") {
        const merged = mergeFilterExpressionWithSimpleCriteria(openingFilter, criteria);
        setEditFilter({ ...expressionPassthroughFilter(openingFilter, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) });
      } else {
        setEditFilter(openingFilter);
      }
      setDialogView(openingView);
      setSimpleExpressionGroupPath(initialExpressionPath?.slice(0, -1) ?? []);
      setConditionDraft(openingLeaf && !openLeafInline ? { filter: openingLeaf, path: initialExpressionPath, parentPath: initialExpressionPath!.slice(0, -1), isNew: false, returnView: "simple" } : null);
      setInlineStackReturnsToExpression(false);
      setSearch("");
      setNavigatorFocusId(null);
      const firstActive = criteria.find((criterion) => isCriterionValueValid(getCriterionFilterValue(normalizedActiveFilter, criterion), criterion))?.id
        ?? (customSections ?? []).find((section) => section.isActive(normalizedActiveFilter[section.filterKey]))?.id;
      const nextSelected = openingLeafCriterion?.id ?? (typeof preselectCriterion === "string"
        ? preselectCriterion
        : preselectCriterion?.criterionId ?? (openAtRoot ? null : firstActive) ?? null);
      setRelatedWorkspaceSelection(typeof preselectCriterion === "object"
        ? { facet: preselectCriterion.relatedFacet ?? "mode", nestedCriterionId: preselectCriterion.nestedCriterionId }
        : null);
      setExpandedCriterion(nextSelected);
      window.setTimeout(() => {
        if (openingView === "expression") backButtonRef.current?.focus();
        else if (openLeafInline && initialExpressionPath) {
          const condition = dialogRef.current?.querySelector<HTMLElement>(`[data-inline-condition-index="${initialExpressionPath.at(-1)}"]`);
          getFirstInlineEditorControl(condition?.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
        }
        else if (openingLeafCriterion?.type === "related" && typeof preselectCriterion === "object" && preselectCriterion.nestedCriterionId) {
          const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
          (panel?.querySelector<HTMLElement>("input:not([type='hidden']), select, textarea, button[aria-pressed='true']") ?? getFirstEditorControl(panel))?.focus();
        }
        else if (nextSelected && preselectCriterion) focusFirstEditorControl();
        else searchRef.current?.focus();
        if (nextSelected) criterionButtonRefs.current.get(nextSelected)?.scrollIntoView?.({ block: "center", inline: "nearest" });
      }, 0);
    }
    wasOpenRef.current = true;
  }, [cloneActiveFilter, criteria, customSections, focusFirstEditorControl, initialExpressionPath, initialView, normalizedActiveFilter, open, openAtRoot, preselectCriterion]);

  const dismiss = useCallback(() => {
    setEditFilter(cloneActiveFilter());
    setSearch("");
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    setNavigatorFocusId(null);
    onClose();
  }, [cloneActiveFilter, onClose]);

  const returnToSimpleFilters = useCallback(() => {
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => window.setTimeout(() => {
        const keyedTarget = simpleReturnFocusKeyRef.current
          ? dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="${simpleReturnFocusKeyRef.current}"]`)
          : null;
        if (keyedTarget) keyedTarget.focus();
        else if (viewReturnFocusRef.current?.isConnected) viewReturnFocusRef.current.focus();
        else searchRef.current?.focus();
      }, 0), 0);
  }, []);

  const returnToExpression = useCallback(() => {
    const addedCondition = inlineAddedConditionRef.current;
    setEditFilter((current) => {
      let next = current;
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as EditableFilterExpression | undefined;
      const addedFilter = currentExpression && addedCondition ? getExpressionLeaf(currentExpression, addedCondition.path) : undefined;
      const addedCriterion = addedFilter ? getExpressionConditionCriterion(addedFilter, criteria) : undefined;
      if (currentExpression && addedCondition && addedFilter && addedCriterion
        && !isCriterionValueValid(getCriterionFilterValue(addedFilter, addedCriterion), addedCriterion)) {
        const expression = removeExpressionLeafAndPrune(currentExpression, addedCondition.path);
        next = { ...current };
        if (expression) next[FILTER_EXPRESSION_STATE_KEY] = expression;
        else delete next[FILTER_EXPRESSION_STATE_KEY];
      }
      const merged = mergeFilterExpressionWithSimpleCriteria(next, criteria);
      return { ...expressionPassthroughFilter(next, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) };
    });
    inlineAddedConditionRef.current = null;
    setDialogView("expression");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => window.setTimeout(() => {
        const keyedTarget = expressionReturnFocusKeyRef.current
          ? dialogRef.current?.querySelector<HTMLElement>(`[data-expression-return-focus="${expressionReturnFocusKeyRef.current}"]`)
          : null;
        if (keyedTarget) keyedTarget.focus();
        else if (viewReturnFocusRef.current?.isConnected) viewReturnFocusRef.current.focus();
        else backButtonRef.current?.focus();
      }, 0), 0);
  }, [criteria]);

  const enterExpression = useCallback((returnFocusKey?: string, groupPath?: number[]) => {
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    simpleReturnFocusKeyRef.current = returnFocusKey ?? viewReturnFocusRef.current?.dataset.simpleReturnFocus ?? null;
    setEditFilter((current) => {
      const merged = mergeFilterExpressionWithSimpleCriteria(current, criteria);
      return { ...expressionPassthroughFilter(current, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) };
    });
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setInlineStackReturnsToExpression(false);
    setDialogView("expression");
    window.setTimeout(() => {
      const pathKey = groupPath?.join(".");
      const groupControl = groupPath
        ? dialogRef.current?.querySelector<HTMLElement>(`[data-expression-group-control="${pathKey}"]`)
        : null;
      (groupControl ?? backButtonRef.current)?.focus();
    }, 0);
  }, [criteria]);

  const discardExpressionCondition = useCallback(() => {
    if (conditionDraft?.returnView === "simple") {
      if (!conditionDraft.isNew && getExpressionConditionCriterion(conditionDraft.filter, criteria)?.type === "related") {
        setExpandedCriterion(null);
        setRelatedWorkspaceSelection(null);
        pendingRelatedWorkspaceReturnFocusRef.current = false;
      }
      returnToSimpleFilters();
    }
    else returnToExpression();
  }, [conditionDraft, criteria, returnToExpression, returnToSimpleFilters]);

  const exitExpressionCondition = () => {
    const criterion = conditionDraft ? getExpressionConditionCriterion(conditionDraft.filter, criteria) : undefined;
    if (conditionDraft?.isNew && criterion?.type === "related"
      && isCriterionValueValid(getCriterionFilterValue(conditionDraft.filter, criterion), criterion)) {
      saveExpressionCondition();
      return;
    }
    discardExpressionCondition();
  };

  const canAutoCommitNewRelatedCondition = (() => {
    const criterion = conditionDraft ? getExpressionConditionCriterion(conditionDraft.filter, criteria) : undefined;
    return Boolean(conditionDraft?.isNew && criterion?.type === "related"
      && isCriterionValueValid(getCriterionFilterValue(conditionDraft.filter, criterion), criterion));
  })();

  const closeRelatedWorkspace = useCallback(() => {
    const returnFocusKey = pendingRelatedWorkspaceReturnFocusRef.current ? simpleReturnFocusKeyRef.current : null;
    pendingRelatedWorkspaceReturnFocusRef.current = false;
    if (returnFocusKey) simpleReturnFocusKeyRef.current = null;
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    window.setTimeout(() => window.setTimeout(() => {
      const keyedTarget = returnFocusKey
        ? dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="${returnFocusKey}"]`)
        : null;
      (keyedTarget ?? searchRef.current)?.focus();
    }, 0), 0);
  }, []);

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        if (canAutoCommitNewRelatedCondition) {
          exitExpressionCondition();
        } else if (conditionDraft?.returnView === "simple" && !conditionDraft.isNew && relatedWorkspaceSelection) {
          exitExpressionCondition();
        } else if (relatedWorkspaceSelection) {
          setRelatedWorkspaceSelection(null);
          window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
        } else if (conditionDraft) exitExpressionCondition();
        else if (inlineStackReturnsToExpression) returnToExpression();
        else if (dialogView === "expression") returnToSimpleFilters();
        else if (relatedWorkspaceCriterion) {
          if (relatedWorkspaceSelection) {
            setRelatedWorkspaceSelection(null);
            window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
          } else {
            closeRelatedWorkspace();
          }
        } else dismiss();
        return;
      }
      if (event.key !== "Tab" || !dialogRef.current) return;
      const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(
        "button:not([disabled]):not([tabindex='-1']), input:not([disabled]):not([tabindex='-1']), select:not([disabled]):not([tabindex='-1']), textarea:not([disabled]):not([tabindex='-1']), [tabindex]:not([tabindex='-1'])",
      )).filter((element) => !element.closest("[hidden]") && element.offsetParent !== null);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [canAutoCommitNewRelatedCondition, closeRelatedWorkspace, conditionDraft, dialogView, discardExpressionCondition, dismiss, inlineStackReturnsToExpression, open, relatedWorkspaceCriterion, relatedWorkspaceSelection, returnToExpression, returnToSimpleFilters]);

  const handleRemoveCriterion = useCallback((criterion: CriterionDefinition, criterionId?: string) => {
    setEditFilter((prev) => removeCriterionFilterValue(prev, criterion));

    if (criterionId && expandedCriterion === criterionId) {
      setExpandedCriterion(null);
    }
  }, [expandedCriterion]);

  const handleSetCriterion = useCallback((criterion: CriterionDefinition, value: unknown) => {
    setEditFilter((prev) => setCriterionFilterValue(prev, criterion, value));
  }, []);

  const handleSetAuxiliaryToggle = useCallback((criterion: CriterionDefinition, checked: boolean) => {
    const auxiliaryToggleKey = criterion.auxiliaryToggleKey;
    if (!auxiliaryToggleKey) {
      return;
    }

    setEditFilter((prev) => {
      const next = { ...prev };
      if (checked) {
        next[auxiliaryToggleKey] = true;
      } else {
        delete next[auxiliaryToggleKey];
      }
      return next;
    });
  }, []);

  const handleEditChip = useCallback((target: FilterChipTarget) => {
    const key = getFilterChipTargetKey(target);
    if (key === FILTER_EXPRESSION_STATE_KEY) {
      enterExpression("advanced");
      return;
    }
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    const criterion = criteria.find((item) => item.id === key
      || item.filterKey === key
      || item.secondaryFilterKey === key
      || item.auxiliaryToggleKey === key);
    const nextId = customSection?.id ?? criterion?.id;
    if (nextId) {
      setSearch("");
      if (target.kind === "related") {
        setExpandedCriterion(nextId);
        setRelatedWorkspaceSelection({ facet: target.facet, nestedCriterionId: target.nestedCriterionId });
        focusFirstEditorControl();
      } else {
        selectNavigatorItem(nextId);
      }
    }
  }, [criteria, customSections, enterExpression, focusFirstEditorControl, selectNavigatorItem]);

  const handleRemoveChip = useCallback((target: FilterChipTarget) => {
    const key = getFilterChipTargetKey(target);
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    if (target.kind === "root" && customSection) {
      setEditFilter((current) => {
        const next = { ...current };
        delete next[customSection.filterKey];
        return next;
      });
      return;
    }
    setEditFilter((current) => removeObjectFilterChipTarget(current, criteria, target));
  }, [criteria, customSections]);

  const handleRemoveRelatedWorkspaceChip = useCallback((target: FilterChipTarget) => {
    if (conditionDraft && relatedWorkspaceCriterion && getExpressionConditionCriterion(conditionDraft.filter, criteria)?.id === relatedWorkspaceCriterion.id) {
      setConditionDraft((current) => current ? { ...current, filter: removeObjectFilterChipTarget(current.filter, criteria, target) } : current);
      return;
    }
    if (relatedExpressionInstances.length > 0) {
      const [{ path }] = relatedExpressionInstances;
      setEditFilter((current) => {
        const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
        const filter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
        if (!currentExpression || !filter) return current;
        return { ...current, [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, removeObjectFilterChipTarget(filter, criteria, target)) };
      });
      return;
    }
    handleRemoveChip(target);
  }, [conditionDraft, criteria, handleRemoveChip, relatedExpressionInstances, relatedWorkspaceCriterion]);

  const handleApply = () => {
    const hasExpression = Boolean(editFilter[FILTER_EXPRESSION_STATE_KEY]);
    const mergedExpression = hasExpression ? sanitizeFilterExpression(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria) as EditableFilterExpression | undefined, criteria) : undefined;
    onApply(hasExpression
      ? { ...expressionPassthroughFilter(editFilter, criteria), ...(mergedExpression ? { [FILTER_EXPRESSION_STATE_KEY]: mergedExpression } : {}) }
      : activeEditFilter);
    onClose();
  };

  const finishRelatedWorkspace = () => {
    setRelatedWorkspaceSelection(null);
    setExpandedCriterion(null);
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  const handleClear = () => {
    inlineAddedConditionRef.current = null;
    setEditFilter({});
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  const updateInlineCondition = (path: number[], criterion: CriterionDefinition, value: unknown) => {
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const existingFilter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
      if (!currentExpression || !existingFilter) return current;
      const nextFilter = { ...existingFilter };
      delete nextFilter[criterion.filterKey];
      if (criterion.secondaryFilterKey) delete nextFilter[criterion.secondaryFilterKey];
      const updatedFilter = {
        ...nextFilter,
        ...setCriterionFilterValue({}, criterion, value),
      };
      if (isCriterionValueValid(value, criterion)) delete updatedFilter._criterionId;
      else updatedFilter._criterionId = criterion.id;
      return {
        ...current,
        [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, updatedFilter),
      };
    });
  };

  const updateInlineAuxiliaryToggle = (path: number[], criterion: CriterionDefinition, checked: boolean) => {
    const auxiliaryToggleKey = criterion.auxiliaryToggleKey;
    if (!auxiliaryToggleKey) return;
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const filter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
      if (!currentExpression || !filter) return current;
      const nextFilter = { ...filter };
      if (checked) nextFilter[auxiliaryToggleKey] = true;
      else delete nextFilter[auxiliaryToggleKey];
      return { ...current, [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, nextFilter) };
    });
  };

  const openNewExpressionCondition = (criterionId?: string, parentPath: number[] = []) => {
    if (expression && getExpressionGroup(expression, parentPath)?.operator === "NOT") return;
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (dialogView === "simple") simpleReturnFocusKeyRef.current = criterionId ? `repeat-${criterionId}` : null;
    else expressionReturnFocusKeyRef.current = `add-${parentPath.join(".")}`;
    setConditionDraft({ filter: criterionId ? { _criterionId: criterionId } : {}, parentPath, isNew: true, returnView: dialogView === "simple" ? "simple" : "expression" });
    setExpandedCriterion(criterionId ?? null);
    setDialogView("simple");
    window.setTimeout(() => criterionId ? focusFirstConditionEditorControl() : searchRef.current?.focus(), 0);
  };

  const openExpressionCondition = (path: number[], returnView: "simple" | "expression" = "expression", simpleReturnFocusKey?: string) => {
    if (!expression) return;
    const filter = getExpressionLeaf(expression, path);
    if (!filter) return;
    const criterion = getExpressionConditionCriterion(filter, criteria);
    if (returnView === "expression" && !expression.children.some((child) => child.group) && criterion?.type !== "related") {
      expressionReturnFocusKeyRef.current = `edit-${path.join(".")}`;
      setConditionDraft(null);
      setInlineStackReturnsToExpression(true);
      setExpandedCriterion(criterion?.id ?? null);
      setDialogView("simple");
      focusInlineCondition(path[0]);
      return;
    }
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (returnView === "simple") {
      simpleReturnFocusKeyRef.current = simpleReturnFocusKey ?? `expression-${path.join(".")}`;
      pendingRelatedWorkspaceReturnFocusRef.current = criterion?.type === "related";
    }
    else expressionReturnFocusKeyRef.current = `edit-${path.join(".")}`;
    setConditionDraft({ filter, path, parentPath: path.slice(0, -1), isNew: false, returnView: returnView === "simple" ? "simple" : "expression" });
    setExpandedCriterion(criterion?.id ?? null);
    setDialogView("simple");
    window.setTimeout(() => criterion ? focusFirstConditionEditorControl() : searchRef.current?.focus(), 0);
  };

  const openSimpleExpressionCondition = (path: number[]) => {
    if (!expression) return;
    const filter = getExpressionLeaf(expression, path);
    const criterion = filter ? getExpressionConditionCriterion(filter, criteria) : undefined;
    if (!criterion || criterion.type === "related") {
      openExpressionCondition(path, "simple");
      return;
    }
    setSearch("");
    setRelatedWorkspaceSelection(null);
    setSimpleExpressionGroupPath(path.slice(0, -1));
    setExpandedCriterion(criterion.id);
    const siblingCount = getExpressionGroup(expression, path.slice(0, -1))?.children.filter((child) => child.filter
      && getExpressionConditionCriterion(child.filter, criteria)?.id === criterion.id).length ?? 0;
    if (siblingCount > 1) focusInlineCondition(path.at(-1) ?? 0);
    else focusFirstEditorControl();
  };

  function saveExpressionCondition() {
    if (!conditionDraft) return;
    const sanitized = sanitizeFilterCriteria(conditionDraft.filter, criteria);
    if (Object.keys(sanitized).length === 0) return;
    setEditFilter((current) => {
      const base = mergeFilterExpressionWithSimpleCriteria(current, criteria) ?? { operator: "AND" as const, children: [] };
      if (conditionDraft.isNew && countFilterExpressionConditions(base) >= MAX_FILTER_EXPRESSION_CONDITIONS) return current;
      if (conditionDraft.isNew && !conditionDraft.implicitRootAnd && getExpressionGroup(base, conditionDraft.parentPath)?.operator === "NOT") return current;
      const next = conditionDraft.isNew && conditionDraft.implicitRootAnd
        ? base.operator === "AND"
          ? { ...base, children: [...base.children, { filter: sanitized }] }
          : { operator: "AND" as const, children: [{ group: base }, { filter: sanitized }] }
        : conditionDraft.isNew
        ? replaceExpressionGroup(base, conditionDraft.parentPath, (group) => ({ ...group, children: [...group.children, { filter: sanitized }] }))
        : updateExpressionLeaf(base, conditionDraft.path ?? [], sanitized);
      return { ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: next };
    });
    if (conditionDraft.returnView === "simple") {
      if (getExpressionConditionCriterion(conditionDraft.filter, criteria)?.type === "related") {
        setExpandedCriterion(null);
        setRelatedWorkspaceSelection(null);
        pendingRelatedWorkspaceReturnFocusRef.current = false;
      }
      returnToSimpleFilters();
    }
    else returnToExpression();
  }

  const addRelatedCondition = (criterion: CriterionDefinition) => {
    const merged = mergeFilterExpressionWithSimpleCriteria(editFilter, criteria);
    if (!merged || countFilterExpressionConditions(merged) >= MAX_FILTER_EXPRESSION_CONDITIONS) return;
    simpleReturnFocusKeyRef.current = `repeat-${criterion.id}`;
    setConditionDraft({
      filter: { _criterionId: criterion.id },
      parentPath: [],
      isNew: true,
      returnView: "simple",
      implicitRootAnd: true,
    });
    setExpandedCriterion(criterion.id);
    setRelatedWorkspaceSelection(null);
    focusFirstConditionEditorControl();
  };

  const addImplicitAndCondition = (criterion: CriterionDefinition) => {
    const merged = mergeFilterExpressionWithSimpleCriteria(editFilter, criteria);
    if (!merged || countFilterExpressionConditions(merged) >= MAX_FILTER_EXPRESSION_CONDITIONS) return;
    const activeGroup = getExpressionGroup(merged, simpleExpressionGroupPath);
    if ((activeGroup?.operator === "OR" || activeGroup?.operator === "JUST_ONE") && !(activeGroup as EditableFilterExpression)._semanticNone) {
      const newIndex = activeGroup.children.length;
      inlineAddedConditionRef.current = {
        path: [...simpleExpressionGroupPath, newIndex],
        originPath: simpleExpressionGroupPath,
        unwrapRootOnRemove: false,
      };
      setEditFilter((current) => {
        const base = mergeFilterExpressionWithSimpleCriteria(current, criteria);
        if (!base || countFilterExpressionConditions(base) >= MAX_FILTER_EXPRESSION_CONDITIONS) return current;
        const next = replaceExpressionGroup(base, simpleExpressionGroupPath, (group) => ({
          ...group,
          children: [...group.children, { filter: { _criterionId: criterion.id } }],
        }));
        return { ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: next };
      });
      setExpandedCriterion(criterion.id);
      setRelatedWorkspaceSelection(null);
      focusInlineCondition(newIndex);
      return;
    }
    const newIndex = merged.operator === "AND" ? merged.children.length : 1;
    inlineAddedConditionRef.current = {
      path: [newIndex],
      originPath: simpleExpressionGroupPath,
      unwrapRootOnRemove: merged.operator !== "AND",
    };
    setEditFilter((current) => {
      const base = mergeFilterExpressionWithSimpleCriteria(current, criteria);
      if (!base || countFilterExpressionConditions(base) >= MAX_FILTER_EXPRESSION_CONDITIONS) return current;
      const next = base.operator === "AND"
        ? { ...base, children: [...base.children, { filter: { _criterionId: criterion.id } }] }
        : { operator: "AND" as const, children: [{ group: base }, { filter: { _criterionId: criterion.id } }] };
      return { ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: next };
    });
    setSimpleExpressionGroupPath([]);
    setExpandedCriterion(criterion.id);
    setRelatedWorkspaceSelection(null);
    focusInlineCondition(newIndex);
  };

  const removeSimpleExpressionCondition = (index: number, inlinePosition?: number, parentPath: number[] = simpleExpressionGroupPath) => {
    const removedPath = [...parentPath, index];
    const addedCondition = inlineAddedConditionRef.current;
    const removesTrackedAddition = Boolean(addedCondition && addedCondition.path.length === removedPath.length
      && addedCondition.path.every((part, position) => part === removedPath[position]));
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      if (!currentExpression) return current;
      const group = getExpressionGroup(currentExpression, parentPath);
      if (!group) return current;
      if (group.operator === "NOT" && group.children.length === 1) {
        const nextExpression = removeExpressionGroup(currentExpression, parentPath);
        const next = { ...current };
        if (nextExpression) next[FILTER_EXPRESSION_STATE_KEY] = nextExpression;
        else delete next[FILTER_EXPRESSION_STATE_KEY];
        return next;
      }
      const children = group.children.filter((_, candidate) => candidate !== index);
      const next = { ...current };
      if (parentPath.length === 0 && children.length === 0) delete next[FILTER_EXPRESSION_STATE_KEY];
      else if (removesTrackedAddition && addedCondition?.unwrapRootOnRemove && parentPath.length === 0 && children.length === 1 && children[0].group) {
        next[FILTER_EXPRESSION_STATE_KEY] = children[0].group;
      }
      else next[FILTER_EXPRESSION_STATE_KEY] = replaceExpressionGroup(currentExpression, parentPath, (target) => ({ ...target, children }));
      return next;
    });
    if (removesTrackedAddition && addedCondition) {
      setSimpleExpressionGroupPath(addedCondition.originPath);
      inlineAddedConditionRef.current = null;
    } else if (addedCondition) {
      const rebasePath = (path: number[]) => {
        if (path.length <= parentPath.length || !parentPath.every((part, position) => path[position] === part)) return path;
        const childIndex = path[parentPath.length];
        if (childIndex === index) return null;
        if (childIndex < index) return path;
        return [...path.slice(0, parentPath.length), childIndex - 1, ...path.slice(parentPath.length + 1)];
      };
      const path = rebasePath(addedCondition.path);
      const originPath = rebasePath(addedCondition.originPath);
      inlineAddedConditionRef.current = path && originPath ? { ...addedCondition, path, originPath } : null;
    }
    window.setTimeout(() => {
      if (removesTrackedAddition) {
        window.setTimeout(() => {
          const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
          (panel?.querySelector<HTMLElement>("button[aria-pressed='true']") ?? getFirstEditorControl(panel))?.focus();
        }, 0);
        return;
      }
      if (inlinePosition !== undefined) {
        window.setTimeout(() => {
          const conditions = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>("[data-inline-condition-index]") ?? []);
          const target = conditions[Math.min(inlinePosition, conditions.length - 1)];
          if (target) getFirstInlineEditorControl(target.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
          else {
            const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
            getFirstEditorControl(panel)?.focus();
          }
        }, 0);
        return;
      }
      const nextIndex = Math.min(index, directlyEditableExpressionChildren.length - 2);
      const target = dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="expression-${Math.max(0, nextIndex)}"]`);
      const toolbarButtons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
      (target ?? toolbarButtons[Math.min(selectedFiltersLastFocusedIndexRef.current, toolbarButtons.length - 1)] ?? searchRef.current)?.focus();
    }, 0);
  };

  const conditionCriterionId = conditionDraft
    ? typeof conditionDraft.filter._criterionId === "string"
      ? conditionDraft.filter._criterionId
      : criteria.find((criterion) => getCriterionFilterValue(conditionDraft.filter, criterion) !== undefined)?.id
    : undefined;
  const conditionCriterion = criteria.find((criterion) => criterion.id === conditionCriterionId);
  const conditionTitle = `${conditionDraft?.isNew ? "Add" : "Edit"} ${conditionCriterion?.label ?? "filter"} condition`;
  const conditionCanSave = Boolean(conditionCriterion && isCriterionValueValid(getCriterionFilterValue(conditionDraft?.filter ?? {}, conditionCriterion), conditionCriterion));
  const selectedCriterionEditorFilter = conditionDraft && conditionCriterion?.id === selectedCompactCriterion?.id
    ? conditionDraft.filter
    : selectedSingleExpressionInstance?.filter ?? editFilter;
  const relatedConditionLimitReached = countFilterExpressionConditions(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria)) >= MAX_FILTER_EXPRESSION_CONDITIONS;
  const mergedExpressionConditionCount = countFilterExpressionConditions(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria));
  const canCombineFilters = Boolean(dialogView === "simple" && !conditionDraft && supportsExpressions && mergedExpressionConditionCount >= 2);
  const hasRelatedWorkspaceCondition = Boolean(relatedWorkspaceCriterion
    && (isCriterionValueValid(getCriterionFilterValue(editFilter, relatedWorkspaceCriterion), relatedWorkspaceCriterion)
      || expressionHasActiveCriterion(expression, relatedWorkspaceCriterion)));
  const canAddRelatedCondition = Boolean(
    dialogView === "simple"
      && !conditionDraft
      && supportsExpressions
      && relatedWorkspaceCriterion
      && relatedWorkspaceCriterion.expressionSupported !== false
      && hasRelatedWorkspaceCondition
  );
  const canAddSelectedCriterion = Boolean(
    dialogView === "simple"
      && selectedItem?.kind === "criterion"
      && selectedItem.criterion.type !== "related"
      && !conditionDraft
      && supportsExpressions
      && (selectedItem.active || selectedExpressionInstances.length > 0)
      && selectedItem.criterion.expressionSupported !== false
  );

  useEffect(() => {
    const toolbar = selectedFiltersToolbarRef.current;
    if (!toolbar) return;
    const buttons = Array.from(toolbar.querySelectorAll<HTMLButtonElement>("button:not(:disabled)"));
    if (buttons.length === 0) return;
    const activeButton = document.activeElement instanceof HTMLButtonElement && toolbar.contains(document.activeElement)
      ? document.activeElement
      : undefined;
    const rememberedWasRemoved = Boolean(selectedFiltersLastFocusedRef.current && !toolbar.contains(selectedFiltersLastFocusedRef.current));
    const rememberedButton = selectedFiltersLastFocusedRef.current && toolbar.contains(selectedFiltersLastFocusedRef.current)
      ? selectedFiltersLastFocusedRef.current
      : undefined;
    const indexedFallback = buttons[Math.min(selectedFiltersLastFocusedIndexRef.current, buttons.length - 1)];
    const tabStop = activeButton ?? rememberedButton ?? indexedFallback ?? buttons[0];
    buttons.forEach((button) => { button.tabIndex = button === tabStop ? 0 : -1; });
    if (rememberedWasRemoved && document.activeElement === document.body) tabStop.focus();
  });

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 md:p-4"
      onMouseDown={(event) => {
        backdropPointerDownRef.current = event.target === event.currentTarget;
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget && backdropPointerDownRef.current) {
          dismiss();
        }

        backdropPointerDownRef.current = false;
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="filter-dialog-title"
        className="filter-dialog flex h-[100dvh] w-full flex-col overflow-hidden border-border bg-surface shadow-2xl md:h-[min(88dvh,52rem)] md:w-[min(94vw,72rem)] md:rounded-2xl md:border"
        onKeyDown={(event) => {
          if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
            event.preventDefault();
            if (conditionDraft) saveExpressionCondition();
            else if (relatedWorkspaceCriterion) finishRelatedWorkspace();
            else handleApply();
          }
        }}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex min-h-16 items-center justify-between border-b border-border px-4 pt-[env(safe-area-inset-top)] md:px-6 md:pt-0">
          <div className="flex min-w-0 items-center gap-2">
            {conditionDraft || inlineStackReturnsToExpression || dialogView !== "simple" || relatedWorkspaceCriterion ? (
              <button
                ref={backButtonRef}
                type="button"
                onClick={() => {
                  if (canAutoCommitNewRelatedCondition) {
                    exitExpressionCondition();
                  } else if (conditionDraft?.returnView === "simple" && !conditionDraft.isNew && relatedWorkspaceSelection) {
                    exitExpressionCondition();
                  } else if (conditionDraft && relatedWorkspaceSelection) {
                    setRelatedWorkspaceSelection(null);
                    window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
                  } else if (conditionDraft) exitExpressionCondition();
                  else if (inlineStackReturnsToExpression) returnToExpression();
                  else if (dialogView === "expression") returnToSimpleFilters();
                  else {
                    closeRelatedWorkspace();
                  }
                }}
                className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
                aria-label={conditionDraft
                  ? conditionDraft?.returnView === "simple" ? "Back to filters" : "Back to Combine Filters"
                  : inlineStackReturnsToExpression ? "Back to Combine Filters"
                  : dialogView === "expression" ? "Back to simple filters" : "Back to filters"}
              >
                <ArrowLeft className="h-5 w-5" />
              </button>
            ) : selectedItem ? (
              <button type="button" data-mobile-only-control onClick={() => { setExpandedCriterion(null); window.setTimeout(() => searchRef.current?.focus(), 0); }} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground md:hidden" aria-label="Back to filter criteria">
                <ArrowLeft className="h-5 w-5" />
              </button>
            ) : null}
            {relatedWorkspaceCriterion ? (
              <span
                aria-label={relatedWorkspaceCriterion.entityType === "performers" ? "Performers" : "Videos"}
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-accent/15 text-accent"
              >
                {relatedWorkspaceCriterion.entityType === "performers"
                  ? <Users className="h-4 w-4" />
                  : <Film className="h-4 w-4" />}
              </span>
            ) : null}
            <h2 id="filter-dialog-title" className="truncate text-lg font-semibold text-foreground">
              {conditionDraft ? conditionTitle : dialogView === "expression" ? "Combine Filters" : relatedWorkspaceCriterion ? `Filters / ${relatedWorkspaceCriterion.label}` : "Filters"}
            </h2>
            {dialogView === "simple" && !relatedWorkspaceCriterion && selectedItem ? <span className="truncate text-sm text-secondary md:hidden">{selectedItem.label}</span> : null}
            {dialogView === "simple" && displayedActiveCount > 0 && (
              <span className="rounded-full bg-accent px-2 py-0.5 text-xs font-bold text-white" aria-label={`${displayedActiveCount} active filters`}>
                {displayedActiveCount}
              </span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={dismiss} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground" aria-label="Close filters">
              <X className="h-5 w-5" />
            </button>
          </div>
        </div>

        {dialogView === "simple" && !conditionDraft && !relatedWorkspaceCriterion && (expressionConditionCount > 0 || Object.keys(activeEditFilter).length > 0) ? (
          <div
            ref={selectedFiltersToolbarRef}
            className="relative flex min-h-0 shrink flex-wrap items-center gap-2 overflow-y-auto overscroll-contain border-b border-border px-3 py-2 [&_button:focus-visible]:relative [&_button:focus-visible]:z-10 [&_button:focus-visible]:bg-accent/25 [&_button:focus-visible]:outline-none [&_button:focus-visible]:ring-2 [&_button:focus-visible]:ring-inset [&_button:focus-visible]:ring-accent md:px-4"
            role="toolbar"
            aria-label="Selected filters"
            aria-orientation="horizontal"
            aria-describedby={selectedFiltersInstructionsId}
            onFocusCapture={(event) => {
              const target = event.target;
              if (!(target instanceof HTMLButtonElement)) return;
              selectedFiltersLastFocusedRef.current = target;
              const buttons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
              selectedFiltersLastFocusedIndexRef.current = Math.max(0, buttons.indexOf(target));
              buttons.forEach((button) => { button.tabIndex = button === target ? 0 : -1; });
            }}
            onKeyDownCapture={(event) => {
              if (event.key === "ArrowDown" && event.target instanceof HTMLButtonElement) {
                event.preventDefault();
                event.stopPropagation();
                searchRef.current?.focus();
                return;
              }
              if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
              const buttons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
              const current = event.target instanceof HTMLButtonElement ? event.target : undefined;
              const index = current ? buttons.indexOf(current) : -1;
              if (index < 0 || buttons.length === 0) return;
              const nextIndex = event.key === "Home"
                ? 0
                : event.key === "End"
                ? buttons.length - 1
                : event.key === "ArrowRight"
                ? (index + 1) % buttons.length
                : (index - 1 + buttons.length) % buttons.length;
              event.preventDefault();
              event.stopPropagation();
              buttons[nextIndex].focus();
            }}
          >
            <span id={selectedFiltersInstructionsId} className="sr-only">Use Left and Right Arrow to move between selected filter parts and Clear all. Press Down Arrow to move to filter search. Press Enter to activate the focused control.</span>
            {hasComplexExpression && selectedFiltersCollapsed ? (
              <span className="flex h-6 items-center text-xs font-medium text-secondary">{displayedActiveCount} selected filters</span>
            ) : <>
            {validSimpleExpressionEntries.map(({ child, index }) => {
              const { _criterionId: _draftCriterionId, ...displayFilter } = child.filter ?? {};
              return (
              <ActiveObjectFilterChips
                key={index}
                criteriaDefinitions={criteria}
                objectFilter={{ [FILTER_EXPRESSION_STATE_KEY]: { operator: "AND", children: [{ ...child, filter: displayFilter }] } }}
                onEdit={(target) => {
                  if (target.kind === "expression") {
                    setRelatedWorkspaceSelection(target.criterionId ? { facet: target.relatedFacet ?? (target.nestedCriterionId ? "criterion" : "mode"), nestedCriterionId: target.nestedCriterionId } : null);
                    if (target.nestedCriterionId) openExpressionCondition(target.path, "simple", `expression-${target.path.join(".")}-nested-${target.nestedCriterionId}`);
                    else if (target.relatedFacet === "search" || target.relatedFacet === "existence") openExpressionCondition(target.path, "simple", `expression-${target.path.join(".")}-facet-${target.relatedFacet}`);
                    else openSimpleExpressionCondition(target.path);
                  }
                }}
                onRemove={() => removeSimpleExpressionCondition(index)}
                expressionReturnFocusKeys
                expressionPathOffset={index}
                hideRootAndOperator
                embeddedInToolbar
                ariaLabel={`Selected filter ${index + 1}`}
                className="!m-0 !border-0 !bg-transparent !p-0"
              />
              );
            })}
            {hasComplexExpression ? (
              <ActiveObjectFilterChips
                criteriaDefinitions={criteria}
                objectFilter={{ [FILTER_EXPRESSION_STATE_KEY]: expression }}
                onEdit={(target) => {
                  if (target.kind === "expression") {
                    setRelatedWorkspaceSelection(target.criterionId ? { facet: target.relatedFacet ?? (target.nestedCriterionId ? "criterion" : "mode"), nestedCriterionId: target.nestedCriterionId } : null);
                    if (target.nestedCriterionId) openExpressionCondition(target.path, "simple", `expression-${target.path.join(".")}-nested-${target.nestedCriterionId}`);
                    else if (target.relatedFacet === "search" || target.relatedFacet === "existence") openExpressionCondition(target.path, "simple", `expression-${target.path.join(".")}-facet-${target.relatedFacet}`);
                    else if (target.criterionId) openExpressionCondition(target.path, "simple", `expression-${target.path.join(".")}`);
                    else openSimpleExpressionCondition(target.path);
                  }
                  else if (target.kind === "root" && target.key === FILTER_EXPRESSION_STATE_KEY) {
                    const path = target.path ?? [];
                    enterExpression(`expression-group-${path.join(".") || "root"}`, path);
                  } else handleEditChip(target);
                }}
                onRemove={handleRemoveChip}
                onFocusFallback={() => {
                  const toolbarButtons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
                  const toolbarFallback = toolbarButtons[Math.min(selectedFiltersLastFocusedIndexRef.current, toolbarButtons.length - 1)];
                  if (toolbarFallback) toolbarFallback.focus();
                  else searchRef.current?.focus();
                }}
                expressionReturnFocusKeys
                hideRootAndOperator
                embeddedInToolbar
                ariaLabel="Selected expression filters"
                className="!m-0 !border-0 !bg-transparent !p-0"
              />
            ) : null}
            {Object.keys(activeEditFilter).length > 0 ? <ActiveObjectFilterChips
              criteriaDefinitions={criteria}
              objectFilter={activeEditFilter}
              customFilterSections={customSections}
              onEdit={handleEditChip}
              onRemove={handleRemoveChip}
              onClearAll={hasComplexExpression ? undefined : handleClear}
              rovingKeyboardAccess
              embeddedInToolbar
              onFocusFallback={() => {
                const toolbarButtons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
                const toolbarFallback = toolbarButtons[Math.min(selectedFiltersLastFocusedIndexRef.current, toolbarButtons.length - 1)];
                if (toolbarFallback) {
                  toolbarFallback.focus();
                  return;
                }
                const mobileLayout = typeof window.matchMedia === "function"
                  ? window.matchMedia("(max-width: 767px)").matches
                  : window.innerWidth < 768;
                if (mobileLayout && expandedCriterion) {
                  const editorControl = getFirstEditorControl(dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']"));
                  if (editorControl) {
                    editorControl.focus();
                    return;
                  }
                }
                const criterionButton = expandedCriterion ? criterionButtonRefs.current.get(expandedCriterion) : undefined;
                if (criterionButton) criterionButton.focus();
                else searchRef.current?.focus();
              }}
              onFocusKey={(key) => {
                const buttons = dialogRef.current?.querySelectorAll<HTMLButtonElement>("button[data-active-filter-key]");
                Array.from(buttons ?? []).find((button) => button.dataset.activeFilterKey === key)?.focus();
              }}
              ariaLabel="Selected filters"
              className="!m-0 !border-0 !bg-transparent !p-0"
            /> : null}
            </>}
            {hasComplexExpression ? (
              <div className="absolute right-2 top-2 z-10 flex items-center gap-1">
                <button type="button" aria-label={selectedFiltersCollapsed ? "Show selected filters" : "Hide selected filters"} aria-expanded={!selectedFiltersCollapsed} onClick={() => setSelectedFiltersCollapsed((current) => !current)} className="inline-flex h-6 w-6 items-center justify-center rounded-md border border-border/80 bg-surface/95 text-secondary shadow-sm hover:bg-card hover:text-foreground">{selectedFiltersCollapsed ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronUp className="h-3.5 w-3.5" />}</button>
                <button type="button" onClick={handleClear} className="h-6 rounded-md border border-border/80 bg-surface/95 px-2 text-xs font-medium text-secondary shadow-sm hover:bg-red-500/10 hover:text-red-300">Clear all</button>
              </div>
            ) : null}
          </div>
        ) : null}

        {dialogView === "expression" ? (
          <FilterExpressionEditor
            criteria={criteria}
            value={expression ?? { operator: "AND", children: [] }}
            onChange={(value) => setEditFilter((current) => ({ ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: value }))}
            onAddCondition={openNewExpressionCondition}
            onEditCondition={(path, target) => {
              openExpressionCondition(path);
              if (target?.kind === "related") setRelatedWorkspaceSelection({ facet: target.facet, nestedCriterionId: target.nestedCriterionId });
            }}
            subjectLabel={subjectLabel}
          />
        ) : relatedWorkspaceCriterion ? (
          <div className="flex min-h-0 flex-1 flex-col">
              <>
                {isCriterionValueValid(getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion), relatedWorkspaceCriterion) ? (
                  <ActiveObjectFilterChips
                    criteriaDefinitions={criteria}
                    objectFilter={setCriterionFilterValue({}, relatedWorkspaceCriterion, getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion))}
                    onEdit={handleEditChip}
                    onRemove={handleRemoveRelatedWorkspaceChip}
                    rovingKeyboardAccess
                    onFocusFallback={() => {
                      const searchLabel = relatedWorkspaceCriterion.entityType === "performers"
                        ? "Search performer filter criteria"
                        : "Search video filter criteria";
                      window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>(`[aria-label="${searchLabel}"]`)?.focus(), 0);
                    }}
                    ariaLabel={`${relatedWorkspaceCriterion.label} selected filters`}
                    className="mx-3 mt-3 max-h-[min(10rem,25dvh)] shrink-0 overflow-y-auto md:mx-4"
                  />
                ) : null}
                <RelatedFilterWorkspace
                  criterion={relatedWorkspaceCriterion}
                  value={getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion) as RelatedFilterCriterion | undefined}
                  onChange={(value) => conditionCriterion?.id === relatedWorkspaceCriterion.id && conditionDraft
                    ? setConditionDraft((current) => current ? { ...current, filter: { _criterionId: relatedWorkspaceCriterion.id, ...setCriterionFilterValue({}, relatedWorkspaceCriterion, value) } } : current)
                    : relatedExpressionInstances.length > 0
                      ? updateInlineCondition(relatedExpressionInstances[0].path, relatedWorkspaceCriterion, value)
                    : handleSetCriterion(relatedWorkspaceCriterion, value)}
                  selection={relatedWorkspaceSelection}
                  onSelectionChange={setRelatedWorkspaceSelection}
                  CriterionEditorComponent={CriterionEditor}
                />
              </>
          </div>
        ) : <div className="grid min-h-[min(12rem,35dvh)] flex-1 overflow-hidden md:grid-cols-[20rem_minmax(0,1fr)]">
          <aside className={`${selectedItem ? "hidden md:flex" : "flex"} min-h-0 flex-col border-border md:border-r`} aria-label="Filter criteria">
            <div className="border-b border-border p-3 md:p-4">
              <label className="relative block">
                <span className="sr-only">Search filter criteria</span>
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                <input
                  ref={searchRef}
                  type="search"
                  aria-label="Search filter criteria"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === "ArrowUp") {
                      const toolbar = selectedFiltersToolbarRef.current;
                      const buttons = Array.from(toolbar?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
                      const rememberedButton = selectedFiltersLastFocusedRef.current && toolbar?.contains(selectedFiltersLastFocusedRef.current)
                        ? selectedFiltersLastFocusedRef.current
                        : undefined;
                      const toolbarTarget = rememberedButton ?? buttons.find((button) => button.tabIndex === 0) ?? buttons[0];
                      if (toolbarTarget) {
                        event.preventDefault();
                        toolbarTarget.focus();
                      }
                      return;
                    }
                    if (event.key !== "ArrowDown" || visibleNavigatorItems.length === 0) return;
                    event.preventDefault();
                    criterionButtonRefs.current.get(visibleNavigatorItems[0].id)?.focus();
                  }}
                  placeholder="Search filters"
                  className="min-h-11 w-full rounded-lg border border-border bg-input py-2 pl-10 pr-3 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
                />
              </label>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain p-2 md:p-3" role="tablist" aria-label="Available filter criteria" aria-orientation="vertical">
              {navigatorGroups.map((group) => (
                <section key={group.label} className="mb-4" aria-label={group.label}>
                  <h3 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">{group.label}</h3>
                  <div className="space-y-1">
                    {group.items.map((item) => {
                      const selected = item.id === expandedCriterion;
                      const supported = item.kind === "custom" || item.criterion.supported !== false;
                      const rowStateClass = selected
                        ? "border-accent bg-accent/15 text-foreground"
                        : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground";
                      return (
                        <div key={item.id} role="presentation" className={`flex min-h-11 w-full items-stretch overflow-hidden rounded-lg border text-sm transition ${rowStateClass} ${supported ? "" : "cursor-not-allowed opacity-60"}`}>
                          <button
                            ref={(element) => { if (element) criterionButtonRefs.current.set(item.id, element); else criterionButtonRefs.current.delete(item.id); }}
                            type="button"
                            role="tab"
                            id={`filter-tab-${item.id}`}
                            aria-selected={selected}
                            aria-describedby={item.active ? `filter-status-${item.id}` : undefined}
                            aria-controls="filter-editor-panel"
                            aria-disabled={!supported || undefined}
                            tabIndex={item.id === rovingNavigatorId ? 0 : -1}
                            onClick={() => { if (supported) selectNavigatorItem(item.id); }}
                            onFocus={() => setNavigatorFocusId(item.id)}
                            onKeyDown={(event) => {
                              const index = visibleNavigatorItems.findIndex((candidate) => candidate.id === item.id);
                              if (event.key === "ArrowRight" && item.kind === "criterion" && supported) {
                                event.preventDefault();
                                pinButtonRefs.current.get(item.id)?.focus();
                                return;
                              }
                              if (event.key === "ArrowUp" && index === 0) {
                                event.preventDefault();
                                searchRef.current?.focus();
                                return;
                              }
                              let nextIndex: number | undefined;
                              if (event.key === "ArrowDown") nextIndex = Math.min(visibleNavigatorItems.length - 1, index + 1);
                              if (event.key === "ArrowUp") nextIndex = Math.max(0, index - 1);
                              if (event.key === "Home") nextIndex = 0;
                              if (event.key === "End") nextIndex = visibleNavigatorItems.length - 1;
                              if (nextIndex !== undefined) {
                                event.preventDefault();
                                criterionButtonRefs.current.get(visibleNavigatorItems[nextIndex].id)?.focus();
                              }
                            }}
                            className="flex min-w-0 flex-1 items-center gap-3 px-3 py-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                            title={supported ? undefined : item.kind === "criterion" ? item.criterion.unsupportedReason : undefined}
                          >
                            <span className={`min-w-0 flex-1 truncate font-medium ${item.active ? "text-accent" : ""}`}>{item.label}</span>
                            {!supported ? <span className="text-[10px] uppercase tracking-wide text-muted">Unavailable</span> : null}
                          </button>
                          {item.active ? <span id={`filter-status-${item.id}`} className="sr-only">Active filter</span> : null}
                          {item.kind === "criterion" && supported ? (
                            <button
                              ref={(element) => { if (element) pinButtonRefs.current.set(item.id, element); else pinButtonRefs.current.delete(item.id); }}
                              type="button"
                              onClick={() => togglePin(item.id)}
                              onKeyDown={(event) => {
                                if (event.key === "ArrowLeft" || event.key === "Escape") {
                                  event.preventDefault();
                                  event.stopPropagation();
                                  criterionButtonRefs.current.get(item.id)?.focus();
                                }
                              }}
                              tabIndex={-1}
                              aria-label={`${item.pinned ? "Unpin" : "Pin"} ${item.label}`}
                              aria-pressed={item.pinned}
                              title={`${item.pinned ? "Unpin" : "Pin"} ${item.label}`}
                              className={`group flex w-10 shrink-0 items-center justify-center border-l text-muted transition hover:bg-background/40 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent ${selected ? "border-accent/40" : "border-border/60"} ${item.pinned ? "opacity-100" : "opacity-100 md:opacity-0 md:hover:opacity-100 md:focus-visible:opacity-100"}`}
                            >
                              {item.pinned ? (
                                <>
                                  <Pin className="h-4 w-4 group-hover:hidden group-focus-visible:hidden" />
                                  <PinOff className="hidden h-4 w-4 group-hover:block group-focus-visible:block" />
                                </>
                              ) : <Pin className="h-4 w-4" />}
                            </button>
                          ) : null}
                        </div>
                      );
                    })}
                  </div>
                </section>
              ))}
              {visibleNavigatorItems.length === 0 ? <div className="px-4 py-10 text-center text-sm text-muted">No filters match “{search}”.</div> : null}
            </div>
          </aside>

          <main className={`${selectedItem ? "flex" : "hidden md:flex"} min-h-0 min-w-0 flex-col`}>
            {selectedItem ? (
              <div
                id="filter-editor-panel"
                role="tabpanel"
                aria-labelledby={`filter-tab-${selectedItem.id}`}
                aria-label={selectedItem.label}
                className="flex min-h-0 flex-1 flex-col overflow-y-auto p-4 md:p-6"
              >
                {selectedItem.kind === "custom" ? selectedItem.section.renderEditor(
                  editFilter[selectedItem.section.filterKey] ?? selectedItem.section.defaultValue,
                  (nextValue) => {
                    setEditFilter((current) => {
                      const next = { ...current };
                      const shouldKeepDraft = selectedItem.section.shouldKeepDraft ?? selectedItem.section.isActive;
                      if (shouldKeepDraft(nextValue)) next[selectedItem.section.filterKey] = nextValue;
                      else delete next[selectedItem.section.filterKey];
                      return next;
                    });
                  },
                ) : selectedItem.criterion.supported === false ? (
                  <div className="rounded-xl border border-border bg-card p-4 text-sm text-secondary">{selectedItem.criterion.unsupportedReason ?? "This filter is not currently available."}</div>
                ) : showInlineConditionStack ? (
                  <div className="space-y-4">
                    {selectedExpressionInstances.map(({ index, path, filter }, position) => (
                      <fieldset
                        key={index}
                        data-inline-condition-index={index}
                        aria-label={`${selectedItem.criterion.label} condition ${position + 1}`}
                        className="relative rounded-xl border border-border bg-card/30 p-4 pr-12"
                      >
                        <button
                          type="button"
                          onClick={() => removeSimpleExpressionCondition(index, position, path.slice(0, -1))}
                          title={`Remove ${selectedItem.criterion.label} condition ${position + 1}`}
                          aria-label={`Remove ${selectedItem.criterion.label} condition ${position + 1}`}
                          className="absolute right-2 top-2 inline-flex h-8 w-8 items-center justify-center rounded-md text-muted hover:bg-red-500/10 hover:text-red-300"
                        >
                          <X className="h-3.5 w-3.5" />
                        </button>
                        <div data-inline-condition-editor>
                          <CriterionEditor
                            criterion={selectedItem.criterion}
                            value={getCriterionFilterValue(filter, selectedItem.criterion)}
                            auxiliaryToggleChecked={selectedItem.criterion.auxiliaryToggleKey ? Boolean(filter[selectedItem.criterion.auxiliaryToggleKey]) : undefined}
                            onAuxiliaryToggleChange={(checked) => updateInlineAuxiliaryToggle(path, selectedItem.criterion, checked)}
                            onChange={(value) => updateInlineCondition(path, selectedItem.criterion, value)}
                          />
                        </div>
                      </fieldset>
                    ))}
                  </div>
                ) : (
                  <>
                    <CriterionEditor
                      criterion={selectedItem.criterion}
                      value={getCriterionFilterValue(selectedCriterionEditorFilter, selectedItem.criterion)}
                      auxiliaryToggleChecked={selectedItem.criterion.auxiliaryToggleKey ? Boolean(selectedCriterionEditorFilter[selectedItem.criterion.auxiliaryToggleKey]) : undefined}
                      onAuxiliaryToggleChange={(checked) => {
                        if (conditionCriterion?.id === selectedItem.criterion.id && conditionDraft) {
                          setConditionDraft((current) => {
                            if (!current || !selectedItem.criterion.auxiliaryToggleKey) return current;
                            const filter = { ...current.filter };
                            if (checked) filter[selectedItem.criterion.auxiliaryToggleKey] = true;
                            else delete filter[selectedItem.criterion.auxiliaryToggleKey];
                            return { ...current, filter };
                          });
                        } else if (selectedSingleExpressionInstance) {
                          updateInlineAuxiliaryToggle(selectedSingleExpressionInstance.path, selectedItem.criterion, checked);
                        } else handleSetAuxiliaryToggle(selectedItem.criterion, checked);
                      }}
                      onChange={(value) => conditionCriterion?.id === selectedItem.criterion.id && conditionDraft
                        ? setConditionDraft((current) => current ? { ...current, filter: { _criterionId: selectedItem.criterion.id, ...setCriterionFilterValue({}, selectedItem.criterion, value) } } : current)
                        : selectedSingleExpressionInstance
                        ? updateInlineCondition(selectedSingleExpressionInstance.path, selectedItem.criterion, value)
                        : handleSetCriterion(selectedItem.criterion, value)}
                    />
                  </>
                )}
              </div>
            ) : (
              <div className="flex flex-1 items-center justify-center p-8 text-center">
                <div className="max-w-sm">
                  <Search className="mx-auto mb-3 h-8 w-8 text-muted" />
                  <h3 className="text-lg font-semibold text-foreground">Choose a filter</h3>
                  <p className="mt-1 text-sm text-secondary">Search or select a criterion to configure it. Changes are applied only when you choose Apply.</p>
                </div>
              </div>
            )}
          </main>
        </div>}

        {/* Footer */}
        <div className="flex min-h-16 flex-wrap items-center justify-between gap-2 border-t border-border px-4 py-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] md:px-6 md:py-2">
          {canCombineFilters || canAddRelatedCondition || canAddSelectedCriterion ? (
            <div className="flex items-center gap-1" role="group" aria-label="Filter composition actions">
              {canCombineFilters ? (
                <button
                  type="button"
                  onClick={() => enterExpression("combine")}
                  data-simple-return-focus="combine"
                  title="Combine Filters"
                  aria-label="Combine Filters"
                  className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
                >
                  <Workflow className="h-4 w-4" />
                </button>
              ) : null}
              {relatedWorkspaceCriterion && canAddRelatedCondition ? (
                <button
                  type="button"
                  onClick={() => addRelatedCondition(relatedWorkspaceCriterion)}
                  disabled={relatedConditionLimitReached}
                  data-simple-return-focus={`repeat-${relatedWorkspaceCriterion.id}`}
                  className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"
                  title={`Add another ${relatedWorkspaceCriterion.entityType === "performers" ? "performer" : "video"} condition`}
                  aria-label={`Add another ${relatedWorkspaceCriterion.entityType === "performers" ? "performer" : "video"} condition`}
                >
                  <Plus className="h-4 w-4" />
                </button>
              ) : null}
              {canAddSelectedCriterion && selectedItem?.kind === "criterion" ? (
                <button
                  type="button"
                  onClick={() => addImplicitAndCondition(selectedItem.criterion)}
                  disabled={mergedExpressionConditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS || selectedHasIncompleteExpressionInstance}
                  data-simple-return-focus={`repeat-${selectedItem.criterion.id}`}
                  className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"
                  title={`Add another ${selectedItem.criterion.label}`}
                  aria-label={`Add another ${selectedItem.criterion.label}`}
                >
                  <Plus className="h-4 w-4" />
                </button>
              ) : null}
            </div>
          ) : <span />}
          <div className="flex flex-wrap items-center justify-end gap-2">
            {conditionDraft ? (
              <>
                {conditionDraft.isNew && conditionCriterion?.type === "related" ? (
                  <button type="button" onClick={saveExpressionCondition} disabled={!conditionCanSave} className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50">Apply</button>
                ) : (
                  <>
                    <button type="button" onClick={discardExpressionCondition} className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel condition</button>
                    <button type="button" onClick={saveExpressionCondition} disabled={!conditionCanSave} className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50">Save condition</button>
                  </>
                )}
              </>
            ) : (
              <>
                <button type="button" onClick={dismiss} className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel</button>
                {relatedWorkspaceCriterion ? (
                  <button type="button" onClick={finishRelatedWorkspace} aria-keyshortcuts="Control+Enter Meta+Enter" className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover">Apply</button>
                ) : (
                  <button type="button" onClick={handleApply} aria-keyshortcuts="Control+Enter Meta+Enter" className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover">Apply</button>
                )}
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

// ===== Criterion Editor =====

function CriterionEditor({
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

function FilterExpressionEditor({
  criteria,
  value,
  onChange,
  onAddCondition,
  onEditCondition,
  subjectLabel,
}: {
  criteria: CriterionDefinition[];
  value: FilterExpression<Record<string, unknown>>;
  onChange: (value: FilterExpression<Record<string, unknown>>) => void;
  onAddCondition: (criterionId?: string, parentPath?: number[]) => void;
  onEditCondition: (path: number[], target?: FilterChipTarget) => void;
  subjectLabel: string;
}) {
  const ratingOptions = useRatingOptions();
  const appConfig = useOptionalAppConfig();
  const metadataServers = appConfig?.config?.scraping?.metadataServers ?? [];
  return (
    <FilterExpressionEditorView
      criteria={criteria}
      value={value}
      onChange={onChange}
      onAddCondition={onAddCondition}
      onEditCondition={onEditCondition}
      subjectLabel={subjectLabel}
      describeCondition={(filter) => describeFilterExpressionCondition(filter, criteria, ratingOptions, metadataServers)}
    />
  );
}



// ===== Related-entity Editor =====


// ===== Bool Editor =====

function BoolEditor({ value, onChange }: { value?: BoolCriterion; onChange: (v: unknown) => void }) {
  return (
    <div className="space-y-2" role="group" aria-label="Value">
      <div className="text-sm font-medium text-secondary">Value</div>
      <div className="flex flex-wrap items-center gap-2">
      <button
        type="button"
        aria-pressed={value?.value === true}
        onClick={() => onChange({ value: true })}
        className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === true ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        True
      </button>
      <button
        type="button"
        aria-pressed={value?.value === false}
        onClick={() => onChange({ value: false })}
        className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === false ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        False
      </button>
      </div>
    </div>
  );
}

// ===== Number Editor =====

export function NumberEditor({
  value,
  onChange,
  type,
  modifiers,
  defaultModifier,
  min,
  max,
  step,
  hint,
  auxiliaryToggleLabel,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
}: {
  value?: IntCriterion;
  onChange: (v: unknown) => void;
  type: CriterionType;
  modifiers: CriterionModifier[];
  defaultModifier?: CriterionModifier;
  min?: number;
  max?: number;
  step?: number;
  hint?: string;
  auxiliaryToggleLabel?: string;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
}) {
  // A criterion that narrows `modifiers` must be able to start on one it actually offers — otherwise the Match
  // control shows nothing selected and the saved criterion carries a modifier that isn't in the list.
  const modifier = value?.modifier ?? defaultModifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  // Both bounds known ⇒ the value lives on a range, so offer a slider alongside the box.
  const bounded = min != null && max != null && max > min;
  const sliderStep = step ?? (bounded ? Math.max((max! - min!) / 100, 0.001) : undefined);
  const fallback = bounded ? (min! + max!) / 2 : 0;

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ modifier, ...(bounded ? { value: value?.value ?? fallback } : {}), ...value, ...patch });
  };

  const numberInput = (current: number | undefined, onPick: (v: number | undefined) => void, label: string) => (
    <div className={bounded ? "flex items-center gap-3" : undefined}>
      {bounded && (
        <input
          aria-label={`${label} slider`}
          type="range"
          min={min}
          max={max}
          step={sliderStep}
          value={current ?? fallback}
          onChange={(e) => onPick(Number(e.target.value))}
          className="h-2 flex-1 accent-accent"
        />
      )}
      <input
        aria-label={label}
        type="number"
        min={min}
        max={max}
        step={sliderStep}
        value={current ?? ""}
        onChange={(e) => onPick(e.target.value === "" ? undefined : Number(e.target.value))}
        className={`min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm ${bounded ? "w-24 tabular-nums" : "w-full"}`}
      />
    </div>
  );

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="grid gap-3 sm:grid-cols-2">
          {type === "duration" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}><DurationInput value={value?.value} onChange={(v) => update({ value: v })} ariaLabel={isBetween ? "Minimum" : "Value"} /></LabeledControl>
          ) : type === "resolution" ? (
            <LabeledControl label="Value"><ResolutionSelect value={value?.value ?? 0} onChange={(v) => update({ value: v })} /></LabeledControl>
          ) : type === "careerLength" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}><CareerLengthInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} /></LabeledControl>
          ) : (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}>
              {numberInput(value?.value, (v) => update({ value: v }), isBetween ? "Minimum" : "Value")}
            </LabeledControl>
          )}
          {isBetween && (
            <div>
              {type === "duration" ? (
                <LabeledControl label="Maximum"><DurationInput value={value?.value2} onChange={(v) => update({ value2: v })} ariaLabel="Maximum" /></LabeledControl>
              ) : type === "careerLength" ? (
                <LabeledControl label="Maximum"><CareerLengthInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} /></LabeledControl>
              ) : (
                <LabeledControl label="Maximum">
                  {numberInput(value?.value2, (v) => update({ value2: v }), "Maximum")}
                </LabeledControl>
              )}
            </div>
          )}
        </div>
      )}
      {hint && <div className="text-xs text-muted">{hint}</div>}
      {auxiliaryToggleLabel && onAuxiliaryToggleChange && (
        <label className="flex min-h-9 items-center gap-2 text-sm text-secondary">
          <input
            type="checkbox"
            checked={Boolean(auxiliaryToggleChecked)}
            onChange={(event) => onAuxiliaryToggleChange(event.target.checked)}
            className="h-5 w-5 rounded border-border bg-input text-accent focus:ring-accent"
          />
          <span>{auxiliaryToggleLabel}</span>
        </label>
      )}
    </div>
  );
}


function RatingStarInput({
  displayValue,
  onChangeDisplay,
  step,
}: {
  displayValue: number;
  onChangeDisplay: (v: number) => void;
  step: number;
}) {
  const [hoverValue, setHoverValue] = useState<number | null>(null);
  const activeValue = hoverValue ?? displayValue;

  return (
    <div className="flex items-center gap-0.5" onMouseLeave={() => setHoverValue(null)}>
      {[1, 2, 3, 4, 5].map((star) => (
        <button
          key={star}
          type="button"
          aria-label={`Set rating to ${star}`}
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            setHoverValue(star - 1 + frac);
          }}
          onMouseLeave={() => setHoverValue(null)}
          onClick={(e) => {
            const next = e.detail === 0
              ? star
              : (() => {
                const rect = e.currentTarget.getBoundingClientRect();
                const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
                const segments = Math.max(1, Math.ceil(ratio / step));
                const frac = Math.min(1, Number((segments * step).toFixed(2)));
                return star - 1 + frac;
              })();
            onChangeDisplay(next === displayValue ? 0 : next);
          }}
          className="relative inline-flex h-9 w-9 items-center justify-center rounded-lg text-accent transition-transform hover:scale-105 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <Star className="h-7 w-7 text-muted" />
          <span
            className="absolute left-1 top-1 h-7 overflow-hidden"
            style={{ width: `${Math.max(0, Math.min(1, activeValue - (star - 1))) * 1.75}rem` }}
          >
            <Star className="h-7 w-7 fill-current text-accent" />
          </span>
        </button>
      ))}
      {hoverValue != null && (
        <span className="text-xs text-secondary ml-1">{hoverValue.toFixed(step < 1 ? 1 : 0)}</span>
      )}
    </div>
  );
}

function RatingFilterInput({
  rawValue,
  onChangeRaw,
}: {
  rawValue: number;
  onChangeRaw: (v: number) => void;
}) {
  const options = useRatingOptions();
  const displayValue = convertToRatingFormat(rawValue || undefined, options) ?? 0;
  const max = getRatingMax(options);
  const step = getRatingStep(options);

  const setDisplay = (v: number) => {
    const clamped = Math.min(max, Math.max(0, Number(v.toFixed(2))));
    onChangeRaw(convertFromRatingFormat(clamped, options));
  };

  if (options.type === "stars") {
    return (
      <div className="flex items-center gap-2">
        <RatingStarInput
          displayValue={displayValue}
          onChangeDisplay={setDisplay}
          step={getRatingPrecision(options.starPrecision)}
        />
      </div>
    );
  }

  // Decimal mode
  return (
    <input
      type="number"
      value={displayValue || ""}
      min={0}
      max={max}
      step={step}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (Number.isFinite(v)) setDisplay(v);
      }}
      className="min-h-11 w-28 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
    />
  );
}

function RatingFilterEditor({ value, onChange, modifiers }: { value?: IntCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ value: value?.value ?? 0, modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="space-y-2">
          <RatingFilterInput rawValue={value?.value ?? 0} onChangeRaw={(v) => update({ value: v })} />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <RatingFilterInput rawValue={value?.value2 ?? 0} onChangeRaw={(v) => update({ value2: v })} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== String Editor =====

function StringEditor({ value, onChange, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            aria-label="Value"
            type="text"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            placeholder="Enter a value"
          />
        </LabeledControl>
      )}
    </div>
  );
}

function PathEditor({ value, onChange, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "UNDER_PATH";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const rootsQuery = useQuery({
    queryKey: ["library-folders", "roots", false],
    queryFn: () => metadata.libraryFolders(undefined, false),
    retry: false,
  });

  const updateModifier = (nextModifier: CriterionModifier) => {
    onChange({ value: value?.value ?? "", modifier: nextModifier });
  };

  const selectFolder = (path: string, checked: boolean) => {
    if (!checked) return;
    onChange({
      value: path,
      modifier: modifier === "NOT_UNDER_PATH" ? "NOT_UNDER_PATH" : "UNDER_PATH",
    });
  };

  return (
    <div className="space-y-4">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={updateModifier} />
      {!isNull ? (
        <>
          <div className="space-y-2">
            <div>
              <div className="text-sm font-medium text-secondary">Browse library folders</div>
              <p className="text-xs text-muted">Choose a folder to match it and all of its descendants.</p>
            </div>
            {rootsQuery.isLoading || (rootsQuery.isFetching && rootsQuery.isError) ? (
              <p className="text-xs text-muted">Loading library folders…</p>
            ) : rootsQuery.isError ? (
              <p className="text-xs text-muted">Folder browsing is unavailable. You can still enter a path manually.</p>
            ) : (
              <LibraryFolderTree
                roots={rootsQuery.data ?? []}
                selected={value?.value ? [value.value] : []}
                onToggle={selectFolder}
                selectionMode="single"
                probeChildren={false}
                emptyHint="No library folders are configured."
              />
            )}
          </div>
          <LabeledControl label="Path">
            <input
              aria-label="Path"
              type="text"
              value={value?.value ?? ""}
              onChange={(event) => onChange({ value: event.target.value, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              placeholder="Enter a file or folder path"
            />
          </LabeledControl>
        </>
      ) : null}
    </div>
  );
}

export function RemoteIdFilterEditor({
  value,
  onChange,
  modifiers,
  metadataServers,
}: {
  value?: StringCriterion & { endpoint?: string };
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  metadataServers?: MetadataServer[];
}) {
  const appConfig = useOptionalAppConfig();
  const modifier = value?.modifier ?? "EQUALS";
  const selectedEndpoint = value?.endpoint?.trim() ?? "";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const configuredServers = metadataServers ?? appConfig?.config?.scraping?.metadataServers ?? [];
  const options = useMemo(() => {
    const endpoints = new Set<string>();
    const configured = configuredServers.flatMap((server) => {
      const endpoint = server.endpoint.trim();
      const normalizedEndpoint = endpoint.toLowerCase();
      if (!endpoint || endpoints.has(normalizedEndpoint)) return [];
      endpoints.add(normalizedEndpoint);
      const optionValue = selectedEndpoint.toLowerCase() === normalizedEndpoint ? selectedEndpoint : endpoint;
      return [{ value: optionValue, label: server.name?.trim() || endpoint }];
    });

    if (selectedEndpoint && !endpoints.has(selectedEndpoint.toLowerCase())) {
      configured.push({ value: selectedEndpoint, label: `${selectedEndpoint} (unconfigured)` });
    }

    return configured;
  }, [configuredServers, selectedEndpoint]);

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(nextModifier) => onChange({ value: value?.value ?? "", endpoint: selectedEndpoint, modifier: nextModifier })}
      />
      <select
        aria-label="Metadata Service"
        value={selectedEndpoint}
        onChange={(event) => onChange({ value: value?.value ?? "", endpoint: event.target.value, modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none disabled:opacity-60 md:text-sm"
      >
        <option value="">Any metadata service</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      {!isNull && (
        <input
          type="text"
          aria-label="Remote ID value"
          value={value?.value ?? ""}
          onChange={(event) => onChange({ value: event.target.value, endpoint: selectedEndpoint, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
          placeholder="Value..."
        />
      )}
    </div>
  );
}

function HashEditor({
  value,
  onChange,
  modifiers,
  options,
}: {
  value?: FingerprintCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  options: { value: string; label: string }[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const hashType = value?.type ?? options[0]?.value ?? "md5";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <select
        value={hashType}
        onChange={(event) => onChange({ type: event.target.value, value: value?.value ?? "", modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(nextModifier) => onChange({ type: hashType, value: value?.value ?? "", modifier: nextModifier })} />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            type="text"
            aria-label="Value"
            value={value?.value ?? ""}
            onChange={(event) => onChange({ type: hashType, value: event.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
            placeholder="Hash value..."
          />
        </LabeledControl>
      )}
    </div>
  );
}

// ===== Enum Editor =====

function EnumEditor({ value, onChange, options, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; options: { value: string; label: string }[]; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <select
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
        >
          <option value="">Select...</option>
          {options.map((opt) => (
            <option key={opt.value} value={opt.value}>{opt.label}</option>
          ))}
        </select>
      )}
    </div>
  );
}

function MultiEnumEditor({ value, onChange, options }: { value?: StringCriterion; onChange: (v: unknown) => void; options: { value: string; label: string }[] }) {
  const selectionMode = value?.modifier === "NOT_MATCHES_REGEX"
    ? "exclude"
    : value?.modifier === "IS_NULL"
    ? "isNull"
    : value?.modifier === "NOT_NULL"
    ? "notNull"
    : "include";
  const selectedValues = useMemo(() => {
    const storedValues = (value as { _selectedValues?: string[] } | undefined)?._selectedValues;
    if (Array.isArray(storedValues) && storedValues.length > 0) {
      return options.filter((option) => storedValues.includes(option.value)).map((option) => option.value);
    }

    if (!value?.value) {
      return [];
    }

    if (value.modifier === "MATCHES_REGEX" || value.modifier === "NOT_MATCHES_REGEX") {
      try {
        const regex = new RegExp(value.value, "i");
        return options.filter((option) => regex.test(option.value)).map((option) => option.value);
      } catch {
        return [];
      }
    }

    return options.some((option) => option.value === value.value) ? [value.value] : [];
  }, [options, value]);

  const buildCriterion = (nextSelectedValues: string[], nextMode: "include" | "exclude" | "isNull" | "notNull") => {
    if (nextMode === "isNull") {
      onChange({ value: "", modifier: "IS_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    if (nextMode === "notNull") {
      onChange({ value: "", modifier: "NOT_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    const escapedValues = nextSelectedValues.map((selectedValue) => selectedValue.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
    onChange({
      value: escapedValues.length > 0 ? `^(?:${escapedValues.join("|")})$` : "",
      modifier: nextMode === "exclude" ? "NOT_MATCHES_REGEX" : "MATCHES_REGEX",
      _selectedValues: nextSelectedValues,
    });
  };

  const toggleValue = (optionValue: string) => {
    const nextSelectedValues = selectedValues.includes(optionValue)
      ? selectedValues.filter((selectedValue) => selectedValue !== optionValue)
      : [...selectedValues, optionValue];
    buildCriterion(nextSelectedValues, selectionMode);
  };

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2" role="group" aria-label="Match mode">
        {([
          ["include", "Any Of"],
          ["exclude", "None Of"],
          ["isNull", "No Value"],
          ["notNull", "Has Value"],
        ] as const).map(([mode, label]) => (
          <button
            key={mode}
            onClick={() => buildCriterion(selectedValues, mode)}
            className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
              selectionMode === mode
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {label}
          </button>
        ))}
      </div>
      {(selectionMode === "include" || selectionMode === "exclude") && (
        <div className="grid gap-1 sm:grid-cols-2">
          {options.map((option) => {
            const checked = selectedValues.includes(option.value);

            return (
              <label key={option.value} className="flex min-h-9 items-center gap-2 rounded-lg border border-border bg-input px-3 py-1.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleValue(option.value)}
                  className="h-5 w-5 accent-accent"
                />
                <span>{option.label}</span>
              </label>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ===== Date Editor =====

function DateEditor({ value, onChange, modifiers }: { value?: DateCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              value={value?.value ?? ""}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}

// ===== Timestamp Editor =====

function getDefaultLocalTimestampValue() {
  const date = new Date();
  date.setHours(12, 0, 0, 0);

  const pad = (part: number) => String(part).padStart(2, "0");

  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function TimestampEditor({ value, onChange, modifiers }: { value?: TimestampCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  const ensureTimestampValue = (current?: string) => (current && current.length > 0 ? current : getDefaultLocalTimestampValue());

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => {
          const nextIsNull = m === "IS_NULL" || m === "NOT_NULL";
          const nextIsBetween = m === "BETWEEN" || m === "NOT_BETWEEN";
          onChange({
            value: nextIsNull ? (value?.value ?? "") : ensureTimestampValue(value?.value),
            value2: nextIsBetween ? ensureTimestampValue(value?.value2) : undefined,
            modifier: m,
          });
        }}
      />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              pickerType="datetime-local"
              value={value?.value ?? ensureTimestampValue(value?.value)}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                pickerType="datetime-local"
                value={value?.value2 ?? ensureTimestampValue(value?.value2)}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}

// ===== MultiId Editor =====

// ===== Filter Button for ListPage =====

export function FilterButton({
  activeCount,
  onClick,
}: {
  activeCount: number;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={activeCount > 0 ? `Filters, ${activeCount} active` : "Filters"}
      className={`flex items-center gap-1 rounded border px-2 py-1 text-xs ${
        activeCount > 0
          ? "border-accent bg-accent/10 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent hover:text-foreground"
      }`}
    >
      <svg className="h-3.5 w-3.5" aria-hidden="true" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
      </svg>
      Filters
      {activeCount > 0 && (
        <span className="min-w-[16px] rounded-full bg-accent px-1 py-0 text-center text-[10px] font-bold text-white" aria-hidden="true">
          {activeCount}
        </span>
      )}
    </button>
  );
}
