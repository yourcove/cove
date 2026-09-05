import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { act, render, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ServerAvailabilityBanner } from "../components/ServerAvailabilityBanner";
import { reportServerResponse, resetServerAvailabilityForTests } from "../state/serverAvailability";

function ActiveQuery({ queryFn }: { queryFn: () => Promise<string> }) {
  useQuery({ queryKey: ["recovery-test"], queryFn });
  return null;
}

describe("ServerAvailabilityBanner", () => {
  afterEach(() => {
    resetServerAvailabilityForTests();
  });

  it("refetches active queries when the server becomes available again", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const queryFn = vi.fn().mockRejectedValueOnce(new TypeError("Failed to fetch")).mockResolvedValue("recovered");
    reportServerResponse(new Response("", { status: 502 }));

    render(
      <QueryClientProvider client={queryClient}>
        <ActiveQuery queryFn={queryFn} />
        <ServerAvailabilityBanner />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryFn).toHaveBeenCalledTimes(1));

    act(() => reportServerResponse(new Response("", { status: 200 })));

    await waitFor(() => expect(queryFn).toHaveBeenCalledTimes(2));
    expect(queryClient.getQueryData(["recovery-test"])).toBe("recovered");
  });
});
