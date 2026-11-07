import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { BackNavigationButton } from "../components/BackNavigationButton";
import { KeyboardShortcutProvider } from "../keyboard/KeyboardShortcutProvider";

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

function renderButton(withAlternative: boolean) {
  const onGoBack = vi.fn();
  const onAlternative = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <KeyboardShortcutProvider>
        <BackNavigationButton
          label="Back to Videos"
          onGoBack={onGoBack}
          alternative={withAlternative ? { label: "Back to previous video", onSelect: onAlternative } : undefined}
        />
      </KeyboardShortcutProvider>
    </QueryClientProvider>,
  );
  return { onGoBack, onAlternative };
}

describe("BackNavigationButton", () => {
  it("offers both targets in a menu beside the Back button", () => {
    const { onGoBack, onAlternative } = renderButton(true);

    fireEvent.click(screen.getByRole("button", { name: "Choose where to go back to" }));
    const items = screen.getAllByRole("menuitem").map((item) => item.textContent);
    expect(items).toEqual(["Back to Videos", "Back to previous video"]);

    fireEvent.click(screen.getByRole("menuitem", { name: "Back to previous video" }));
    expect(onAlternative).toHaveBeenCalledTimes(1);
    expect(onGoBack).not.toHaveBeenCalled();
    expect(screen.queryByRole("menuitem")).toBeNull();
  });

  it("goes back with B and to the other target with Shift+B", () => {
    const { onGoBack, onAlternative } = renderButton(true);

    fireEvent.keyDown(document.body, { key: "b" });
    expect(onGoBack).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(document.body, { key: "B", shiftKey: true });
    expect(onAlternative).toHaveBeenCalledTimes(1);
  });

  it("shows no menu and sends Shift+B to the only target when there is one", () => {
    const { onGoBack } = renderButton(false);

    expect(screen.queryByRole("button", { name: "Choose where to go back to" })).toBeNull();
    fireEvent.keyDown(document.body, { key: "B", shiftKey: true });
    expect(onGoBack).toHaveBeenCalledTimes(1);
  });
});
