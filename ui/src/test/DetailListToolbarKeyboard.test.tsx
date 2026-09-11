import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DetailListToolbar } from "../components/DetailListToolbar";
import { KeyboardShortcutProvider } from "../keyboard/KeyboardShortcutProvider";
import type { FindFilter } from "../api/types";

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null, hasPermission: () => true }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
  useOptionalAppConfig: () => ({ config: { ui: {} } }),
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({ manifest: { keyboardActions: [], keyboardShortcutPresets: [] } }),
}));

vi.mock("../utils/userUiPreferences", () => ({
  updateAuthenticatedUserUiPreferences: vi.fn(),
}));

vi.mock("../utils/overlayState", () => ({ isOverlayOpen: () => false }));

function renderToolbar(filter: FindFilter, totalCount: number) {
  const onFilterChange = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <KeyboardShortcutProvider>
        <DetailListToolbar
          filter={filter}
          onFilterChange={onFilterChange}
          totalCount={totalCount}
          sortOptions={[{ value: "title", label: "Title" }]}
        />
      </KeyboardShortcutProvider>
    </QueryClientProvider>,
  );
  return onFilterChange;
}

describe("DetailListToolbar keyboard paging", () => {
  it("pages an embedded list with the list-page shortcuts", () => {
    const onFilterChange = renderToolbar({ page: 3, perPage: 10 }, 250);

    fireEvent.keyDown(document.body, { key: "ArrowRight" });
    expect(onFilterChange).toHaveBeenLastCalledWith(expect.objectContaining({ page: 4 }));

    fireEvent.keyDown(document.body, { key: "ArrowLeft" });
    expect(onFilterChange).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));

    fireEvent.keyDown(document.body, { key: "ArrowRight", shiftKey: true });
    expect(onFilterChange).toHaveBeenLastCalledWith(expect.objectContaining({ page: 13 }));
  });

  it("clamps at the ends and ignores keys while typing", () => {
    const onFilterChange = renderToolbar({ page: 1, perPage: 10 }, 25);

    fireEvent.keyDown(document.body, { key: "ArrowLeft" });
    expect(onFilterChange).not.toHaveBeenCalled();

    const input = document.createElement("input");
    document.body.appendChild(input);
    fireEvent.keyDown(input, { key: "ArrowRight" });
    expect(onFilterChange).not.toHaveBeenCalled();
    input.remove();
  });

  it("does not claim arrow keys for a single-page list", () => {
    const onFilterChange = renderToolbar({ page: 1, perPage: 10 }, 5);

    const event = new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true });
    document.body.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
    expect(onFilterChange).not.toHaveBeenCalled();
  });
});
