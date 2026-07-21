import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, Puzzle } from "lucide-react";
import { extensions } from "../api/client";
import type { ExtensionAction } from "../api/types";
import { useExtensions, renderExtensionIcon, resolveComponent } from "../extensions/ExtensionLoader";
import { registerManualContext } from "./ManualContext";

interface Props {
  entityType: string;
  selectedIds: Set<number>;
}

interface QueuedActionResponse {
  jobId?: string;
  description?: string;
  message?: string;
  cancelled?: boolean;
  suppressToast?: boolean;
}

function normalizeEntityType(entityType: string): string {
  const normalized = entityType.trim().toLowerCase();
  switch (normalized) {
    case "videos":
      return "video";
    case "images":
      return "image";
    default:
      return normalized;
  }
}

function buildActionPayload(action: ExtensionAction, entityType: string, selectedIds: number[]) {
  return {
    actionId: action.id,
    extensionId: action.extensionId,
    entityType,
    pageType: entityType,
    entityIds: selectedIds,
    selectedIds,
    selectedCount: selectedIds.length,
  };
}

function formatQueuedMessage(action: ExtensionAction, result: unknown, entityType: string, count: number): string {
  if (result && typeof result === "object") {
    const queued = result as QueuedActionResponse;
    if (queued.description && queued.jobId) {
      return `${queued.description}\nJob: ${queued.jobId}`;
    }

    if (queued.description) {
      return queued.description;
    }

    if (queued.message) {
      return queued.message;
    }

    if (queued.jobId) {
      return `${action.label} queued for ${count} ${entityType}${count === 1 ? "" : "s"}.\nJob: ${queued.jobId}`;
    }
  }

  return `${action.label} queued for ${count} ${entityType}${count === 1 ? "" : "s"}.`;
}

function shouldSuppressResultToast(result: unknown): boolean {
  return !!result
    && typeof result === "object"
    && (("cancelled" in result && Boolean((result as QueuedActionResponse).cancelled))
      || ("suppressToast" in result && Boolean((result as QueuedActionResponse).suppressToast)));
}

function shouldSuppressQueuedAlert(action: ExtensionAction, result: unknown): boolean {
  if (action.suppressSuccessAlert) {
    return true;
  }

  return false;
}

function getActionManualContexts(action: ExtensionAction) {
  return [
    `extension:${action.extensionId}`,
    `extension-action:${action.extensionId}:${action.id}`,
    `extension-action:${action.id}`,
    action.handlerName ? `extension-handler:${action.handlerName}` : undefined,
  ];
}

export function ExtensionSelectionActions({ entityType, selectedIds }: Props) {
  const normalizedEntityType = normalizeEntityType(entityType);
  const selectedIdList = useMemo(() => [...selectedIds], [selectedIds]);
  const queryClient = useQueryClient();
  const { getActionsForContext, resolveActionHandler } = useExtensions();
  const [pendingActionId, setPendingActionId] = useState<string | null>(null);
  const unregisterManualContextRef = useRef<(() => void) | null>(null);

  useEffect(() => () => {
    unregisterManualContextRef.current?.();
    unregisterManualContextRef.current = null;
  }, []);

  const actions = useMemo(() => {
    if (selectedIdList.length === 0) {
      return [];
    }

    return getActionsForContext(normalizedEntityType, undefined, "bulk");
  }, [getActionsForContext, normalizedEntityType, selectedIdList.length]);

  const invokeActionMut = useMutation<unknown, Error, ExtensionAction>({
    mutationFn: async (action) => {
      const payload = buildActionPayload(action, normalizedEntityType, selectedIdList);

      if (action.handlerName) {
        const handler = resolveActionHandler(action.handlerName);
        if (handler) {
          return await handler(action, payload);
        }
      }

      if (!action.apiEndpoint) {
        throw new Error(`Extension action '${action.id}' does not provide an API endpoint or a registered handler.`);
      }

      return await extensions.invokeAction(action.apiEndpoint, payload);
    },
    onMutate: (action) => {
      unregisterManualContextRef.current?.();
      unregisterManualContextRef.current = registerManualContext(...getActionManualContexts(action));
      setPendingActionId(action.id);
    },
    onSuccess: (result, action) => {
      if (shouldSuppressResultToast(result) || shouldSuppressQueuedAlert(action, result)) {
        return;
      }

      queryClient.invalidateQueries();
      window.alert(formatQueuedMessage(action, result, normalizedEntityType, selectedIdList.length));
    },
    onError: (error) => {
      window.alert(error.message || "Failed to run the selected extension action.");
    },
    onSettled: () => {
      unregisterManualContextRef.current?.();
      unregisterManualContextRef.current = null;
      setPendingActionId(null);
    },
  });

  if (actions.length === 0) {
    return null;
  }

  return (
    <>
      {actions.map((action) => {
        const isPending = invokeActionMut.isPending && pendingActionId === action.id;
        return (
          <button
            key={action.id}
            onClick={() => invokeActionMut.mutate(action)}
            disabled={invokeActionMut.isPending}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 disabled:opacity-60"
          >
            {isPending
              ? <Loader2 className="w-3 h-3 animate-spin" />
              : renderExtensionIcon(action.icon, action.extensionId, resolveComponent, {
                  sizeClass: "h-3 w-3",
                  fallback: <Puzzle className="h-3 w-3" />,
                })}
            {action.label}
          </button>
        );
      })}
    </>
  );
}
