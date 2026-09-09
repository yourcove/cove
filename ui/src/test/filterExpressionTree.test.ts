import { describe, expect, it } from "vitest";
import {
  getExpressionLeaf,
  isComplexFilterExpression,
  moveExpressionLeaf,
  normalizeFilterExpressionForEditing,
  remapExpressionLeafPath,
  removeExpressionLeafAndPrune,
  repairRelatedScopes,
  updateExpressionLeaf,
  type EditableFilterExpression,
} from "../utils/filterExpressionTree";
import { VIDEO_CRITERIA } from "../components/filterCriteriaCatalogs";
import { mergeFilterExpressionWithSimpleCriteria, sanitizeFilterExpression } from "../components/filterCriterionState";

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

  it("migrates legacy distinct matching into a scoped related-entity group", () => {
    const male = { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Male" } } } } };
    const female = { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Female" } } } } };
    const count = { filter: { performerCountCriterion: { modifier: "EQUALS", value: 3 } } };

    const normalized = normalizeFilterExpressionForEditing(
      {
        _filterExpression: { operator: "AND", distinctRelatedMatches: true, children: [male, female, female, count] },
      },
      VIDEO_CRITERIA,
    );

    expect(normalized._filterExpression).toEqual({
      operator: "AND",
      children: [
        {
          group: {
            operator: "AND",
            relatedScope: { filterKey: "performerFilterCriterion", matchMode: "distinct" },
            children: [male, female, female],
          },
        },
        count,
      ],
    });
  });

  it("remaps legacy leaf paths after related conditions are scoped", () => {
    const male = { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Male" } } } } };
    const count = { filter: { performerCountCriterion: { modifier: "EQUALS", value: 3 } } };
    const female = { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Female" } } } } };
    const source = { operator: "AND" as const, distinctRelatedMatches: true, children: [male, count, female] };
    const destination = normalizeFilterExpressionForEditing({ _filterExpression: source }, VIDEO_CRITERIA)
      ._filterExpression as EditableFilterExpression;

    expect(remapExpressionLeafPath(source, destination, [2])).toEqual([0, 1]);
    expect(remapExpressionLeafPath(source, destination, [1])).toEqual([1]);
  });

  it("remaps duplicate legacy leaves by identity across nested and newly scoped groups", () => {
    const femaleFilter = { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Female" } } } };
    const source = {
      operator: "AND" as const,
      distinctRelatedMatches: true,
      children: [
        { group: { operator: "OR" as const, children: [{ filter: femaleFilter }] } },
        { filter: { ...femaleFilter } },
        { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "Male" } } } } },
        { filter: { performerCountCriterion: { modifier: "EQUALS", value: 3 } } },
      ],
    };
    const destination = normalizeFilterExpressionForEditing({ _filterExpression: source }, VIDEO_CRITERIA)
      ._filterExpression as EditableFilterExpression;

    expect(remapExpressionLeafPath(source, destination, [0, 0])).toEqual([1, 0]);
    expect(remapExpressionLeafPath(source, destination, [1])).toEqual([0, 0]);
  });

  it("drops an invalid scope after its operator changes and a leaf is edited", () => {
    const expression: EditableFilterExpression = {
      operator: "OR",
      relatedScope: { filterKey: "performerFilterCriterion", matchMode: "distinct" },
      children: [
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } } },
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: false } } } } },
      ],
    };
    const repaired = repairRelatedScopes(expression);
    expect(repaired.operator).toBe("OR");
    expect(repaired.relatedScope).toBeUndefined();
    expect(
      updateExpressionLeaf(repaired, [0], {
        performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: false } } },
      }).operator,
    ).toBe("OR");
  });

  it("keeps large reuse scopes during normalization", () => {
    const normalized = normalizeFilterExpressionForEditing(
      {
        _filterExpression: {
          operator: "AND",
          relatedScope: { filterKey: "performerFilterCriterion", matchMode: "reuse" },
          children: Array.from({ length: 9 }, () => ({
            filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } },
          })),
        },
      },
      VIDEO_CRITERIA,
    );
    expect((normalized._filterExpression as EditableFilterExpression).relatedScope?.matchMode).toBe("reuse");
    expect((normalized._filterExpression as EditableFilterExpression).children).toHaveLength(9);
  });

  it("does not deepen a maximum-depth legacy expression while migrating it", () => {
    let deepest: EditableFilterExpression = {
      operator: "AND",
      distinctRelatedMatches: true,
      children: [
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } } },
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: false } } } } },
        { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
      ],
    };
    for (let depth = 1; depth < 8; depth += 1) deepest = { operator: "AND", children: [{ group: deepest }] };
    const normalized = normalizeFilterExpressionForEditing({ _filterExpression: deepest }, VIDEO_CRITERIA)
      ._filterExpression as EditableFilterExpression;
    let target = normalized;
    for (let depth = 1; depth < 8; depth += 1) target = target.children[0].group as EditableFilterExpression;
    expect(target.distinctRelatedMatches).toBe(true);
    expect(target.relatedScope).toBeUndefined();
    expect(target.children).toHaveLength(3);
    let sanitizedTarget = sanitizeFilterExpression(normalized, VIDEO_CRITERIA) as EditableFilterExpression;
    for (let depth = 1; depth < 8; depth += 1)
      sanitizedTarget = sanitizedTarget.children[0].group as EditableFilterExpression;
    expect(sanitizedTarget.distinctRelatedMatches).toBe(true);
  });

  it("moves an ineligible edited condition outside a distinct related scope", () => {
    const performer = (gender: string) => ({
      filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: gender } } } },
    });
    const expression: EditableFilterExpression = {
      operator: "AND",
      relatedScope: { filterKey: "performerFilterCriterion", matchMode: "distinct" },
      children: [performer("Male"), performer("Female"), performer("Female")],
    };
    const edited = updateExpressionLeaf(expression, [0], {
      performerFilterCriterion: { mode: "every", objectFilter: { genderCriterion: { value: "Male" } } },
    });

    expect(edited).toEqual({
      operator: "AND",
      children: [
        {
          group: {
            operator: "AND",
            relatedScope: { filterKey: "performerFilterCriterion", matchMode: "distinct" },
            children: [performer("Female"), performer("Female")],
          },
        },
        {
          filter: { performerFilterCriterion: { mode: "every", objectFilter: { genderCriterion: { value: "Male" } } } },
        },
      ],
    });
  });

  it("keeps ordinary simple filters outside a scoped root expression", () => {
    const scope: EditableFilterExpression = {
      operator: "AND",
      relatedScope: { filterKey: "performerFilterCriterion", matchMode: "distinct" },
      children: [
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } } },
        { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: false } } } } },
      ],
    };

    expect(
      mergeFilterExpressionWithSimpleCriteria(
        {
          _filterExpression: scope,
          performerCountCriterion: { modifier: "EQUALS", value: 2 },
        },
        VIDEO_CRITERIA,
      ),
    ).toEqual({
      operator: "AND",
      children: [{ group: scope }, { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } }],
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
