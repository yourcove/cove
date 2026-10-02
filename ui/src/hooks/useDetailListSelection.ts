import { useCallback, useMemo, useState } from "react";
import { useMultiSelect } from "./useMultiSelect";

interface UseDetailListSelectionOptions<TItem extends { id: string | number }> {
  items: TItem[];
  infinitePageSize: boolean;
  infiniteFilterKey: unknown;
  fetchAllIds: () => Promise<Array<TItem["id"]>>;
  resetKeyParts?: unknown[];
}

export function useDetailListSelection<TItem extends { id: string | number }>({
  items,
  infinitePageSize,
  infiniteFilterKey,
  fetchAllIds,
  resetKeyParts = [],
}: UseDetailListSelectionOptions<TItem>) {
  const selectionResetKey = useMemo(
    () => JSON.stringify({ filter: infiniteFilterKey, parts: resetKeyParts }),
    [infiniteFilterKey, resetKeyParts],
  );
  const selection = useMultiSelect(items, { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const { selectIds } = selection;
  const [selectAllPending, setSelectAllPending] = useState(false);

  const selectAllMatching = useCallback(async () => {
    setSelectAllPending(true);
    try {
      selectIds(await fetchAllIds());
    } finally {
      setSelectAllPending(false);
    }
  }, [fetchAllIds, selectIds]);

  return {
    ...selection,
    selectAll: infinitePageSize ? selectAllMatching : selection.selectAll,
    selectAllPending: infinitePageSize ? selectAllPending : false,
    selectShown: infinitePageSize ? selection.selectAll : undefined,
  };
}
