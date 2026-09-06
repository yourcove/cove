export function orderDetailTabsByMenuItems<T extends { key: string }>(
  tabs: T[],
  menuItems: readonly string[] | null | undefined,
): T[] {
  if (!menuItems?.length) return tabs;

  const menuOrder = new Map(menuItems.map((key, index) => [key, index]));

  return tabs
    .map((tab, index) => ({ tab, index, menuIndex: menuOrder.get(tab.key) }))
    .sort((left, right) => {
      if (left.menuIndex != null && right.menuIndex != null) {
        return left.menuIndex - right.menuIndex;
      }
      if (left.menuIndex != null) return -1;
      if (right.menuIndex != null) return 1;
      return left.index - right.index;
    })
    .map(({ tab }) => tab);
}

export function getFirstDetailTabByMenuItems<T extends string>(
  tabKeys: readonly T[],
  menuItems: readonly string[] | null | undefined,
): T | undefined {
  return orderDetailTabsByMenuItems(
    tabKeys.map((key) => ({ key })),
    menuItems,
  )[0]?.key;
}
