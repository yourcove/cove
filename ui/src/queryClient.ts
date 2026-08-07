import { MutationCache, QueryClient } from "@tanstack/react-query";
import { reportMutationFailure } from "./state/mutationFailure";

declare module "@tanstack/react-query" {
  interface Register {
    mutationMeta: {
      suppressGlobalError?: boolean;
    };
  }
}

export function createAppQueryClient(): QueryClient {
  return new QueryClient({
    mutationCache: new MutationCache({
      onError: (error, _variables, _onMutateResult, mutation) => {
        // Error handlers often only roll back optimistic state, so suppression
        // must be explicit when a workflow already presents its own feedback.
        if (mutation.meta?.suppressGlobalError === true) return;
        reportMutationFailure(error);
      },
    }),
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        retry: 1,
      },
    },
  });
}
