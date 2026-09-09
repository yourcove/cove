import type { MouseEventHandler } from "react";
import type { Route } from "../router/location";
import { buildRouteUrl, navigateToUrl, resolveContextualDetailRoute } from "../router/location";

function isPlainPrimaryClick(event: {
  button: number;
  ctrlKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
}): boolean {
  return event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey;
}

function createRouteLinkClickHandler<T extends HTMLElement>(
  route: Route,
  onDefault?: () => void,
  options?: { stopPropagation?: boolean },
): MouseEventHandler<T> {
  const href = buildRouteUrl(route);

  return (event) => {
    if (options?.stopPropagation) {
      event.stopPropagation();
    }

    if (!isPlainPrimaryClick(event)) {
      return;
    }

    event.preventDefault();

    if (onDefault) {
      onDefault();
      return;
    }

    navigateToUrl(href, { state: route });
  };
}

export function createRouteLinkProps<T extends HTMLAnchorElement>(
  route: Route,
  onDefault?: () => void,
): {
  href: string;
  onClick: MouseEventHandler<T>;
} {
  const contextualRoute = resolveContextualDetailRoute(route);
  return {
    href: buildRouteUrl(contextualRoute),
    onClick: createRouteLinkClickHandler<T>(contextualRoute, onDefault),
  };
}

export function createNestedRouteLinkProps<T extends HTMLAnchorElement>(
  route: Route,
  onDefault?: () => void,
): {
  href: string;
  onClick: MouseEventHandler<T>;
} {
  const contextualRoute = resolveContextualDetailRoute(route);
  return {
    href: buildRouteUrl(contextualRoute),
    onClick: createRouteLinkClickHandler<T>(contextualRoute, onDefault, { stopPropagation: true }),
  };
}
