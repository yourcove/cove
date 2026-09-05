import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent as ReactDragEvent,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
import { ChevronDown, ChevronRight, GripVertical, MoreHorizontal, Plus } from "lucide-react";
import type { FilterExpression } from "../api/types";
import {
  ActiveObjectFilterChips,
  removeObjectFilterChipTarget,
  type FilterChipTarget,
} from "./ActiveObjectFilterChips";
import {
  FILTER_EXPRESSION_OPERATOR_PRESENTATION,
  getFilterExpressionPresentationOperator,
  normalizeFilterExpressionOperator,
  sortFilterExpressionChildrenForDisplay,
} from "../utils/filterExpressionPresentation";
import {
  countFilterExpressionConditions,
  expressionPathsEqual,
  getExpressionLeaf,
  moveExpressionLeaf,
  type EditableFilterExpression,
  type ExpressionGroupDestination,
} from "../utils/filterExpressionTree";
import { MAX_DISTINCT_RELATED_CONDITIONS, type CriterionDefinition } from "./filterCriteriaTypes";
import { useRatingOptions } from "./Rating";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { describeFilterExpressionCondition } from "./filterExpressionExplanation";

export function FilterExpressionEditor({
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
  const conditionCount = countFilterExpressionConditions(value);
  const [keyboardMove, setKeyboardMove] = useState<{ sourcePath: number[]; destinationIndex: number } | null>(null);
  const [draggedPath, setDraggedPath] = useState<number[] | null>(null);
  const [moveAnnouncement, setMoveAnnouncement] = useState("");
  const ratingOptions = useRatingOptions();
  const appConfig = useOptionalAppConfig();
  const metadataServers = appConfig?.config?.scraping?.metadataServers ?? [];
  const describeCondition = (filter: Record<string, unknown>) => describeFilterExpressionCondition(filter, criteria, ratingOptions, metadataServers);
  const destinations = useMemo(() => {
    const result: ExpressionGroupDestination[] = [];
    const visit = (group: EditableFilterExpression, path: number[], depth: number) => {
      const mode = getFilterExpressionPresentationOperator(group);
      const firstLeaf = group.children.find((child) => child.filter)?.filter;
      if (mode !== "NOT") result.push({
        path,
        depth,
        relatedScopeFilterKey: group.relatedScope?.filterKey,
        relatedScopeMatchMode: group.relatedScope?.matchMode,
        childCount: group.children.length,
        label: path.length === 0
          ? `Outermost ${FILTER_EXPRESSION_OPERATOR_PRESENTATION[mode].label} group`
          : `${FILTER_EXPRESSION_OPERATOR_PRESENTATION[mode].label} group${firstLeaf ? ` containing ${describeCondition(firstLeaf)}` : ""}`,
      });
      for (const { child, index } of sortFilterExpressionChildrenForDisplay(group.children, mode)) {
        if (child.group) visit(child.group as EditableFilterExpression, [...path, index], depth + 1);
      }
    };
    visit(value as EditableFilterExpression, [], 0);
    return result;
  }, [criteria, metadataServers, ratingOptions, value]);
  const legalDestinations = useCallback((sourcePath: number[]) => {
    const sourceFilter = getExpressionLeaf(value, sourcePath);
    return destinations.filter((destination) => {
      if (expressionPathsEqual(destination.path, sourcePath.slice(0, -1))) return false;
      if (!destination.relatedScopeFilterKey) return true;
      if (destination.relatedScopeMatchMode === "distinct" && destination.childCount >= MAX_DISTINCT_RELATED_CONDITIONS) return false;
      const related = sourceFilter?.[destination.relatedScopeFilterKey];
      if (!related || typeof related !== "object") return false;
      const criterion = related as { mode?: string; exclude?: boolean };
      return criterion.exclude !== true && (criterion.mode === undefined || criterion.mode === "atLeastOne");
    });
  }, [destinations, value]);
  const moveCondition = useCallback((sourcePath: number[], destinationPath: number[]) => {
    const moved = moveExpressionLeaf(value as EditableFilterExpression, sourcePath, destinationPath);
    if (!moved.insertedPath) return;
    onChange(moved.expression);
    setKeyboardMove(null);
    setDraggedPath(null);
    const destination = destinations.find((candidate) => expressionPathsEqual(candidate.path, destinationPath));
    setMoveAnnouncement(`Condition moved to ${destination?.label ?? "the selected group"}.`);
    window.setTimeout(() => window.setTimeout(() => {
      document.querySelector<HTMLElement>(`[data-expression-move-handle="${moved.insertedPath?.join(".")}"]`)?.focus();
    }, 0), 0);
  }, [destinations, onChange, value]);
  const handleMoveKeyDown = useCallback((sourcePath: number[], event: ReactKeyboardEvent<HTMLElement>) => {
    const legal = legalDestinations(sourcePath);
    const active = keyboardMove && expressionPathsEqual(keyboardMove.sourcePath, sourcePath) ? keyboardMove : null;
    if ((event.key === " " || event.key === "Enter") && !active) {
      event.preventDefault();
      if (legal.length === 0) return;
      setKeyboardMove({ sourcePath, destinationIndex: 0 });
      setMoveAnnouncement(`Condition picked up. Destination: ${legal[0].label}. Use Up and Down to choose a group, Enter to move, or Escape to cancel.`);
      return;
    }
    if (!active) return;
    if (event.key === "Escape") {
      event.preventDefault();
      setKeyboardMove(null);
      setMoveAnnouncement("Move cancelled.");
      return;
    }
    if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      event.preventDefault();
      const direction = event.key === "ArrowDown" ? 1 : -1;
      const destinationIndex = (active.destinationIndex + direction + legal.length) % legal.length;
      setKeyboardMove({ sourcePath, destinationIndex });
      setMoveAnnouncement(`Destination: ${legal[destinationIndex].label}.`);
      return;
    }
    if (event.key === " " || event.key === "Enter") {
      event.preventDefault();
      const destination = legal[active.destinationIndex];
      if (destination) moveCondition(sourcePath, destination.path);
    }
  }, [keyboardMove, legalDestinations, moveCondition]);
  const activeKeyboardDestination = keyboardMove ? legalDestinations(keyboardMove.sourcePath)[keyboardMove.destinationIndex] : undefined;
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-3 py-5 md:px-8 md:py-7">
      <div className="mx-auto max-w-3xl space-y-4">
        <p className="text-sm font-medium text-secondary">Find {subjectLabel} where</p>
        <div data-expression-tree>
          <ExpressionGroupEditor group={value} groupPath={[]} criteria={criteria} root conditionCount={conditionCount} onChange={onChange} onAddCondition={onAddCondition} onEditCondition={onEditCondition} describeCondition={describeCondition} destinations={destinations} legalDestinations={legalDestinations} keyboardMove={keyboardMove} activeKeyboardDestination={activeKeyboardDestination} draggedPath={draggedPath} onMoveCondition={moveCondition} onMoveKeyDown={handleMoveKeyDown} onDragStart={(path, event) => { setDraggedPath(path); event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData("text/plain", path.join(".")); }} onDragEnd={() => setDraggedPath(null)} />
        </div>
        <span className="sr-only" role="status">{moveAnnouncement}</span>
      </div>
    </div>
  );
}
const MAX_FILTER_EXPRESSION_DEPTH = 8;
export const MAX_FILTER_EXPRESSION_CONDITIONS = 100;

