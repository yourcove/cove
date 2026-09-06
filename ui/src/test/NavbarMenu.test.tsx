import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Navbar } from "../components/Navbar";

vi.mock("../components/JobDrawer", () => ({
  JobDrawer: () => null,
  useJobCount: () => 0,
}));

vi.mock("../components/GlobalSearch", () => ({
  GlobalSearch: () => null,
}));

vi.mock("../router/RouteRegistry", () => ({
  useRouteRegistry: () => ({ routes: [] }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: undefined }),
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({ manifest: null }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    authEnabled: false,
    user: null,
    hasPermission: () => true,
  }),
}));

describe("Navbar mobile menu", () => {
  it("closes without consuming a click outside the navigation", async () => {
    const handleOutsideClick = vi.fn();
    const user = userEvent.setup();
    render(
      <>
        <Navbar currentPage="images" navigate={vi.fn()} />
        <button onClick={handleOutsideClick}>Open image</button>
      </>,
    );

    const toggle = screen.getByRole("button", { name: "Toggle navigation menu" });
    fireEvent.click(toggle);

    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: "Videos" })).toBeInTheDocument();

    const outsideButton = screen.getByRole("button", { name: "Open image" });
    fireEvent.pointerDown(outsideButton);

    expect(toggle).toHaveAttribute("aria-expanded", "true");

    await user.click(outsideButton);

    expect(handleOutsideClick).toHaveBeenCalledOnce();
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("button", { name: "Videos" })).not.toBeInTheDocument();
  });

  it("closes when the outside target stops click propagation", async () => {
    const handleOutsideClick = vi.fn();
    const user = userEvent.setup();
    render(
      <>
        <Navbar currentPage="images" navigate={vi.fn()} />
        <button
          onClick={(event) => {
            event.stopPropagation();
            handleOutsideClick();
          }}
        >
          Select image
        </button>
      </>,
    );

    const toggle = screen.getByRole("button", { name: "Toggle navigation menu" });
    fireEvent.click(toggle);
    await user.click(screen.getByRole("button", { name: "Select image" }));

    expect(handleOutsideClick).toHaveBeenCalledOnce();
    expect(toggle).toHaveAttribute("aria-expanded", "false");
  });
});
