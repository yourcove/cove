import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SegmentDisplayProfile, SegmentDisplayRule } from "../api/types";
import { emptyRuleForm } from "../pages/settings/displayProfiles/types";
import { useDisplayProfilesSettings } from "../pages/settings/displayProfiles/useDisplayProfilesSettings";

const mocks = vi.hoisted(() => ({
  listProfiles: vi.fn(),
  listRules: vi.fn(),
  replaceRules: vi.fn(),
  getProfile: vi.fn(),
}));

vi.mock("../api/client", () => ({
  segmentDisplayProfiles: {
    list: mocks.listProfiles,
    get: mocks.getProfile,
    rules: { list: mocks.listRules, replace: mocks.replaceRules },
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
    mocks.getProfile.mockImplementation(async (id: number) => profile(id));
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

function rule(id: number, priority: number, sourceKey = `source-${id}`): SegmentDisplayRule {
  return {
    id,
    sourceKey,
    visible: true,
    collapseToInstant: false,
    priority,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  };
}

describe("useDisplayProfilesSettings rule changes", () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    vi.clearAllMocks();
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    mocks.listProfiles.mockResolvedValue([profile(1, true), profile(2)]);
    mocks.listRules.mockResolvedValue([rule(10, 2), rule(11, 1)]);
    mocks.getProfile.mockImplementation(async (id: number) => profile(id, id === 1));
  });

  async function renderWithRules() {
    const rendered = renderSettingsHook(queryClient);
    await waitFor(() => expect(rendered.result.current.rules.map((item) => item.id)).toEqual([10, 11]));
    return rendered;
  }

  it("keeps edits on the page until they are saved together in one request", async () => {
    const { result } = await renderWithRules();

    act(() => result.current.deleteRule(11));
    act(() => result.current.openCreateRule());
    act(() => result.current.setRuleForm({ ...emptyRuleForm(), sourceKey: "added" }));
    act(() => result.current.saveRule());

    expect(result.current.rules.map((item) => item.sourceKey)).toEqual(["added", "source-10"]);
    expect(result.current.rules[0].id).toBeLessThan(0);
    expect(result.current.hasUnsavedChanges).toBe(true);
    expect(mocks.replaceRules).not.toHaveBeenCalled();

    const saved = [rule(12, 3, "added"), rule(10, 2)];
    mocks.replaceRules.mockResolvedValue({ profile: { ...profile(1, true), version: 4 }, rules: saved, changed: true });
    mocks.listRules.mockResolvedValue(saved);
    act(() => result.current.saveChanges());

    await waitFor(() => expect(result.current.saveNotice?.kind).toBe("saved"));
    expect(mocks.replaceRules).toHaveBeenCalledTimes(1);
    const [profileId, body] = mocks.replaceRules.mock.calls[0];
    expect(profileId).toBe(1);
    expect(body.expectedVersion).toBe(1);
    expect(body.rules.map((item: { id?: number; sourceKey?: string }) => [item.id, item.sourceKey])).toEqual([
      [undefined, "added"],
      [10, "source-10"],
    ]);
    expect(result.current.hasUnsavedChanges).toBe(false);
    expect(result.current.rules.map((item) => item.id)).toEqual([12, 10]);
  });

  it("stops counting edits that are undone as unsaved", async () => {
    const { result } = await renderWithRules();
    const [first, second] = result.current.rules;

    act(() => result.current.reorderRules([second, first]));
    expect(result.current.hasUnsavedChanges).toBe(true);
    act(() => result.current.reorderRules([first, second]));
    expect(result.current.hasUnsavedChanges).toBe(false);
  });

  it("keeps the edits and says so when the rules changed elsewhere", async () => {
    const { result } = await renderWithRules();
    act(() => result.current.deleteRule(10));
    mocks.replaceRules.mockRejectedValue(new Error("API Error 409: changed"));

    act(() => result.current.saveChanges());

    // Meanwhile someone else added a rule; discarding shows the rules as they are stored now.
    mocks.listRules.mockResolvedValue([rule(12, 3), rule(10, 2), rule(11, 1)]);
    mocks.getProfile.mockImplementation(async (id: number) => ({ ...profile(id, id === 1), version: 2 }));
    await waitFor(() => expect(result.current.saveNotice?.kind).toBe("conflict"));
    expect(result.current.hasUnsavedChanges).toBe(true);
    act(() => result.current.discardChanges());
    await waitFor(() => expect(result.current.rules.map((item) => item.id)).toEqual([12, 10, 11]));
    expect(result.current.hasUnsavedChanges).toBe(false);

    // The next save starts from the version those rules belong to.
    mocks.replaceRules.mockResolvedValue({
      profile: { ...profile(1, true), version: 3 },
      rules: [rule(12, 3)],
      changed: true,
    });
    act(() => result.current.deleteRule(10));
    act(() => result.current.deleteRule(11));
    act(() => result.current.saveChanges());
    await waitFor(() => expect(result.current.saveNotice?.kind).toBe("saved"));
    expect(mocks.replaceRules.mock.calls.at(-1)?.[1].expectedVersion).toBe(2);
  });

  it("refuses edits while a save is in flight instead of losing them", async () => {
    const { result } = await renderWithRules();
    act(() => result.current.deleteRule(11));
    let finish: (value: unknown) => void = () => {};
    mocks.replaceRules.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    act(() => result.current.saveChanges());
    await waitFor(() => expect(result.current.saveChangesPending).toBe(true));

    act(() => result.current.deleteRule(10));
    act(() => result.current.openCreateRule());
    act(() => result.current.setRuleForm({ ...emptyRuleForm(), sourceKey: "late" }));
    act(() => result.current.saveRule());
    expect(result.current.rules.map((item) => item.id)).toEqual([10]);
    expect(result.current.ruleModalOpen).toBe(true);

    await act(async () =>
      finish({ profile: { ...profile(1, true), version: 2 }, rules: [rule(10, 2)], changed: true }),
    );
    await waitFor(() => expect(result.current.saveNotice?.kind).toBe("saved"));
  });

  it("keeps a rule typed before the rules have loaded instead of dropping it", async () => {
    let finishRules: (value: SegmentDisplayRule[]) => void = () => {};
    mocks.listRules.mockReturnValue(new Promise((resolve) => (finishRules = resolve)));
    const { result } = renderSettingsHook(queryClient);
    await waitFor(() => expect(result.current.selectedProfileId).toBe(1));
    expect(result.current.rulesEditable).toBe(false);

    act(() => result.current.openCreateRule());
    act(() => result.current.setRuleForm({ ...emptyRuleForm(), sourceKey: "early" }));
    act(() => result.current.saveRule());
    expect(result.current.ruleModalOpen).toBe(true);
    expect(result.current.ruleForm.sourceKey).toBe("early");

    await act(async () => finishRules([rule(10, 2)]));
    await waitFor(() => expect(result.current.rulesEditable).toBe(true));
    act(() => result.current.saveRule());
    expect(result.current.ruleModalOpen).toBe(false);
    expect(result.current.rules.map((item) => item.sourceKey)).toEqual(["early", "source-10"]);
  });

  it("keeps an edited rule's place, even when it has no priority", async () => {
    mocks.listRules.mockResolvedValue([rule(10, 2), { ...rule(11, 0), priority: undefined }]);
    const { result } = await renderWithRules();

    act(() => result.current.openEditRule(result.current.rules[1]));
    act(() => result.current.setRuleForm((form) => ({ ...form, minDurationSec: 4 })));
    act(() => result.current.saveRule());

    expect(result.current.rules.map((item) => [item.id, item.priority, item.minDurationSec])).toEqual([
      [10, 2, undefined],
      [11, undefined, 4],
    ]);
  });

  it("asks before switching profiles with unsaved edits", async () => {
    const { result } = await renderWithRules();
    act(() => result.current.deleteRule(10));
    const confirm = vi.spyOn(window, "confirm");

    confirm.mockReturnValueOnce(false);
    act(() => result.current.setSelectedProfileId(2));
    expect(result.current.selectedProfileId).toBe(1);
    expect(result.current.hasUnsavedChanges).toBe(true);

    confirm.mockReturnValueOnce(true);
    act(() => result.current.setSelectedProfileId(2));
    expect(result.current.selectedProfileId).toBe(2);
    expect(confirm).toHaveBeenCalledTimes(2);
    confirm.mockRestore();
  });
});
