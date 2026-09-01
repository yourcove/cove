export type FilterExpressionOperator = "AND" | "OR" | "NOT";

export const FILTER_EXPRESSION_OPERATOR_PRESENTATION: Record<FilterExpressionOperator, {
  label: "All" | "Any" | "Exclude";
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
  NOT: {
    label: "Exclude",
    rank: 2,
    containerClassName: "filter-expression-exclude-container border-rose-500/45 bg-rose-500/5",
    labelClassName: "filter-expression-exclude-label bg-rose-500/15 text-rose-300 hover:bg-rose-500/25",
    selectedClassName: "bg-rose-500 text-white shadow-sm",
  },
};

export function normalizeFilterExpressionOperator(operator: unknown): FilterExpressionOperator {
  const normalized = typeof operator === "string" ? operator.toUpperCase() : "AND";
  return normalized === "OR" || normalized === "NOT" ? normalized : "AND";
}

export function sortFilterExpressionChildrenForDisplay<T extends { group?: { operator?: unknown } }>(
  children: readonly T[],
  parentOperator: FilterExpressionOperator,
): Array<{ child: T; index: number }> {
  return children
    .map((child, index) => ({ child, index }))
    .sort((left, right) => {
      const leftOperator = left.child.group ? normalizeFilterExpressionOperator(left.child.group.operator) : parentOperator;
      const rightOperator = right.child.group ? normalizeFilterExpressionOperator(right.child.group.operator) : parentOperator;
      return FILTER_EXPRESSION_OPERATOR_PRESENTATION[leftOperator].rank - FILTER_EXPRESSION_OPERATOR_PRESENTATION[rightOperator].rank;
    });
}
