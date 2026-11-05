import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { extensions } from "../api/client";
import { useExtensionNames } from "../components/useExtensionNames";

vi.mock("../api/client", () => ({
  extensions: { list: vi.fn() },
}));

const list = vi.mocked(extensions.list);

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  list.mockReset();
  list.mockResolvedValue([
    { id: "com.example.hashes", name: "Hash Helper" },
    { id: "com.example.other", name: "Other Extension" },
  ] as Awaited<ReturnType<typeof extensions.list>>);
});

describe("useExtensionNames", () => {
  it("does not load the installed extensions until the User Guide needs their names", () => {
    const { result } = renderHook(() => useExtensionNames(false), { wrapper });

    expect(list).not.toHaveBeenCalled();
    expect(result.current.size).toBe(0);
  });

  it("maps each installed extension's ID to its name once enabled", async () => {
    const { result, rerender } = renderHook(({ enabled }) => useExtensionNames(enabled), {
      wrapper,
      initialProps: { enabled: false },
    });

    rerender({ enabled: true });

    await waitFor(() => expect(result.current.get("com.example.hashes")).toBe("Hash Helper"));
    expect(result.current.get("com.example.other")).toBe("Other Extension");
    expect(list).toHaveBeenCalledTimes(1);
  });
});
