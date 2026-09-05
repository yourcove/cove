import type { FindFilter } from "../api/types";

const MAX_RANDOM_SORT_SEED = 2147483647;

function generateRandomSortSeed() {
  return Math.floor(Math.random() * MAX_RANDOM_SORT_SEED) || 1;
}

export function reshuffleRandomSort(filter: FindFilter): FindFilter {
  if (filter.sort !== "random") {
    return withSeededRandomSort(filter, filter);
  }

  return {
    ...filter,
    page: 1,
    seed: generateRandomSortSeed(),
  };
}

export function withSeededRandomSort(currentFilter: FindFilter, nextFilter: FindFilter): FindFilter {
  if (nextFilter.sort === "random") {
    if (currentFilter.sort !== "random") {
      return { ...nextFilter, seed: generateRandomSortSeed() };
    }

    if (nextFilter.seed == null) {
      return { ...nextFilter, seed: currentFilter.seed ?? generateRandomSortSeed() };
    }

    return nextFilter;
  }

  if (nextFilter.seed == null) {
    return nextFilter;
  }

  const { seed: _seed, ...rest } = nextFilter;
  return rest;
}

export function seededRandomKey(id: string, seed = 1) {
  let hash = Math.abs(seed) || 1;
  for (let index = 0; index < id.length; index += 1) {
    hash = Math.imul(hash ^ id.charCodeAt(index), 16777619);
  }
  return hash >>> 0;
}

export function sortSeededRandom<T>(items: T[], idSelector: (item: T) => string, seed?: number, descending = false) {
  return [...items].sort((left, right) => {
    const leftId = idSelector(left);
    const rightId = idSelector(right);
    const comparison =
      seededRandomKey(leftId, seed) - seededRandomKey(rightId, seed) ||
      leftId.localeCompare(rightId, undefined, { numeric: true });
    return descending ? -comparison : comparison;
  });
}
