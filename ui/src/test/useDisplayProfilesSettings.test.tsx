import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SegmentDisplayProfile } from "../api/types";
import { useDisplayProfilesSettings } from "../pages/settings/displayProfiles/useDisplayProfilesSettings";

const mocks = vi.hoisted(() => ({
  listProfiles: vi.fn(),
  listRules: vi.fn(),
}));

vi.mock("../api/client", () => ({
  segmentDisplayProfiles: {
    list: mocks.listProfiles,
    rules: { list: mocks.listRules },
    preview: vi.fn(),
  },
  segmentLibrary: {
    distinctSourceKeys: vi.fn().mockResolvedValue([]),
    distinctKinds: vi.fn().mockResolvedValue([]),
  },
  videos: { find: vi.fn(), get: vi.fn() },
  tags: { get: vi.fn() },
}));

function profile(id: number, isDefault = false): SegmentDisplayProfile {
  return {
    id,
    name: `Profile ${id}`,
    isSystem: false,
    isDefault,
    version: 1,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  };
}

function renderSettingsHook(queryClient: QueryClient) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return renderHook(() => useDisplayProfilesSettings(), { wrapper });
}

describe("useDisplayProfilesSettings profile selection", () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    vi.clearAllMocks();
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    mocks.listRules.mockResolvedValue([]);
  });

  it("selects the default profile once the list loads", async () => {
    mocks.listProfiles.mockResolvedValue([profile(1), profile(2, true)]);
    const { result } = renderSettingsHook(queryClient);

    expect(result.current.selectedProfileId).toBeNull();
    await waitFor(() => expect(result.current.selectedProfileId).toBe(2));
  });

  it("keeps a still-present selection and falls back when the selected profile disappears", async () => {
    mocks.listProfiles.mockResolvedValue([profile(1, true), profile(2), profile(3)]);
    const { result } = renderSettingsHook(queryClient);
    await waitFor(() => expect(result.current.selectedProfileId).toBe(1));

    act(() => result.current.setSelectedProfileId(3));
    mocks.listProfiles.mockResolvedValue([profile(1, true), profile(2), profile(3)]);
    await act(() => queryClient.refetchQueries({ queryKey: ["segment-display-profiles"], exact: true }));
    expect(result.current.selectedProfileId).toBe(3);

    mocks.listProfiles.mockResolvedValue([profile(1, true), profile(2)]);
    await act(() => queryClient.refetchQueries({ queryKey: ["segment-display-profiles"], exact: true }));
    await waitFor(() => expect(result.current.selectedProfileId).toBe(1));
  });

  it("clears the selection when every profile is removed", async () => {
    mocks.listProfiles.mockResolvedValue([profile(4, true)]);
    const { result } = renderSettingsHook(queryClient);
    await waitFor(() => expect(result.current.selectedProfileId).toBe(4));

    mocks.listProfiles.mockResolvedValue([]);
    await act(() => queryClient.refetchQueries({ queryKey: ["segment-display-profiles"], exact: true }));
    await waitFor(() => expect(result.current.selectedProfileId).toBeNull());
  });
});
