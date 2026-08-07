import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { ListLoadError } from "../components/ListLoadError";
import { reportServerResponse, resetServerAvailabilityForTests } from "../state/serverAvailability";

describe("ListLoadError", () => {
  afterEach(() => resetServerAvailabilityForTests());

  it("explains a confirmed outage without exposing the raw API error", () => {
    render(<ListLoadError error={new Error("API Error 502:")} title="Could not load video" />);
    act(() => reportServerResponse(new Response(null, { status: 502 })));

    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("Could not load video");
    expect(alert).toHaveTextContent("Cove can’t reach the server right now.");
    expect(alert).not.toHaveTextContent("API Error");
  });

  it("uses a useful fallback for an API failure while the server is reachable", () => {
    render(<ListLoadError error={new Error("API Error 500: internal implementation detail")} />);

    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("The server returned an error. Please try again.");
    expect(alert).not.toHaveTextContent("internal implementation detail");
  });
});
