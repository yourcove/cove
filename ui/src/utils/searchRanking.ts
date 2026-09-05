export interface LabelledSearchOption {
  label: string;
}

export function rankSearchOptions<T extends LabelledSearchOption>(options: T[], searchText: string): T[] {
  const needle = normalizeSearchText(searchText);
  if (!needle) {
    return [...options];
  }

  return [...options].sort((left, right) => {
    const leftLabel = normalizeSearchText(left.label);
    const rightLabel = normalizeSearchText(right.label);
    const leftRank = getSearchRank(leftLabel, needle);
    const rightRank = getSearchRank(rightLabel, needle);

    if (leftRank !== rightRank) return leftRank - rightRank;
    if (leftLabel.length !== rightLabel.length) return leftLabel.length - rightLabel.length;
    return leftLabel.localeCompare(rightLabel);
  });
}

export function rankByLabel<T>(items: T[], searchText: string, getLabel: (item: T) => string): T[] {
  return rankSearchOptions(
    items.map((item) => ({ item, label: getLabel(item) })),
    searchText,
  ).map((entry) => entry.item);
}

function getSearchRank(label: string, needle: string) {
  if (label === needle) return 0;
  if (label.startsWith(needle)) return 1;
  const wordIndex = label.search(new RegExp(`(^|\\s)${escapeRegExp(needle)}`));
  if (wordIndex >= 0) return 2;
  if (label.includes(needle)) return 3;
  return 4;
}

function normalizeSearchText(value: string) {
  return value.trim().toLocaleLowerCase();
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
