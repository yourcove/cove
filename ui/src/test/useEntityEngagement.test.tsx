import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

const mockSetRating = vi.fn();
const mockGet = vi.fn();

vi.mock("../api/client", () => ({
  entityEngagement: {
    get: (...args: unknown[]) => mockGet(...args),
    setRating: (...args: unknown[]) => mockSetRating(...args),
    setFavorite: vi.fn(),
  },
}));

import { useEntityEngagement } from "../hooks/useEntityEngagement";

describe("useEntityEngagement", () => {
  it("stores a rating saved for one image under that image after the viewer moved to another", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    queryClient.setQueryData(["engagement", "image", 1], { hostId: 1, rating: null, likeCount: 0 });
    queryClient.setQueryData(["engagement", "image", 2], { hostId: 2, rating: 20, likeCount: 5 });
    let resolveSave!: (value: unknown) => void;
    mockSetRating.mockReturnValue(new Promise((resolve) => (resolveSave = resolve)));
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result, rerender } = renderHook(({ id }) => useEntityEngagement("image", id), {
      wrapper,
      initialProps: { id: 1 },
    });

    act(() => result.current.setRating(80));
    await waitFor(() => expect(mockSetRating).toHaveBeenCalledWith("image", 1, { value: 80, aspect: "overall" }));

    // The viewer turns to the next image while the save is still in flight.
    rerender({ id: 2 });
    await act(async () => resolveSave({ hostId: 1, rating: 80, likeCount: 0 }));

    await waitFor(() => expect(queryClient.getQueryData(["engagement", "image", 1])).toMatchObject({ rating: 80 }));
    expect(queryClient.getQueryData(["engagement", "image", 2])).toMatchObject({ hostId: 2, rating: 20, likeCount: 5 });
    expect(result.current.rating).toBe(20);
  });
});
