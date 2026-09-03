import { describe, expect, it } from "vitest";
import {
  getExpressionLeaf,
  isComplexFilterExpression,
  moveExpressionLeaf,
  normalizeFilterExpressionForEditing,
  removeExpressionLeafAndPrune,
  type EditableFilterExpression,
} from "../utils/filterExpressionTree";

describe("filterExpressionTree", () => {
  it("normalizes unary NOT leaves into editable semantic-none groups", () => {
    const normalized = normalizeFilterExpressionForEditing({
      _filterExpression: {
        operator: "NOT",
        children: [{ filter: { favoriteCriterion: { value: true } } }],
      },
    });

    expect(normalized._filterExpression).toEqual({
      operator: "OR",
      children: [{ filter: { favoriteCriterion: { value: true } } }],
      _semanticNone: true,
    });
  });

  it("moves a nested leaf to the root and prunes its empty source group", () => {
    const expression: EditableFilterExpression = {
      operator: "AND",
      children: [
        { group: { operator: "OR", children: [{ filter: { titleCriterion: { value: "one" } } }] } },
        { filter: { titleCriterion: { value: "two" } } },
      ],
    };

    const moved = moveExpressionLeaf(expression, [0, 0], []);

    expect(moved.insertedPath).toEqual([1]);
    expect(moved.expression.children).toHaveLength(2);
    expect(getExpressionLeaf(moved.expression, [1])).toEqual({ titleCriterion: { value: "one" } });
  });

  it("preserves a marked empty destination while pruning a moved leaf", () => {
    const expression: EditableFilterExpression = {
      operator: "AND",
      children: [{ filter: { titleCriterion: { value: "one" } } }],
      _moveTarget: true,
    };

    expect(removeExpressionLeafAndPrune(expression, [0])).toEqual({
      operator: "AND",
      children: [],
      _moveTarget: true,
    });
    expect(isComplexFilterExpression(expression)).toBe(false);
  });
});
