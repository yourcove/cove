import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { InteractiveRating } from "../components/Rating";
import { RATING_OPTIONS_STORAGE_KEY } from "../utils/ratingPreferences";

function useRatingSystem(type: "stars" | "decimal") {
  window.localStorage.setItem(RATING_OPTIONS_STORAGE_KEY, JSON.stringify({ type, starPrecision: "full" }));
}

afterEach(() => window.localStorage.removeItem(RATING_OPTIONS_STORAGE_KEY));

describe("InteractiveRating disabled", () => {
  it("keeps the star buttons in place but ignores them, without the read-only label", () => {
    useRatingSystem("stars");
    const onChange = vi.fn();
    render(<InteractiveRating value={60} onChange={onChange} disabled />);

    const stars = screen.getAllByTitle("Set rating");
    expect(stars).toHaveLength(5);
    for (const star of stars) expect(star).toBeDisabled();
    fireEvent.click(stars[4]);
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.queryByText("Unrated")).not.toBeInTheDocument();
  });

  it("keeps the decimal field, read-only so typing focus stays, and ignores input", () => {
    useRatingSystem("decimal");
    const onChange = vi.fn();
    render(<InteractiveRating value={60} onChange={onChange} disabled />);

    const field = screen.getByRole("spinbutton");
    expect(field).toHaveAttribute("readonly");
    expect(field).not.toBeDisabled();
    // Leaving the field must not send its value again while a save is in flight.
    fireEvent.blur(field);
    expect(onChange).not.toHaveBeenCalled();
  });
});
