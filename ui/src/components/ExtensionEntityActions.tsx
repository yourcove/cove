import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, Puzzle } from "lucide-react";
import { extensions } from "../api/client";
import type { ExtensionAction } from "../api/types";
import { useExtensions } from "../extensions/ExtensionLoader";
import { registerManualContext } from "./ManualContext";

interface Props {
  entityType: string;
  entityId: number;
  pageType?: string;
  renderMode?: "toolbar" | "menu";
  onInvoked?: () => void;
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

function buildActionPayload(action: ExtensionAction, entityType: string, pageType: string, entityId: number) {
  return {
    actionId: action.id,
    extensionId: action.extensionId,
    entityType,
    pageType,
    entityIds: [entityId],
    selectedIds: [entityId],
    selectedCount: 1,
  };
}

function formatQueuedMessage(action: ExtensionAction, result: unknown, entityType: string): string {
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
      return `${action.label} queued for ${entityType}.\nJob: ${queued.jobId}`;
    }
  }

  return `${action.label} queued for ${entityType}.`;
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

export function ExtensionEntityActions({ entityType, entityId, pageType, renderMode = "toolbar", onInvoked }: Props) {
  const normalizedEntityType = normalizeEntityType(entityType);
  const normalizedPageType = normalizeEntityType(pageType ?? entityType);
  const queryClient = useQueryClient();
  const { getActionsForContext, resolveActionHandler } = useExtensions();
  const [pendingActionId, setPendingActionId] = useState<string | null>(null);
  const unregisterManualContextRef = useRef<(() => void) | null>(null);

  useEffect(() => () => {
    unregisterManualContextRef.current?.();
    unregisterManualContextRef.current = null;
  }, []);

  const actions = useMemo(() => {
    if (normalizedEntityType !== "video" && normalizedEntityType !== "image") {
      return [];
    }

    return getActionsForContext(normalizedEntityType, normalizedPageType, "toolbar");
  }, [getActionsForContext, normalizedEntityType, normalizedPageType]);

  const invokeActionMut = useMutation<unknown, Error, ExtensionAction>({
    mutationFn: async (action) => {
      const payload = buildActionPayload(action, normalizedEntityType, normalizedPageType, entityId);

      if (action.handlerName) {
        const handler = resolveActionHandler(action.extensionId, action.handlerName);
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
      window.alert(formatQueuedMessage(action, result, normalizedEntityType));
    },
    onError: (error) => {
      window.alert(error.message || "Failed to run the extension action.");
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

  if (renderMode === "menu") {
    return (
      <>
        {actions.map((action) => {
          const isPending = invokeActionMut.isPending && pendingActionId === action.id;
          return (
            <button
              key={action.id}
              type="button"
              onClick={() => {
                onInvoked?.();
                invokeActionMut.mutate(action);
              }}
              disabled={invokeActionMut.isPending}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
            >
              {isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Puzzle className="h-3.5 w-3.5" />}
              {action.label}
            </button>
          );
        })}
      </>
    );
  }

  return (
    <>
      {actions.map((action) => {
        const isPending = invokeActionMut.isPending && pendingActionId === action.id;
        return (
          <button
            key={action.id}
            type="button"
            onClick={() => invokeActionMut.mutate(action)}
            disabled={invokeActionMut.isPending}
            className="flex items-center gap-1.5 rounded border border-border bg-card px-3 py-1.5 text-sm text-secondary transition-colors hover:border-accent hover:text-foreground disabled:opacity-60"
          >
            {isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Puzzle className="h-3.5 w-3.5" />}
            {action.label}
          </button>
        );
      })}
    </>
  );
}