function ExpressionGroupEditor({
  group,
  groupPath,
  criteria,
  root = false,
  conditionCount,
  onChange,
  onAddCondition,
  onEditCondition,
  parentOperator,
  ungroupChildFromParent,
  removeGroupFromParent,
  describeCondition,
  destinations,
  legalDestinations,
  keyboardMove,
  activeKeyboardDestination,
  draggedPath,
  onMoveCondition,
  onMoveKeyDown,
  onDragStart,
  onDragEnd,
}: {
  group: FilterExpression<Record<string, unknown>>;
  groupPath: number[];
  criteria: CriterionDefinition[];
  root?: boolean;
  conditionCount: number;
  onChange: (value: FilterExpression<Record<string, unknown>>) => void;
  onAddCondition: (criterionId?: string, parentPath?: number[]) => void;
  onEditCondition: (path: number[], target?: FilterChipTarget) => void;
  parentOperator?: "AND" | "OR" | "JUST_ONE" | "NOT";
  ungroupChildFromParent?: () => void;
  removeGroupFromParent?: () => void;
  describeCondition: (filter: Record<string, unknown>) => string;
  destinations: ExpressionGroupDestination[];
  legalDestinations: (sourcePath: number[]) => ExpressionGroupDestination[];
  keyboardMove: { sourcePath: number[]; destinationIndex: number } | null;
  activeKeyboardDestination?: ExpressionGroupDestination;
  draggedPath: number[] | null;
  onMoveCondition: (sourcePath: number[], destinationPath: number[]) => void;
  onMoveKeyDown: (sourcePath: number[], event: ReactKeyboardEvent<HTMLElement>) => void;
  onDragStart: (sourcePath: number[], event: ReactDragEvent<HTMLElement>) => void;
  onDragEnd: () => void;
}) {
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [groupingMode, setGroupingMode] = useState(false);
  const [openMenu, setOpenMenu] = useState<"group" | number | null>(null);
  const [moveMenuIndex, setMoveMenuIndex] = useState<number | null>(null);
  const [operatorPickerOpen, setOperatorPickerOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const [announcement, setAnnouncement] = useState("");
  const childFocusRefs = useRef(new Map<number, HTMLElement>());
  const conditionMenuButtonRefs = useRef(new Map<number, HTMLButtonElement>());
  const groupMenuButtonRef = useRef<HTMLButtonElement>(null);
  const operatorButtonRef = useRef<HTMLButtonElement>(null);
  const addConditionRef = useRef<HTMLButtonElement>(null);
  const groupPathKey = groupPath.join(".");
  const operator = normalizeFilterExpressionOperator(group.operator);
  const mode = getFilterExpressionPresentationOperator(group as EditableFilterExpression);
  const presentation = FILTER_EXPRESSION_OPERATOR_PRESENTATION[mode];
  const displayedChildren = sortFilterExpressionChildrenForDisplay(group.children, mode);
  const canCreateNestedGroup = !group.relatedScope && groupPath.length + 2 <= MAX_FILTER_EXPRESSION_DEPTH;
  const hasGroupActions = !root || (mode !== "NOT" && group.children.length > 0 && canCreateNestedGroup);
  const relatedScopeCriterion = group.relatedScope
    ? criteria.find((criterion) => criterion.type === "related" && criterion.filterKey === group.relatedScope?.filterKey)
    : undefined;
  const relatedItemName = relatedScopeCriterion?.entityType === "galleries"
    ? "gallery"
    : relatedScopeCriterion?.entityType?.replace(/s$/, "") ?? "item";
  const relatedItemArticle = /^[aeiou]/i.test(relatedItemName) ? "an" : "a";
  const distinctScopeAtLimit = group.relatedScope?.matchMode === "distinct"
    && group.children.length >= MAX_DISTINCT_RELATED_CONDITIONS;
  const operatorText = mode === "NOT" || mode === "NONE" ? presentation.label : `${presentation.label} of ${displayedChildren.length}`;
  useEffect(() => {
    setSelected(new Set());
    setGroupingMode(false);
    setOpenMenu(null);
    setMoveMenuIndex(null);
    setOperatorPickerOpen(false);
    setCollapsed(false);
  }, [group]);
  useEffect(() => {
    if (!operatorPickerOpen) return;
    window.setTimeout(() => window.setTimeout(() => document.querySelector<HTMLButtonElement>(`[data-expression-operator-picker="${groupPathKey}"] button[aria-pressed="true"]`)?.focus(), 0), 0);
  }, [groupPathKey, operatorPickerOpen]);
  const focusChildAfterChange = (index: number) => {
    window.setTimeout(() => window.setTimeout(() => {
      const pathKey = [...groupPath, index].join(".");
      (childFocusRefs.current.get(index)
        ?? document.querySelector<HTMLElement>(`[data-expression-node-path="${pathKey}"]`)
        ?? addConditionRef.current)?.focus();
    }, 0), 0);
  };
  const updateChild = (index: number, child: FilterExpression<Record<string, unknown>>["children"][number]) => {
    const children = group.children.slice();
    children[index] = child;
    onChange({ ...group, children });
  };
  const removeChild = (index: number) => {
    const removedDisplayIndex = displayedChildren.findIndex((entry) => entry.index === index);
    const nextChildren = group.children.filter((_, candidate) => candidate !== index);
    const nextDisplayedChildren = sortFilterExpressionChildrenForDisplay(nextChildren, operator);
    const nextFocusIndex = nextDisplayedChildren[Math.min(removedDisplayIndex, nextDisplayedChildren.length - 1)]?.index;
    setSelected(new Set());
    setOpenMenu(null);
    onChange({ ...group, children: nextChildren });
    focusChildAfterChange(nextFocusIndex ?? 0);
  };
  const ungroupChild = (index: number) => {
    const child = group.children[index];
    if (!child?.group) return;
    if (group.operator === "NOT" && child.group.children.length !== 1) {
      setAnnouncement("A NOT group must contain exactly one item.");
      return;
    }
    setSelected(new Set());
    setOpenMenu(null);
    const nextChildren = [...group.children.slice(0, index), ...child.group.children, ...group.children.slice(index + 1)];
    const insertedIndexes = new Set(child.group.children.map((_, childIndex) => index + childIndex));
    const nextFocusIndex = sortFilterExpressionChildrenForDisplay(nextChildren, operator)
      .find((entry) => insertedIndexes.has(entry.index))?.index ?? index;
    onChange({ ...group, children: nextChildren });
    focusChildAfterChange(nextFocusIndex);
  };
  const reorderChild = (sourceIndex: number, insertionIndex: number) => {
    if (sourceIndex === insertionIndex || sourceIndex + 1 === insertionIndex) return;
    const children = group.children.slice();
    const [child] = children.splice(sourceIndex, 1);
    if (!child) return;
    const adjustedInsertionIndex = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
    children.splice(adjustedInsertionIndex, 0, child);
    onChange({ ...group, children });
    focusChildAfterChange(adjustedInsertionIndex);
  };
  const groupSelected = (nextMode: "AND" | "OR" | "JUST_ONE" | "NONE") => {
    const indexes = [...selected].sort((a, b) => a - b);
    if (nextMode === "NONE" ? indexes.length < 1 : indexes.length < 2) return;
    const selectedChildren = indexes.map((index) => group.children[index]);
    const scopedCriterion = nextMode === "AND" ? criteria.find((criterion) => criterion.type === "related"
      && criterion.supportsDistinctSiblingMatches
      && selectedChildren.every((child) => {
        const related = child.filter?.[criterion.filterKey];
        if (!related || typeof related !== "object") return false;
        const value = related as { mode?: string; exclude?: boolean };
        return value.exclude !== true && (value.mode === undefined || value.mode === "atLeastOne");
      })) : undefined;
    const first = indexes[0];
    const selectedSet = new Set(indexes);
    const children: FilterExpression<Record<string, unknown>>["children"] = group.children.flatMap((child, index) => index === first
      ? [{ group: (nextMode === "NONE"
        ? { operator: "OR" as const, children: selectedChildren, _semanticNone: true }
        : { operator: nextMode, children: selectedChildren, ...(scopedCriterion ? { relatedScope: { filterKey: scopedCriterion.filterKey, matchMode: "reuse" as const } } : {}) }) as EditableFilterExpression }]
      : selectedSet.has(index) ? [] : [child]);
    setSelected(new Set());
    setGroupingMode(false);
    onChange({ ...group, children });
    focusChildAfterChange(first);
  };
  const startGrouping = () => {
    if (!canCreateNestedGroup) {
      setAnnouncement(`Groups may not be nested more than ${MAX_FILTER_EXPRESSION_DEPTH} levels.`);
      return;
    }
    setCollapsed(false);
    setSelected(new Set());
    setGroupingMode(true);
    window.setTimeout(() => childFocusRefs.current.get(displayedChildren[0]?.index ?? 0)?.focus(), 0);
  };
  const setOperator = (nextMode: "AND" | "OR" | "JUST_ONE" | "NONE") => {
    setOpenMenu(null);
    setOperatorPickerOpen(false);
    const { relatedScope: _relatedScope, ...unscopedGroup } = group;
    const nextGroup = nextMode === "AND" ? group : unscopedGroup;
    onChange(nextMode === "NONE"
      ? { ...nextGroup, operator: "OR", _semanticNone: true } as EditableFilterExpression
      : { ...nextGroup, operator: nextMode, _semanticNone: undefined } as EditableFilterExpression);
    window.setTimeout(() => operatorButtonRef.current?.focus(), 0);
  };
  const toggleSelection = (index: number) => setSelected((current) => {
    const next = new Set(current);
    if (next.has(index)) next.delete(index); else next.add(index);
    return next;
  });
  const closeMenuAndRestoreFocus = () => {
    const trigger = openMenu === "group" ? groupMenuButtonRef.current : openMenu === null ? null : conditionMenuButtonRefs.current.get(openMenu);
    setOpenMenu(null);
    window.setTimeout(() => trigger?.focus(), 0);
  };
  const isActiveMoveDestination = Boolean(activeKeyboardDestination && expressionPathsEqual(activeKeyboardDestination.path, groupPath));
  const isAvailableDropDestination = Boolean(draggedPath
    && legalDestinations(draggedPath).some((destination) => expressionPathsEqual(destination.path, groupPath)));

  return (
    <section
      className={`relative space-y-2 rounded-lg bg-card/20 py-2 pl-4 pr-2 ${isActiveMoveDestination ? "ring-2 ring-accent ring-offset-2 ring-offset-background" : isAvailableDropDestination ? "ring-1 ring-accent/50" : ""}`}
      aria-label={relatedScopeCriterion ? `${relatedScopeCriterion.label} ${presentation.label} group` : `${presentation.label} group`}
      data-expression-node-path={groupPathKey || undefined}
      data-expression-drop-path={groupPathKey}
      tabIndex={root ? undefined : -1}
      onDragOver={(event) => {
        if (!draggedPath) return;
        if (expressionPathsEqual(draggedPath.slice(0, -1), groupPath)) {
          event.stopPropagation();
          return;
        }
        if (!isAvailableDropDestination) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      }}
      onDrop={(event) => {
        if (!draggedPath) return;
        if (expressionPathsEqual(draggedPath.slice(0, -1), groupPath)) {
          event.stopPropagation();
          return;
        }
        if (!isAvailableDropDestination) return;
        event.preventDefault();
        event.stopPropagation();
        onMoveCondition(draggedPath, groupPath);
      }}
      onKeyDown={(event) => {
        if (event.key !== "Escape" || (openMenu === null && !operatorPickerOpen)) return;
        event.preventDefault();
        event.stopPropagation();
        if (operatorPickerOpen) {
          setOperatorPickerOpen(false);
          window.setTimeout(() => operatorButtonRef.current?.focus(), 0);
        } else closeMenuAndRestoreFocus();
      }}
    >
      <span aria-hidden="true" className={`absolute bottom-2 left-1.5 top-2 w-0.5 rounded-full ${presentation.railClassName}`} />
      <div className="flex min-h-10 flex-wrap items-center gap-2">
        {relatedScopeCriterion ? <span className="rounded-md bg-accent/10 px-2 py-1 text-sm font-semibold text-accent">{relatedScopeCriterion.label}</span> : null}
        {mode === "NOT" ? (
          <span className={`rounded-md px-2 py-1 text-sm font-semibold ${presentation.labelClassName}`}>{operatorText}</span>
        ) : (
          <>
            <button ref={operatorButtonRef} type="button" data-expression-group-control={groupPathKey} aria-label={`${operatorPickerOpen ? "Close" : "Change"} ${operatorText} operator`} aria-expanded={operatorPickerOpen} title={`Change ${presentation.label} group operator`} onClick={() => { setOpenMenu(null); setMoveMenuIndex(null); setOperatorPickerOpen((current) => !current); }} className={`rounded-md px-2 py-1 text-sm font-semibold ${presentation.labelClassName}`}>{operatorText}</button>
            {operatorPickerOpen ? <div data-expression-operator-picker={groupPathKey} className="inline-flex max-w-full rounded-lg bg-card p-1" role="group" aria-label="How conditions are combined">
              <button type="button" aria-label="All" aria-pressed={mode === "AND"} onClick={() => setOperator("AND")} className={`min-h-8 rounded-md px-2.5 text-sm ${mode === "AND" ? FILTER_EXPRESSION_OPERATOR_PRESENTATION.AND.selectedClassName : "text-secondary hover:text-foreground"}`}>All</button>
              <button type="button" aria-label="Any" aria-pressed={mode === "OR"} onClick={() => setOperator("OR")} className={`min-h-8 rounded-md px-2.5 text-sm ${mode === "OR" ? FILTER_EXPRESSION_OPERATOR_PRESENTATION.OR.selectedClassName : "text-secondary hover:text-foreground"}`}>Any</button>
              <button type="button" aria-label="Just One" aria-pressed={mode === "JUST_ONE"} onClick={() => setOperator("JUST_ONE")} className={`min-h-8 rounded-md px-2.5 text-sm ${mode === "JUST_ONE" ? FILTER_EXPRESSION_OPERATOR_PRESENTATION.JUST_ONE.selectedClassName : "text-secondary hover:text-foreground"}`}>Just One</button>
              <button type="button" aria-label="None" aria-pressed={mode === "NONE"} onClick={() => setOperator("NONE")} className={`min-h-8 rounded-md px-2.5 text-sm ${mode === "NONE" ? FILTER_EXPRESSION_OPERATOR_PRESENTATION.NONE.selectedClassName : "text-secondary hover:text-foreground"}`}>None</button>
            </div> : null}
          </>
        )}
        <div data-expression-group-actions className="relative ml-auto flex items-center gap-1">
          {!root ? <button type="button" aria-label={`${collapsed ? "Expand" : "Collapse"} ${presentation.label} group`} aria-expanded={!collapsed} onClick={() => { setOperatorPickerOpen(false); setOpenMenu(null); setMoveMenuIndex(null); setCollapsed((current) => !current); }} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground">{collapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}</button> : null}
          {mode !== "NOT" ? <button ref={addConditionRef} type="button" aria-label={relatedScopeCriterion ? `Add ${relatedItemName} condition` : "Add condition"} title={distinctScopeAtLimit ? `Distinct assignment supports up to ${MAX_DISTINCT_RELATED_CONDITIONS} conditions` : "Add condition"} disabled={conditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS || distinctScopeAtLimit} onClick={() => { setOperatorPickerOpen(false); setOpenMenu(null); setMoveMenuIndex(null); setCollapsed(false); onAddCondition(relatedScopeCriterion?.id, groupPath); }} data-expression-return-focus={`add-${groupPath.join(".")}`} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"><Plus className="h-4 w-4" /></button> : null}
          {hasGroupActions ? <>
          <button ref={groupMenuButtonRef} type="button" data-expression-group-control={operator === "NOT" ? groupPathKey : undefined} aria-label={`More actions for ${root ? "root " : ""}group`} aria-expanded={openMenu === "group"} onClick={() => { setOperatorPickerOpen(false); setMoveMenuIndex(null); setOpenMenu((current) => current === "group" ? null : "group"); }} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground"><MoreHorizontal className="h-4 w-4" /></button>
          {openMenu === "group" ? (
            <div role="group" aria-label="Group actions" className="absolute right-0 top-full z-20 mt-1 min-w-44 rounded-lg border border-border bg-surface p-1 shadow-xl">
              {mode !== "NOT" && group.children.length > 0 ? <button type="button" onClick={() => { setOpenMenu(null); startGrouping(); }} disabled={!canCreateNestedGroup} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Create subgroup</button> : null}
              {!root ? <>
                <button type="button" onClick={() => { setOpenMenu(null); ungroupChildFromParent?.(); }} disabled={!ungroupChildFromParent || (parentOperator === "NOT" && group.children.length !== 1)} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Dissolve group</button>
                <button type="button" onClick={() => { setOpenMenu(null); removeGroupFromParent?.(); }} disabled={!removeGroupFromParent || parentOperator === "NOT"} className="block min-h-10 w-full rounded px-3 text-left text-sm text-red-300 hover:bg-red-500/10 disabled:opacity-40">Remove group</button>
              </> : null}
            </div>
          ) : null}
          </> : null}
        </div>
      </div>
      {!collapsed && mode === "AND" && relatedScopeCriterion ? <div className="flex flex-wrap items-center gap-2 px-2" role="group" aria-label={`How ${relatedScopeCriterion.label.toLowerCase()} conditions choose matches`}>
        <span className="text-xs font-medium text-muted">Match assignment</span>
        <button type="button" aria-pressed={group.relatedScope?.matchMode !== "distinct"} onClick={() => onChange({ ...group, relatedScope: { filterKey: relatedScopeCriterion.filterKey, matchMode: "reuse" } })} className={`min-h-8 rounded-md px-2.5 text-sm ${group.relatedScope?.matchMode !== "distinct" ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"}`}>May reuse {relatedItemArticle} {relatedItemName}</button>
        <button type="button" aria-pressed={group.relatedScope?.matchMode === "distinct"} disabled={group.children.length > MAX_DISTINCT_RELATED_CONDITIONS} onClick={() => onChange({ ...group, relatedScope: { filterKey: relatedScopeCriterion.filterKey, matchMode: "distinct" } })} className={`min-h-8 rounded-md px-2.5 text-sm ${group.relatedScope?.matchMode === "distinct" ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"} disabled:opacity-40`}>Use a different {relatedItemName} for each</button>
        {group.children.length > MAX_DISTINCT_RELATED_CONDITIONS ? <span className="text-xs text-muted">Distinct assignment supports up to {MAX_DISTINCT_RELATED_CONDITIONS} conditions.</span> : null}
      </div> : null}
      {!collapsed ? <div className="space-y-2" data-testid="expression-group-children">
        {displayedChildren.map(({ child, index }, displayIndex) => {
          const displayPosition = displayIndex + 1;
          const childPath = [...groupPath, index];
          const childPathKey = childPath.join(".");
          const conditionDestinations = child.filter ? legalDestinations(childPath) : [];
          const displayedConditionEntries = displayedChildren.filter((entry) => Boolean(entry.child.filter));
          const displayedConditionIndex = displayedConditionEntries.findIndex((entry) => entry.index === index);
          const previousConditionIndex = displayedConditionEntries[displayedConditionIndex - 1]?.index;
          const nextConditionIndex = displayedConditionEntries[displayedConditionIndex + 1]?.index;
          const isLastDisplayedCondition = Boolean(child.filter && displayedConditionIndex === displayedConditionEntries.length - 1);
          const isKeyboardMoving = Boolean(keyboardMove && expressionPathsEqual(keyboardMove.sourcePath, childPath));
          const { _criterionId: _draftCriterionId, ...displayFilter } = child.filter ?? {};
          const isDraftOnly = Object.keys(displayFilter).length === 0;
          const updateConditionFromChip = (target: FilterChipTarget) => {
            const nextFilter = removeObjectFilterChipTarget(child.filter ?? {}, criteria, target);
            const { _criterionId: _remainingCriterionId, ...remainingFilter } = nextFilter;
            if (Object.keys(remainingFilter).length === 0) removeChild(index);
            else updateChild(index, { filter: nextFilter });
          };
          const isReorderingHere = Boolean(draggedPath && expressionPathsEqual(draggedPath.slice(0, -1), groupPath));
          const sourceIndex = isReorderingHere ? draggedPath?.at(-1) : undefined;
          return <div key={index} className="space-y-2">
            {isReorderingHere && child.filter ? <div
              data-expression-reorder-target={`before-${index}`}
              onDragOver={(event) => { event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = "move"; }}
              onDrop={(event) => { event.preventDefault(); event.stopPropagation(); if (sourceIndex !== undefined) reorderChild(sourceIndex, index); }}
              className="flex h-7 w-full items-center gap-2 rounded-md border border-dashed border-accent/60 px-2 text-xs text-accent hover:bg-accent/10 focus-visible:ring-2 focus-visible:ring-accent"
            ><span className="h-0.5 flex-1 bg-accent/60" /><span>Drop here</span><span className="h-0.5 flex-1 bg-accent/60" /></div> : null}
            {groupingMode ? (
              <button
                ref={(element) => { if (element) childFocusRefs.current.set(index, element); else childFocusRefs.current.delete(index); }}
                type="button"
                aria-pressed={selected.has(index)}
                aria-label={`Select ${child.group ? "group" : "condition"} ${displayPosition} for grouping`}
                onClick={() => toggleSelection(index)}
                className={`flex min-h-12 w-full items-center gap-3 rounded-xl border px-4 text-left text-sm transition-colors ${selected.has(index) ? "border-accent bg-accent/15 text-foreground" : "border-border bg-surface text-secondary hover:border-accent/50 hover:text-foreground"}`}
              >
                <span aria-hidden="true" className={`flex h-5 w-5 shrink-0 items-center justify-center rounded-full border ${selected.has(index) ? "border-accent bg-accent text-white" : "border-muted"}`}>{selected.has(index) ? "✓" : ""}</span>
                <span>{child.group ? `${FILTER_EXPRESSION_OPERATOR_PRESENTATION[normalizeFilterExpressionOperator(child.group.operator)].label} group` : describeCondition(child.filter ?? {})}</span>
              </button>
            ) : child.group ? (
              <ExpressionGroupEditor
                group={child.group}
                groupPath={[...groupPath, index]}
                criteria={criteria}
                conditionCount={conditionCount}
                onChange={(next) => updateChild(index, { group: next })}
                onAddCondition={onAddCondition}
                onEditCondition={onEditCondition}
                describeCondition={describeCondition}
                parentOperator={group.operator}
                ungroupChildFromParent={() => ungroupChild(index)}
                removeGroupFromParent={() => removeChild(index)}
                destinations={destinations}
                legalDestinations={legalDestinations}
                keyboardMove={keyboardMove}
                activeKeyboardDestination={activeKeyboardDestination}
                draggedPath={draggedPath}
                onMoveCondition={onMoveCondition}
                onMoveKeyDown={onMoveKeyDown}
                onDragStart={onDragStart}
                onDragEnd={onDragEnd}
              />
            ) : (
              <div
                ref={(element) => { if (element) childFocusRefs.current.set(index, element); else childFocusRefs.current.delete(index); }}
                tabIndex={-1}
                data-expression-node-path={childPathKey}
                data-expression-return-focus={`edit-${childPathKey}`}
                onFocus={(event) => {
                  if (event.target === event.currentTarget) event.currentTarget.querySelector<HTMLButtonElement>("button:not([data-expression-move-handle])")?.focus();
                }}
                role="group"
                aria-label={`Condition ${displayPosition}`}
                className="group/condition relative flex min-h-12 min-w-0 items-center gap-1 overflow-visible"
              >
                <button
                  type="button"
                  draggable
                  data-expression-move-handle={childPathKey}
                  aria-label={isKeyboardMoving && activeKeyboardDestination ? `Drop condition in ${activeKeyboardDestination.label}` : `Move condition ${displayPosition}`}
                  aria-pressed={isKeyboardMoving}
                  title="Move condition"
                  onKeyDown={(event) => onMoveKeyDown(childPath, event)}
                  onDragStart={(event) => onDragStart(childPath, event)}
                  onDragEnd={onDragEnd}
                  className="inline-flex h-9 w-7 shrink-0 cursor-grab items-center justify-center rounded text-muted hover:bg-card hover:text-foreground focus-visible:ring-2 focus-visible:ring-accent active:cursor-grabbing"
                ><GripVertical className="h-4 w-4" /></button>
                {isDraftOnly ? <button type="button" onClick={() => onEditCondition(childPath)} className="flex min-h-7 min-w-0 items-center overflow-hidden rounded-md border border-border bg-surface/70 px-2 text-left text-xs text-muted" aria-label={`Edit condition ${displayPosition}: ${describeCondition(child.filter ?? {})}`}>{describeCondition(child.filter ?? {})}</button> : <ActiveObjectFilterChips
                  criteriaDefinitions={criteria}
                  objectFilter={displayFilter}
                  onEdit={(target) => onEditCondition(childPath, target)}
                  onRemove={updateConditionFromChip}
                  embeddedInToolbar
                  primaryEditAriaLabel={`Edit condition ${displayPosition}: ${describeCondition(child.filter ?? {})}`}
                  removable={group.operator !== "NOT" || group.children.length !== 1}
                  className="!m-0 min-w-0 flex-1 !border-0 !bg-transparent !p-0"
                />}
                <div className="relative flex items-center pr-1">
                  <button ref={(element) => { if (element) conditionMenuButtonRefs.current.set(index, element); else conditionMenuButtonRefs.current.delete(index); }} type="button" aria-label={`More actions for condition ${displayPosition}`} aria-expanded={openMenu === index} onClick={() => { setOperatorPickerOpen(false); setMoveMenuIndex(null); setOpenMenu((current) => current === index ? null : index); }} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-muted opacity-100 hover:bg-card hover:text-foreground md:opacity-0 md:group-hover/condition:opacity-100 md:group-focus-within/condition:opacity-100"><MoreHorizontal className="h-4 w-4" /></button>
                  {openMenu === index ? (
                    <div role="group" aria-label={moveMenuIndex === index ? `Move condition ${displayPosition}` : `Condition ${displayPosition} actions`} className="absolute right-0 top-full z-20 mt-1 max-h-72 min-w-56 overflow-y-auto rounded-lg border border-border bg-surface p-1 shadow-xl">
                      {moveMenuIndex === index ? <>
                        <button type="button" onClick={() => setMoveMenuIndex(null)} className="block min-h-10 w-full rounded px-3 text-left text-sm text-secondary hover:bg-card">Back</button>
                        {conditionDestinations.map((destination) => <button key={destination.path.join(".") || "root"} type="button" onClick={() => { setOpenMenu(null); setMoveMenuIndex(null); onMoveCondition(childPath, destination.path); }} style={{ paddingLeft: `${0.75 + destination.depth * 0.75}rem` }} className="block min-h-10 w-full rounded pr-3 text-left text-sm hover:bg-card">{destination.label}</button>)}
                      </> : <>
                        <button type="button" onClick={() => { setOpenMenu(null); onEditCondition([...groupPath, index]); }} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card">Edit</button>
                        <button type="button" onClick={() => { setOpenMenu(null); if (previousConditionIndex !== undefined) reorderChild(index, previousConditionIndex); }} disabled={previousConditionIndex === undefined} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Move earlier</button>
                        <button type="button" onClick={() => { setOpenMenu(null); if (nextConditionIndex !== undefined) reorderChild(index, nextConditionIndex + 1); }} disabled={nextConditionIndex === undefined} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Move later</button>
                        <button type="button" onClick={() => setMoveMenuIndex(index)} disabled={conditionDestinations.length === 0} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Move to…</button>
                        <button type="button" onClick={() => removeChild(index)} disabled={group.operator === "NOT" && group.children.length === 1} className="block min-h-10 w-full rounded px-3 text-left text-sm text-red-300 hover:bg-red-500/10 disabled:opacity-40">Remove</button>
                      </>}
                    </div>
                  ) : null}
                </div>
              </div>
            )}
            {isReorderingHere && isLastDisplayedCondition ? <div
              data-expression-reorder-target="end"
              onDragOver={(event) => { event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = "move"; }}
              onDrop={(event) => { event.preventDefault(); event.stopPropagation(); if (sourceIndex !== undefined) reorderChild(sourceIndex, index + 1); }}
              className="flex h-7 w-full items-center gap-2 rounded-md border border-dashed border-accent/60 px-2 text-xs text-accent hover:bg-accent/10 focus-visible:ring-2 focus-visible:ring-accent"
            ><span className="h-0.5 flex-1 bg-accent/60" /><span>Drop here</span><span className="h-0.5 flex-1 bg-accent/60" /></div> : null}
          </div>
        })}
        {group.children.length === 0 ? <p className="px-3 py-5 text-center text-sm text-muted">No conditions in this group.</p> : null}
      </div> : null}
      {!collapsed ? <div className="flex flex-wrap items-center gap-2 pt-1">
        {groupingMode ? <div className="w-full space-y-2">
          <div className="flex min-h-10 items-center justify-between gap-3">
            <span className="text-sm text-secondary">{selected.size} selected</span>
            <button type="button" onClick={() => { setGroupingMode(false); setSelected(new Set()); }} className="min-h-10 rounded-lg px-3 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel grouping</button>
          </div>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <button type="button" aria-label="Group selected as All" disabled={selected.size < 2} onClick={() => groupSelected("AND")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">All</button>
            <button type="button" aria-label="Group selected as Any" disabled={selected.size < 2} onClick={() => groupSelected("OR")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Any</button>
            <button type="button" aria-label="Group selected as Just One" disabled={selected.size < 2} onClick={() => groupSelected("JUST_ONE")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Just One</button>
            <button type="button" aria-label="Group selected as None" disabled={selected.size < 1} onClick={() => groupSelected("NONE")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">None</button>
          </div>
        </div> : null}
        {conditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS ? <span className="text-xs text-muted">Maximum of {MAX_FILTER_EXPRESSION_CONDITIONS} conditions reached.</span> : null}
        <span className="sr-only" role="status">{announcement}</span>
      </div> : null}
    </section>
  );
}
