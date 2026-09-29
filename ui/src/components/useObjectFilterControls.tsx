import { useMemo, useState } from "react";
import type { CustomFieldEntityType } from "../api/types";
import { useRegisterKeyboardActionHandler } from "../hooks/useRegisterKeyboardActionHandler";
import {
  ActiveObjectFilterChips,
  countActiveObjectFilters,
  getFilterChipTargetKey,
  removeObjectFilterChipTarget,
} from "./ActiveObjectFilterChips";
import { customFieldEntityTypeForFilterMode, useCustomFieldFilterSection } from "./CustomFieldFilterSection";
import { FilterButton } from "./FilterButton";
import { FilterDialog, type FilterDialogPreselection } from "./FilterDialog";
import type { CriterionDefinition } from "./filterCriteriaTypes";
import { migrateLegacyPerformerFavoriteCriterion } from "./filterCriterionState";

interface ObjectFilterControlsOptions {
  /** Without criteria or a change handler there is nothing to filter, and every element is null. */
  criteriaDefinitions?: CriterionDefinition[];
  objectFilter?: Record<string, unknown>;
  onObjectFilterChange?: (filter: Record<string, unknown>) => void;
  filterMode?: string;
  customFieldEntityType?: CustomFieldEntityType;
  supportsFilterExpressions?: boolean;
  chipsClassName?: string;
}

/**
 * The Filters button, the active-filter chips and the filter dialog a list toolbar shows, with the
 * list.filters shortcut. The caller places each element; `activeObjectFilter` is the filter as the
 * controls read it, legacy criteria migrated.
 */
export function useObjectFilterControls({
  criteriaDefinitions,
  objectFilter,
  onObjectFilterChange,
  filterMode,
  customFieldEntityType,
  supportsFilterExpressions = false,
  chipsClassName,
}: ObjectFilterControlsOptions) {
  const [filterDialogOpen, setFilterDialogOpen] = useState(false);
  const [filterDialogPreselect, setFilterDialogPreselect] = useState<FilterDialogPreselection | undefined>();
  const [filterDialogInitialView, setFilterDialogInitialView] = useState<"simple" | "advanced">("simple");
  const [filterDialogExpressionPath, setFilterDialogExpressionPath] = useState<number[] | undefined>();
  const [filterDialogOpenAtRoot, setFilterDialogOpenAtRoot] = useState(false);
  const enabled = Boolean(criteriaDefinitions && onObjectFilterChange);
  const openAtRoot = () => {
    setFilterDialogPreselect(undefined);
    setFilterDialogExpressionPath(undefined);
    setFilterDialogInitialView("simple");
    setFilterDialogOpenAtRoot(true);
    setFilterDialogOpen(true);
  };
  useRegisterKeyboardActionHandler("list.filters", openAtRoot, { enabled, surface: "list" });

  const activeObjectFilter = useMemo(
    () => migrateLegacyPerformerFavoriteCriterion(objectFilter ?? {}, criteriaDefinitions ?? []),
    [criteriaDefinitions, objectFilter],
  );
  const customFieldSection = useCustomFieldFilterSection(
    customFieldEntityType ?? customFieldEntityTypeForFilterMode(filterMode),
    activeObjectFilter,
  );
  const customFilterSections = useMemo(
    () => (customFieldSection ? [customFieldSection] : undefined),
    [customFieldSection],
  );

  if (!criteriaDefinitions || !onObjectFilterChange) {
    return { activeObjectFilter, filterButton: null, filterChips: null, filterDialog: null };
  }

  const filterButton = (
    <FilterButton
      activeCount={countActiveObjectFilters(criteriaDefinitions, activeObjectFilter)}
      onClick={openAtRoot}
    />
  );

  const filterChips =
    Object.keys(activeObjectFilter).length > 0 ? (
      <ActiveObjectFilterChips
        criteriaDefinitions={criteriaDefinitions}
        objectFilter={activeObjectFilter}
        customFilterSections={customFilterSections}
        className={chipsClassName}
        onEdit={(target) => {
          const key = getFilterChipTargetKey(target);
          setFilterDialogExpressionPath(target.kind === "expression" ? target.path : undefined);
          const criterion = criteriaDefinitions.find(
            (item) =>
              item.id === key ||
              item.filterKey === key ||
              item.secondaryFilterKey === key ||
              item.auxiliaryToggleKey === key,
          );
          const customSection =
            target.kind === "root" ? customFilterSections?.find((section) => section.filterKey === key) : undefined;
          setFilterDialogPreselect(
            target.kind === "expression"
              ? undefined
              : target.kind === "related"
                ? {
                    criterionId: criterion?.id ?? key,
                    relatedFacet: target.facet,
                    nestedCriterionId: target.nestedCriterionId,
                  }
                : (customSection?.id ?? criterion?.id ?? key),
          );
          setFilterDialogInitialView(
            key === "_filterExpression" && target.kind !== "expression" ? "advanced" : "simple",
          );
          setFilterDialogOpenAtRoot(false);
          setFilterDialogOpen(true);
        }}
        onRemove={(target) =>
          onObjectFilterChange(removeObjectFilterChipTarget(activeObjectFilter, criteriaDefinitions, target))
        }
        onClearAll={() => onObjectFilterChange({})}
      />
    ) : null;

  const filterDialog = (
    <FilterDialog
      open={filterDialogOpen}
      onClose={() => {
        setFilterDialogOpen(false);
        setFilterDialogPreselect(undefined);
        setFilterDialogInitialView("simple");
        setFilterDialogExpressionPath(undefined);
        setFilterDialogOpenAtRoot(false);
      }}
      criteria={criteriaDefinitions}
      customSections={customFilterSections}
      activeFilter={activeObjectFilter}
      onApply={onObjectFilterChange}
      preselectCriterion={filterDialogPreselect}
      initialView={filterDialogInitialView}
      initialExpressionPath={filterDialogExpressionPath}
      openAtRoot={filterDialogOpenAtRoot}
      supportsFilterExpressions={supportsFilterExpressions || Boolean(activeObjectFilter._filterExpression)}
    />
  );

  return { activeObjectFilter, filterButton, filterChips, filterDialog };
}
