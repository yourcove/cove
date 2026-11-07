import { useOptionalAuth } from "../auth/optionalAuth";
import type { Route } from "../router/location";
import {
  buildCurrentUrl,
  buildRouteUrl,
  getPreviousInternalRoute,
  getRouteLabel,
  navigateToUrl,
  parseCurrentRoute,
  readBackOrigin,
  readBackReturned,
  type BackOrigin,
} from "../router/location";
import { resolveBackNavigationTarget } from "../utils/userUiPreferences";
import { restoreWindowScroll } from "../utils/restoreWindowScroll";

export interface BackNavigationAlternative {
  label: string;
  onSelect: () => void;
}

export interface BackNavigation {
  /** Label of the target the Back button uses. */
  backLabel: string;
  /** Go to the target chosen in settings. */
  goBack: () => void;
  /** The other target, offered beside the Back button when it leads somewhere different. */
  backAlternative?: BackNavigationAlternative;
  /**
   * Leave for the page the current run started from after the item itself is gone, e.g. deleted.
   * The current entry is replaced, so browser Back does not return to the missing item.
   */
  goToOrigin: () => void;
}

function returnToOrigin(origin: BackOrigin, options?: { replace?: boolean }) {
  // The origin opens as a new entry rather than rewinding history: it is a copy of where the run
  // started, so it does not depend on that entry still being reachable in this tab's history. It
  // takes back its own origin, so Back from there still leads to where that page's run began.
  const opened = navigateToUrl(origin.url, {
    state: origin.route,
    replace: options?.replace,
    backOrigin: origin.origin ?? null,
  });
  if (opened) restoreWindowScroll(origin.scrollY);
}

export function useBackNavigation(fallbackRoute: Route, onNavigate: (route: Route) => void): BackNavigation {
  const preferred = resolveBackNavigationTarget(useOptionalAuth()?.user?.uiPreferences?.backNavigationTarget);
  const previous = getPreviousInternalRoute(fallbackRoute);
  const previousUrl = previous.url ?? buildRouteUrl(fallbackRoute);
  const currentUrl = buildCurrentUrl(window.location.pathname, window.location.search);
  const recordedOrigin = readBackOrigin();
  // With a single step the origin is the previous page, so there is only one place to go back to.
  // The entry id tells, even after the page rewrote its URL (a list adding its sort or page) without
  // the recorded URL catching up.
  const originIsPrevious =
    recordedOrigin != null &&
    (recordedOrigin.url === previousUrl ||
      (previous.hasHistory && recordedOrigin.entryId != null && recordedOrigin.entryId === previous.entryId));
  const origin = recordedOrigin && !originIsPrevious && recordedOrigin.url !== currentUrl ? recordedOrigin : undefined;

  const goPrevious = () => {
    if (previous.hasHistory) {
      window.history.back();
      return;
    }

    onNavigate(fallbackRoute);
  };
  const previousLabel =
    previous.hasHistory && previous.route.page === parseCurrentRoute().page
      ? `Back to previous ${previous.label.toLowerCase()}`
      : `Back to ${previous.label}`;

  // A page reopened by going back to where a run started, whose own start was never recorded (it was
  // opened directly, or lies past the nesting cap). Its previous page is the run just left, whose
  // Back returns here again, so where it started is taken to be its list.
  if (!recordedOrigin && readBackReturned()) {
    const fallbackLabel = `Back to ${getRouteLabel(fallbackRoute)}`;
    const goFallback = () => onNavigate(fallbackRoute);
    const leaveForFallback = () => {
      if (navigateToUrl(buildRouteUrl(fallbackRoute), { state: fallbackRoute, replace: true })) window.scrollTo(0, 0);
    };
    if (!previous.hasHistory || previousUrl === buildRouteUrl(fallbackRoute)) {
      return { backLabel: fallbackLabel, goBack: goFallback, goToOrigin: leaveForFallback };
    }

    const fallbackTarget = { label: fallbackLabel, onSelect: goFallback };
    const previousTarget = {
      label: previousLabel === fallbackLabel ? `Back to previous ${previous.label.toLowerCase()}` : previousLabel,
      onSelect: goPrevious,
    };
    const [primary, alternative] =
      preferred === "origin" ? [fallbackTarget, previousTarget] : [previousTarget, fallbackTarget];
    return {
      backLabel: primary.label,
      goBack: primary.onSelect,
      backAlternative: alternative,
      goToOrigin: leaveForFallback,
    };
  }

  if (!origin) {
    return { backLabel: previousLabel, goBack: goPrevious, goToOrigin: goPrevious };
  }

  const goToOrigin = () => returnToOrigin(origin);
  const leaveForOrigin = () => returnToOrigin(origin, { replace: true });
  const originLabel = `Back to ${getRouteLabel(origin.route)}`;

  // Without a recorded previous page the fallback list is only a guess, and the origin is the better
  // place to go, so it is the single target.
  if (!previous.hasHistory) {
    return { backLabel: originLabel, goBack: goToOrigin, goToOrigin: leaveForOrigin };
  }

  // Two targets of the same kind, such as a video before a run of performers and the video just left
  // after it, would read alike; "previous" tells the one-step target apart.
  const distinctPreviousLabel =
    previousLabel === originLabel ? `Back to previous ${previous.label.toLowerCase()}` : previousLabel;
  const previousTarget = { label: distinctPreviousLabel, onSelect: goPrevious };
  const originTarget = { label: originLabel, onSelect: goToOrigin };
  const [primary, alternative] =
    preferred === "origin" ? [originTarget, previousTarget] : [previousTarget, originTarget];

  return {
    backLabel: primary.label,
    goBack: primary.onSelect,
    backAlternative: alternative,
    goToOrigin: leaveForOrigin,
  };
}
