import type { FilterExpression } from "../api/types";

export const FILTER_EXPRESSION_STATE_KEY = "_filterExpression";

export type EditableFilterExpression = FilterExpression<Record<string, unknown>> & {
  _semanticNone?: boolean;
  _moveTarget?: boolean;
};

export interface ExpressionGroupDestination {
  path: number[];
  depth: number;
  label: string;
}

export function toEditableFilterExpression(expression: FilterExpression<Record<string, unknown>>): EditableFilterExpression {
  const children = expression.children.map((child) => child.group
    ? { group: toEditableFilterExpression(child.group) }
    : child);
  if (expression.operator !== "NOT" || children.length !== 1) return { ...expression, children };
  const onlyChild = children[0];
  if ("filter" in onlyChild && onlyChild.filter) return { operator: "OR", children: [onlyChild], _semanticNone: true };
  if (onlyChild.group?.operator === "OR" && !(onlyChild.group as EditableFilterExpression)._semanticNone) {
    return { operator: "OR", children: onlyChild.group.children, _semanticNone: true };
  }
  return { ...expression, children };
}

export function normalizeFilterExpressionForEditing(filter: Record<string, unknown>): Record<string, unknown> {
  const expression = filter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
  return expression ? { ...filter, [FILTER_EXPRESSION_STATE_KEY]: toEditableFilterExpression(expression) } : filter;
}

export function countFilterExpressionConditions(expression: FilterExpression<Record<string, unknown>> | undefined): number {
  if (!expression) return 0;
  return expression.children.reduce((count, child) => count + (child.group ? countFilterExpressionConditions(child.group) : child.filter ? 1 : 0), 0);
}

export function isComplexFilterExpression(expression: FilterExpression<Record<string, unknown>> | undefined): boolean {
  return Boolean(expression && (expression.operator !== "AND" || expression.children.some((child) => child.group)));
}

export function replaceExpressionGroup(
  root: FilterExpression<Record<string, unknown>>,
  groupPath: number[],
  update: (group: FilterExpression<Record<string, unknown>>) => FilterExpression<Record<string, unknown>>,
): FilterExpression<Record<string, unknown>> {
  if (groupPath.length === 0) return update(root);
  const [index, ...rest] = groupPath;
  const child = root.children[index];
  if (!child?.group) return root;
  const children = root.children.slice();
  children[index] = { group: replaceExpressionGroup(child.group, rest, update) };
  return { ...root, children };
}

export function removeExpressionGroup(root: FilterExpression<Record<string, unknown>>, groupPath: number[]): FilterExpression<Record<string, unknown>> | undefined {
  if (groupPath.length === 0) return undefined;
  const parentPath = groupPath.slice(0, -1);
  const groupIndex = groupPath.at(-1);
  if (groupIndex === undefined) return root;
  return replaceExpressionGroup(root, parentPath, (parent) => ({
    ...parent,
    children: parent.children.filter((_, index) => index !== groupIndex),
  }));
}

export function getExpressionLeaf(root: FilterExpression<Record<string, unknown>>, path: number[]): Record<string, unknown> | undefined {
  if (path.length === 0) return undefined;
  const [index, ...rest] = path;
  const child = root.children[index];
  if (!child) return undefined;
  if (rest.length === 0) return child.filter;
  return child.group ? getExpressionLeaf(child.group, rest) : undefined;
}

export function getExpressionGroup(root: FilterExpression<Record<string, unknown>>, path: number[]): FilterExpression<Record<string, unknown>> | undefined {
  if (path.length === 0) return root;
  const [index, ...rest] = path;
  const group = root.children[index]?.group;
  return group ? getExpressionGroup(group, rest) : undefined;
}

export function updateExpressionLeaf(root: FilterExpression<Record<string, unknown>>, path: number[], filter: Record<string, unknown>): FilterExpression<Record<string, unknown>> {
  const parentPath = path.slice(0, -1);
  const index = path.at(-1);
  if (index === undefined) return root;
  return replaceExpressionGroup(root, parentPath, (group) => {
    const children = group.children.slice();
    children[index] = { filter };
    return { ...group, children };
  });
}

export function expressionPathsEqual(left: number[], right: number[]) {
  return left.length === right.length && left.every((part, index) => part === right[index]);
}

export function removeExpressionLeafAndPrune(group: EditableFilterExpression, path: number[]): EditableFilterExpression | undefined {
  const [index, ...rest] = path;
  if (index === undefined) return group;
  const child = group.children[index];
  if (!child) return group;
  const children = group.children.slice();
  if (rest.length === 0) {
    if (!child.filter) return group;
    children.splice(index, 1);
  } else {
    if (!child.group) return group;
    const nextGroup = removeExpressionLeafAndPrune(child.group as EditableFilterExpression, rest);
    if (nextGroup) children[index] = { group: nextGroup };
    else children.splice(index, 1);
  }
  return children.length > 0 || group._moveTarget ? { ...group, children } : undefined;
}

function insertExpressionLeafAtMarkedGroup(
  group: EditableFilterExpression,
  child: FilterExpression<Record<string, unknown>>["children"][number],
  path: number[] = [],
): { group: EditableFilterExpression; insertedPath?: number[] } {
  if (group._moveTarget) {
    const { _moveTarget: _marker, ...rest } = group;
    return { group: { ...rest, children: [...group.children, child] }, insertedPath: [...path, group.children.length] };
  }
  let insertedPath: number[] | undefined;
  const children = group.children.map((candidate, index) => {
    if (!candidate.group || insertedPath) return candidate;
    const inserted = insertExpressionLeafAtMarkedGroup(candidate.group as EditableFilterExpression, child, [...path, index]);
    if (inserted.insertedPath) insertedPath = inserted.insertedPath;
    return { group: inserted.group };
  });
  return { group: { ...group, children }, insertedPath };
}

export function moveExpressionLeaf(
  root: EditableFilterExpression,
  sourcePath: number[],
  destinationPath: number[],
): { expression: EditableFilterExpression; insertedPath?: number[] } {
  if (expressionPathsEqual(sourcePath.slice(0, -1), destinationPath)) return { expression: root };
  const filter = getExpressionLeaf(root, sourcePath);
  if (!filter) return { expression: root };
  const marked = replaceExpressionGroup(root, destinationPath, (group) => ({ ...group, _moveTarget: true } as EditableFilterExpression)) as EditableFilterExpression;
  const withoutSource = removeExpressionLeafAndPrune(marked, sourcePath);
  if (!withoutSource) return { expression: root };
  const inserted = insertExpressionLeafAtMarkedGroup(withoutSource, { filter });
  return inserted.insertedPath ? { expression: inserted.group, insertedPath: inserted.insertedPath } : { expression: root };
}
