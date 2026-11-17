import { useEffect } from "react";
import { emitLocationChange, historyStateForNewEntry, registerNavigationBlocker } from "../../../router/location";

/**
 * Asks before leaving while there are unsaved changes: on in-app navigation, on the browser's back and
 * forward buttons, and on closing or reloading the tab.
 */
export function useUnsavedChangesGuard(dirty: boolean, message: string) {
  useEffect(() => {
    if (!dirty) return;

    const url = `${window.location.pathname}${window.location.search}`;
    const historyState = window.history.state as unknown;
    const confirmNavigation = () => window.confirm(message);
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    const handlePopState = () => {
      if (confirmNavigation()) return;
      window.history.pushState(historyStateForNewEntry(historyState), "", url);
      emitLocationChange();
    };

    const unregisterBlocker = registerNavigationBlocker(confirmNavigation);
    window.addEventListener("beforeunload", handleBeforeUnload);
    window.addEventListener("popstate", handlePopState);
    return () => {
      unregisterBlocker();
      window.removeEventListener("beforeunload", handleBeforeUnload);
      window.removeEventListener("popstate", handlePopState);
    };
  }, [dirty, message]);
}
