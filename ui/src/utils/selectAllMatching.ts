import type { FindFilter, PaginatedResponse } from "../api/types";

export async function fetchAllMatchingIds<TItem extends { id: string | number }>(
  filter: FindFilter,
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<TItem>>,
  chunkSize = 1000,
) {
  const ids: Array<TItem["id"]> = [];
  const seen = new Set<string>();
  let page = 1;
  let totalCount = Number.POSITIVE_INFINITY;

  while (ids.length < totalCount) {
    const response = await queryPage({ ...filter, page, perPage: chunkSize });
    totalCount = response.totalCount;
    for (const item of response.items) {
      const key = String(item.id);
      if (seen.has(key)) continue;
      seen.add(key);
      ids.push(item.id);
    }

    if (response.items.length === 0 || response.page * response.perPage >= response.totalCount) {
      break;
    }

    page += 1;
  }

  return ids;
}
