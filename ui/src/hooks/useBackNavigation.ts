import { useMemo } from "react";
import type { Route } from "../router/location";
import { getPreviousInternalRoute } from "../router/location";

export function useBackNavigation(fallbackRoute: Route, onNavigate: (route: Route) => void) {
  const backTarget = useMemo(
    () => getPreviousInternalRoute(fallbackRoute),
    [fallbackRoute.id, fallbackRoute.page],
  );

  const goBack = () => {
    if (backTarget.hasHistory) {
      window.history.back();
      return;
    }

    onNavigate(fallbackRoute);
  };

  return {
    backLabel: `Back to ${backTarget.label}`,
    goBack,
  };
}