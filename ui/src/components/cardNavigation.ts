import type { MouseEventHandler } from "react";
import type { Route } from "../router/location";
import { handleModifiedRouteNavigation } from "../router/location";

export function createCardNavigationHandlers<T extends HTMLElement>(route: Route, onDefault: () => void): {
  onClick: MouseEventHandler<T>;
  onMouseDown: MouseEventHandler<T>;
  onAuxClick: MouseEventHandler<T>;
} {
  return {
    onClick: (event) => {
      if (handleModifiedRouteNavigation(event, route)) {
        return;
      }

      onDefault();
    },
    onMouseDown: (event) => {
      if (event.button === 1) {
        handleModifiedRouteNavigation(event, route);
      }
    },
    onAuxClick: (event) => {
      if (event.button === 1) {
        handleModifiedRouteNavigation(event, route);
      }
    },
  };
}

export function createNestedCardNavigationHandlers<T extends HTMLElement>(route: Route, onDefault: () => void): {
  onClick: MouseEventHandler<T>;
  onMouseDown: MouseEventHandler<T>;
  onAuxClick: MouseEventHandler<T>;
} {
  return {
    onClick: (event) => {
      event.stopPropagation();

      if (handleModifiedRouteNavigation(event, route)) {
        return;
      }

      onDefault();
    },
    onMouseDown: (event) => {
      event.stopPropagation();

      if (event.button === 1) {
        handleModifiedRouteNavigation(event, route);
      }
    },
    onAuxClick: (event) => {
      event.stopPropagation();

      if (event.button === 1) {
        handleModifiedRouteNavigation(event, route);
      }
    },
  };
}