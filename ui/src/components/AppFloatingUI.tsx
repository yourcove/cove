import { ExtensionSlot } from "../router/RouteRegistry";

/**
 * Viewport-level slot for extension UI that should persist independently of
 * the active page. Contributions own their placement within the viewport.
 */
export const APP_FLOATING_UI_SLOT = "app-floating-ui" as const;

export function AppFloatingUI() {
  return (
    <div
      className="pointer-events-none fixed inset-0 z-[70]"
      data-testid="app-floating-ui-layer"
    >
      <ExtensionSlot
        slot={APP_FLOATING_UI_SLOT}
        context={{}}
        fallback={null}
        entryClassName="pointer-events-auto contents"
      />
    </div>
  );
}
