import { QueryClientProvider, useMutation } from "@tanstack/react-query";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { MutationFailureNotice } from "../components/MutationFailureNotice";
import { createAppQueryClient } from "../queryClient";
import { resetMutationFailureForTests } from "../state/mutationFailure";
import { reportServerResponse, resetServerAvailabilityForTests } from "../state/serverAvailability";

function FailingAction({ rollsBack = false, suppressNotice = false, message = "API Error 502: simulated gateway failure" }: { rollsBack?: boolean; suppressNotice?: boolean; message?: string }) {
  const mutation = useMutation({
    mutationFn: async () => {
      throw new Error(message);
    },
    onError: rollsBack ? () => undefined : undefined,
    meta: suppressNotice ? { suppressGlobalError: true } : undefined,
  });

  return <button onClick={() => mutation.mutate()}>{mutation.isError ? "Rating failed" : "Save rating"}</button>;
}

function renderAction(options: { rollsBack?: boolean; suppressNotice?: boolean; message?: string } = {}) {
  const queryClient = createAppQueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <MutationFailureNotice />
      <FailingAction {...options} />
    </QueryClientProvider>,
  );
}

describe("MutationFailureNotice", () => {
  afterEach(() => {
    resetMutationFailureForTests();
    resetServerAvailabilityForTests();
  });

  it("reports an unhandled action failure and lets the user dismiss it", async () => {
    renderAction();

    fireEvent.click(screen.getByRole("button", { name: "Save rating" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Couldn’t complete the action");
    expect(alert).toHaveTextContent("The server returned an error. Please try again.");
    expect(alert).not.toHaveTextContent("502");
    expect(alert).toHaveClass("z-[20000]");

    fireEvent.click(screen.getByRole("button", { name: "Dismiss action error" }));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("still reports a failure when onError only performs rollback", async () => {
    renderAction({ rollsBack: true });

    fireEvent.click(screen.getByRole("button", { name: "Save rating" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn’t complete the action");
  });

  it("allows state-driven error UI to suppress the global notice explicitly", async () => {
    renderAction({ suppressNotice: true });

    fireEvent.click(screen.getByRole("button", { name: "Save rating" }));

    await screen.findByRole("button", { name: "Rating failed" });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("explains a confirmed outage without exposing an HTTP status", async () => {
    renderAction();

    fireEvent.click(screen.getByRole("button", { name: "Save rating" }));
    const alert = await screen.findByRole("alert");
    act(() => reportServerResponse(new Response(null, { status: 502 })));
    expect(alert).toHaveTextContent("Cove can’t reach the server right now.");
    expect(alert).not.toHaveTextContent("502");
  });

  it("shows a validation conflict's server-provided guidance", async () => {
    const userMessage = "That performer identity already exists. Use a different disambiguation.";
    renderAction({ message: `API Error 409: ${JSON.stringify({ code: "PERFORMER_NAME_CONFLICT", message: userMessage })}` });

    fireEvent.click(screen.getByRole("button", { name: "Save rating" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(userMessage);
  });
});
