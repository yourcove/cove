import type { MouseEventHandler } from "react";
import type { Route } from "../router/location";
import { buildRoutePath, navigateToUrl } from "../router/location";

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
  options?: { stopPropagation?: boolean }
): MouseEventHandler<T> {
  const href = buildRoutePath(route);

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

    navigateToUrl(href);
  };
}

export function createRouteLinkProps<T extends HTMLAnchorElement>(route: Route, onDefault?: () => void): {
  href: string;
  onClick: MouseEventHandler<T>;
} {
  return {
    href: buildRoutePath(route),
    onClick: createRouteLinkClickHandler<T>(route, onDefault),
  };
}

export function createNestedRouteLinkProps<T extends HTMLAnchorElement>(route: Route, onDefault?: () => void): {
  href: string;
  onClick: MouseEventHandler<T>;
} {
  return {
    href: buildRoutePath(route),
    onClick: createRouteLinkClickHandler<T>(route, onDefault, { stopPropagation: true }),
  };
}