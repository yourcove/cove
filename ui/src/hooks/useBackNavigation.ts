import type { Route } from "../router/location";
import { getPreviousInternalRoute } from "../router/location";

export function useBackNavigation(fallbackRoute: Route, onNavigate: (route: Route) => void) {
  const backTarget = getPreviousInternalRoute(fallbackRoute);

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