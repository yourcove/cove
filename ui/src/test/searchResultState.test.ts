import { createElement, type ReactNode } from "react";
import { QueryClient, QueryClientProvider, keepPreviousData, onlineManager, useQuery } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { getSearchResultState } from "../utils/queryLoadState";

const settled = { isError: false, isFetching: false, isPending: false, isPlaceholderData: false };

describe("getSearchResultState", () => {
  it("reports a failed search as a failure, never as no matches", () => {
    expect(getSearchResultState({ ...settled, isError: true }, 0)).toBe("error");
  });

  it("says it is searching while the current text has no answer and nothing is kept on screen", () => {
    expect(getSearchResultState({ ...settled, isPending: true, isFetching: true }, 0)).toBe("searching");
    expect(getSearchResultState({ ...settled, isPlaceholderData: true, isFetching: true }, 0)).toBe("searching");
    // Paused offline: no fetch is running, but the text still has no answer.
    expect(getSearchResultState({ ...settled, isPlaceholderData: true }, 0)).toBe("searching");
  });

  it("keeps the previous results on screen while the next search loads", () => {
    expect(getSearchResultState({ ...settled, isPlaceholderData: true, isFetching: true }, 3)).toBe("results");
  });

  it("keeps saying no matches while a settled empty answer refreshes in the background", () => {
    expect(getSearchResultState({ ...settled, isFetching: true }, 0)).toBe("empty");
  });

  it("reports settled answers as they are", () => {
    expect(getSearchResultState(settled, 0)).toBe("empty");
    expect(getSearchResultState(settled, 2)).toBe("results");
  });
});

// The same helper over a real query, as the face page's searches use it, so the flag combinations come from
// TanStack Query rather than from the test.
describe("getSearchResultState with a real search query", () => {
  afterEach(() => {
    onlineManager.setOnline(true);
  });

  function renderSearch(answer: (term: string) => Promise<string[]>) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children);
    return renderHook(
      ({ term }: { term: string }) => {
        const query = useQuery({
          queryKey: ["search", term],
          queryFn: () => answer(term),
          placeholderData: keepPreviousData,
        });
        return { query, state: getSearchResultState(query, query.data?.length ?? 0) };
      },
      { wrapper, initialProps: { term: "Zz" } },
    );
  }

  it("does not report no matches for text typed while offline after an empty answer", async () => {
    const { result, rerender } = renderSearch(async (term) => (term === "Zz" ? [] : ["Zzq Example"]));
    await waitFor(() => expect(result.current.state).toBe("empty"));

    act(() => onlineManager.setOnline(false));
    rerender({ term: "Zzq" });

    expect(result.current.query.fetchStatus).toBe("paused");
    expect(result.current.state).toBe("searching");
    act(() => onlineManager.setOnline(true));
    await waitFor(() => expect(result.current.state).toBe("results"));
  });

  it("reports a failed search, and a successful retry shows its answer", async () => {
    let failing = true;
    const { result, rerender } = renderSearch(async (term) => {
      if (term === "Ale" && failing) throw new Error("API Error 502: Bad Gateway");
      return term === "Ale" ? ["Alexis Example"] : [];
    });
    await waitFor(() => expect(result.current.state).toBe("empty"));

    rerender({ term: "Ale" });
    await waitFor(() => expect(result.current.state).toBe("error"));

    failing = false;
    await act(async () => {
      await result.current.query.refetch();
    });
    await waitFor(() => expect(result.current.state).toBe("results"));
    expect(result.current.query.data).toEqual(["Alexis Example"]);
  });
});
