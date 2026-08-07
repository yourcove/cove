import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { StartupConnectionScreen } from "../components/StartupConnectionScreen";

describe("StartupConnectionScreen", () => {
  it("explains the startup failure and lets the user retry", () => {
    const onRetry = vi.fn();
    render(<StartupConnectionScreen retrying={false} onRetry={onRetry} />);

    expect(screen.getByRole("heading", { name: "Can’t connect to the Cove server" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it("disables the retry action while another attempt is running", () => {
    render(<StartupConnectionScreen retrying onRetry={() => undefined} />);

    expect(screen.getByRole("button", { name: "Trying again…" })).toBeDisabled();
  });
});
