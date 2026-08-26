import { useMemo } from "react";
import { extensions } from "../api/client";
import type { ExtensionKeyboardAction } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { useKeySequence } from "../hooks/useKeySequence";
import type { Route } from "../router/location";
import { useExtensions } from "./ExtensionLoader";

export function extensionKeyboardScopeMatches(
  scope: ExtensionKeyboardAction["scopes"][number],
  route: Route,
) {
  if (scope.page && scope.page !== route.page) return false;
  if (scope.tab) return false; // Tab-scoped actions must register from their mounted tab component.
  if (scope.entityType) {
    const routeEntity = route.id == null ? null : route.page.replace(/s$/, "");
    if (routeEntity !== scope.entityType.replace(/s$/, "")) return false;
  }
  return true;
}

export function ExtensionKeyboardActions({ route }: { route: Route }) {
  const { manifest, resolveActionHandler } = useExtensions();
  const { hasPermission } = useAuth();
  const bindings = useMemo(() => (manifest?.keyboardActions ?? []).flatMap((action) => {
    if (action.requiredPermission && !hasPermission(action.requiredPermission)) return [];
    const scope = (action.scopes ?? []).find((candidate) => extensionKeyboardScopeMatches(candidate, route));
    if (!scope || (!action.handlerName && !action.apiEndpoint)) return [];
    return [{
      id: action.id,
      keys: action.defaultBindings?.[0] ?? "",
      surface: scope.surface,
      action: () => {
        const payload = buildInvocationPayload(action, route, scope.surface);
        void invokeExtensionKeyboardAction(action, payload, resolveActionHandler).catch((error) => {
          window.alert(error instanceof Error ? error.message : "Failed to run the extension keyboard action.");
        });
      },
    }];
  }), [hasPermission, manifest?.keyboardActions, resolveActionHandler, route]);
  useKeySequence(bindings);
  return null;
}

function buildInvocationPayload(action: ExtensionKeyboardAction, route: Route, surface: string) {
  const payload: Record<string, unknown> = {
    actionId: action.id,
    extensionId: action.extensionId,
    page: route.page,
    surface,
  };
  if ("id" in route && typeof route.id === "number") payload.entityId = route.id;
  return payload;
}

async function invokeExtensionKeyboardAction(
  action: ExtensionKeyboardAction,
  payload: Record<string, unknown>,
  resolveActionHandler: ReturnType<typeof useExtensions>["resolveActionHandler"],
) {
  if (action.handlerName) {
    const handler = resolveActionHandler(action.extensionId, action.handlerName);
    if (handler) return await handler(action as never, payload);
  }
  if (action.apiEndpoint) return await extensions.invokeAction(action.apiEndpoint, payload);
  throw new Error(`Extension keyboard action '${action.id}' has no available handler.`);
}
