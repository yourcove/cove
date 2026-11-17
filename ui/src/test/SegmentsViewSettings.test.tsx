import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { SegmentDisplayProfile } from "../api/types";
import { SegmentsViewSettings } from "../pages/segments/SegmentsViewSettings";

const profiles = [
  { id: 2, name: "Default", isDefault: true },
  { id: 5, name: "Fine", isDefault: false },
] as SegmentDisplayProfile[];

describe("SegmentsViewSettings", () => {
  it("keeps the display profile behind the gear and switches it from there", () => {
    const onProfileChange = vi.fn();
    render(
      <SegmentsViewSettings
        profiles={profiles}
        activeProfileId={2}
        onProfileChange={onProfileChange}
        isRawView={false}
      />,
    );

    expect(screen.queryByLabelText("Display profile")).not.toBeInTheDocument();
    const gear = screen.getByRole("button", { name: "View settings" });
    expect(gear).toHaveAttribute("aria-expanded", "false");
    expect(gear).toHaveAttribute("title", "View settings");

    fireEvent.click(gear);
    expect(gear).toHaveAttribute("aria-expanded", "true");
    fireEvent.change(screen.getByLabelText("Display profile"), { target: { value: "5" } });
    expect(onProfileChange).toHaveBeenCalledWith(5);
  });

  it("says on the gear when a profile other than the default is in use", () => {
    render(
      <SegmentsViewSettings profiles={profiles} activeProfileId={5} onProfileChange={vi.fn()} isRawView={false} />,
    );

    expect(screen.getByRole("button", { name: "View settings" })).toHaveAttribute(
      "title",
      "View settings: Fine profile",
    );
  });

  it("explains that the raw view uses no profile", () => {
    render(<SegmentsViewSettings profiles={profiles} activeProfileId={5} onProfileChange={vi.fn()} isRawView />);

    const gear = screen.getByRole("button", { name: "View settings" });
    expect(gear).toHaveAttribute("title", "View settings");
    fireEvent.click(gear);
    expect(screen.getByLabelText("Display profile")).toBeDisabled();
    expect(screen.getByText(/profiles and derived queries do not apply/)).toBeInTheDocument();
  });
});
