import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { VideoCoverImage } from "../components/VideoCoverImage";

describe("VideoCoverImage", () => {
  it("retries a previously failed URL after the source changes", () => {
    const view = render(<VideoCoverImage src="/cover-a.jpg" alt="Cover" fallbackClassName="fallback" />);

    fireEvent.error(view.getByRole("img", { name: "Cover" }));
    expect(view.container.querySelector(".fallback")).toBeVisible();

    view.rerender(<VideoCoverImage src="/cover-b.jpg" alt="Cover" fallbackClassName="fallback" />);
    expect(view.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "/cover-b.jpg");

    view.rerender(<VideoCoverImage src="/cover-a.jpg" alt="Cover" fallbackClassName="fallback" />);
    expect(view.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "/cover-a.jpg");
  });
});
