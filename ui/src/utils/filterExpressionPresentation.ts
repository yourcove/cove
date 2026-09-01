export type FilterExpressionOperator = "AND" | "OR" | "NONE" | "NOT";

export const FILTER_EXPRESSION_OPERATOR_PRESENTATION: Record<FilterExpressionOperator, {
  label: "All" | "Any" | "None" | "Exclude";
  rank: number;
  containerClassName: string;
  labelClassName: string;
  selectedClassName: string;
}> = {
  AND: {
    label: "All",
    rank: 0,
    containerClassName: "border-accent/45 bg-accent/5",
    labelClassName: "bg-accent/15 text-accent hover:bg-accent/25",
    selectedClassName: "bg-accent text-white shadow-sm",
  },
  OR: {
    label: "Any",
    rank: 1,
    containerClassName: "filter-expression-any-container border-violet-500/45 bg-violet-500/5",
    labelClassName: "filter-expression-any-label bg-violet-500/15 text-violet-300 hover:bg-violet-500/25",
    selectedClassName: "filter-expression-any-selected bg-violet-500 text-white shadow-sm",
  },
  NONE: {
    label: "None",
    rank: 2,
    containerClassName: "filter-expression-exclude-container border-rose-500/45 bg-rose-500/5",
    labelClassName: "filter-expression-exclude-label bg-rose-500/15 text-rose-300 hover:bg-rose-500/25",
    selectedClassName: "bg-rose-600 text-white shadow-sm",
  },
  NOT: {
    label: "Exclude",
    rank: 3,
    containerClassName: "filter-expression-exclude-container border-rose-500/45 bg-rose-500/5",
    labelClassName: "filter-expression-exclude-label bg-rose-500/15 text-rose-300 hover:bg-rose-500/25",
    selectedClassName: "bg-rose-500 text-white shadow-sm",
  },
};

export function normalizeFilterExpressionOperator(operator: unknown): FilterExpressionOperator {
  const normalized = typeof operator === "string" ? operator.toUpperCase() : "AND";
  return normalized === "OR" || normalized === "NONE" || normalized === "NOT" ? normalized : "AND";
}

type PresentableExpression = {
  operator?: unknown;
  children?: Array<{ filter?: Record<string, unknown>; group?: PresentableExpression }>;
  _semanticNone?: boolean;
};

export function getFilterExpressionPresentationOperator(expression: PresentableExpression): FilterExpressionOperator {
  const operator = normalizeFilterExpressionOperator(expression.operator);
  if (expression._semanticNone) return "NONE";
  if (operator !== "NOT" || expression.children?.length !== 1) return operator;
  const onlyChild = expression.children[0];
  return onlyChild?.filter || normalizeFilterExpressionOperator(onlyChild?.group?.operator) === "OR" ? "NONE" : "NOT";
}

export function getFilterExpressionPresentationChildren<T extends { filter?: Record<string, unknown>; group?: PresentableExpression }>(expression: PresentableExpression & { children?: T[] }): T[] {
  const operator = getFilterExpressionPresentationOperator(expression);
  const onlyGroup = expression.children?.length === 1 ? expression.children[0]?.group : undefined;
  return operator === "NONE" && !expression._semanticNone && normalizeFilterExpressionOperator(onlyGroup?.operator) === "OR"
    ? (onlyGroup?.children ?? []) as T[]
    : expression.children ?? [];
}

export function sortFilterExpressionChildrenForDisplay<T extends { group?: { operator?: unknown } }>(
  children: readonly T[],
  parentOperator: FilterExpressionOperator,
): Array<{ child: T; index: number }> {
  return children
    .map((child, index) => ({ child, index }))
    .sort((left, right) => {
      const leftOperator = left.child.group ? getFilterExpressionPresentationOperator(left.child.group) : parentOperator;
      const rightOperator = right.child.group ? getFilterExpressionPresentationOperator(right.child.group) : parentOperator;
      return FILTER_EXPRESSION_OPERATOR_PRESENTATION[leftOperator].rank - FILTER_EXPRESSION_OPERATOR_PRESENTATION[rightOperator].rank;
    });
}
