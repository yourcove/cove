import type { FindFilter } from "../api/types";

const MAX_RANDOM_SORT_SEED = 2147483647;

function generateRandomSortSeed() {
  return Math.floor(Math.random() * MAX_RANDOM_SORT_SEED) || 1;
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