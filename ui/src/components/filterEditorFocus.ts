export function getFirstEditorControl(panel: HTMLElement | null | undefined): HTMLElement | null {
  return (
    panel?.querySelector<HTMLElement>("[data-filter-primary-control]") ??
    panel?.querySelector<HTMLElement>(
      "input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]):not([data-mobile-only-control])",
    ) ??
    panel?.querySelector<HTMLElement>("button:not([disabled])") ??
    null
  );
}

export function getFirstInlineEditorControl(panel: HTMLElement | null | undefined): HTMLElement | null {
  return (
    panel?.querySelector<HTMLElement>(
      "button[data-modifier][aria-pressed='true'], button[data-match-modifier][aria-pressed='true']",
    ) ?? getFirstEditorControl(panel)
  );
}
