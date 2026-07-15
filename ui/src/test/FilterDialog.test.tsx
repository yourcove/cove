import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactElement } from "react";
import { describe, expect, it, vi } from "vitest";
import { FilterDialog, PERFORMER_CRITERIA, VIDEO_CRITERIA, TAG_CRITERIA } from "../components/FilterDialog";

const { performersFind } = vi.hoisted(() => ({ performersFind: vi.fn() }));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    performers: { ...actual.performers, find: performersFind },
  };
});

function renderWithQueryClient(ui: ReactElement, setup?: (client: QueryClient) => void) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  setup?.(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

describe("FilterDialog", () => {
  it("keeps performer suggestions visible while a search refreshes", async () => {
    const onApply = vi.fn();
    let resolveSearch: ((value: { items: { id: number; name: string }[] }) => void) | undefined;
    performersFind.mockImplementation(({ q }: { q?: string }) => {
      if (!q) return Promise.resolve({ items: [{ id: 1, name: "Existing performer" }] });
      return new Promise((resolve) => { resolveSearch = resolve; });
    });

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Performers"));
    expect(await screen.findByText("Existing performer")).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Search performers..."), { target: { value: "Eva" } });
    await waitFor(() => expect(performersFind).toHaveBeenCalledWith(expect.objectContaining({ q: "Eva" })));

    expect(screen.getByText("Existing performer")).toBeInTheDocument();
    expect(screen.getByTitle("Include")).toBeDisabled();
    expect(screen.getByTitle("Include").closest("div.max-h-32")).toHaveAttribute("aria-busy", "true");
    fireEvent.click(screen.getByTitle("Include"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenLastCalledWith({});

    resolveSearch?.({ items: [{ id: 2, name: "Matching performer" }] });
    expect(await screen.findByText("Matching performer")).toBeInTheDocument();
  });

  it("resyncs its local edit state when the active filter changes outside the dialog", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ titleCriterion: { value: "Cloud Nine", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.getAllByText("Title")).toHaveLength(2);

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    expect(screen.getAllByText("Title")).toHaveLength(1);
  });

  it("applies multi-select performer gender filters as a regex-backed criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Gender"));
    fireEvent.click(screen.getByLabelText("Male"));
    fireEvent.click(screen.getByLabelText("Female"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      genderCriterion: expect.objectContaining({
        modifier: "MATCHES_REGEX",
        value: "^(?:Male|Female)$",
        _selectedValues: ["Male", "Female"],
      }),
    }));
  });

  it("does not restore a removed criterion when the parent rerenders with the same active filter", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove Created At filter chip" }));
    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();
    expect(screen.getAllByText("Created At")).toHaveLength(1);
  });

  it("does not re-add an expanded timestamp criterion after removing it", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={vi.fn()}
      />
    );

    fireEvent.click(screen.getAllByText("Created At")[1]);
    fireEvent.click(screen.getByRole("button", { name: "Remove Created At filter row" }));

    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();
    expect(screen.getAllByText("Created At")).toHaveLength(1);
  });

  it("applies child-inclusive tag count toggles alongside the main criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={TAG_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Video Count"));
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "2" } });
    fireEvent.click(screen.getByLabelText("Count videos from child tags"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      videoCountCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 2,
      }),
      videoCountIncludesChildren: true,
    }));
  });

  it("renders the career length filter with a years/months unit selector", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Career Length"));

    const unitSelect = screen.getByLabelText("Career length unit") as HTMLSelectElement;
    expect(unitSelect.value).toBe("years");

    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });

  it("converts career length entered in months into whole years before applying", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Career Length"));
    fireEvent.change(screen.getByLabelText("Career length unit"), { target: { value: "months" } });
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });

  it("does not apply a tag duration filter until a threshold is entered", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [{ id: 1, name: "Action", tagGroupName: "Acts" }]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));

    expect(screen.queryByRole("button", { name: "Remove Tag Duration filter chip" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({});
  });

  it("uses time and percent controls for tag duration filters without context mode choices", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [{ id: 1, name: "Action", tagGroupName: "Acts" }]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    expect(screen.queryByRole("option", { name: "Any" })).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));
    fireEvent.change(screen.getByLabelText("Tag duration time"), { target: { value: "1:30" } });
    fireEvent.blur(screen.getByLabelText("Tag duration time"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenLastCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [expect.objectContaining({
          tagId: 1,
          unit: "seconds",
          value: 90,
        })],
      }),
    }));

    fireEvent.change(screen.getByLabelText("Tag duration unit"), { target: { value: "percent" } });
    fireEvent.change(screen.getByLabelText("Tag duration percent"), { target: { value: "25" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenLastCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [expect.objectContaining({
          tagId: 1,
          unit: "percent",
          value: 25,
        })],
      }),
    }));
  });

  it("allows multiple tag duration clauses in one filter", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [
          { id: 1, name: "Action", tagGroupName: "Acts" },
          { id: 2, name: "Mood", tagGroupName: "Qualities" },
        ]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));
    fireEvent.change(screen.getByLabelText("Tag duration time"), { target: { value: "0:30" } });
    fireEvent.blur(screen.getByLabelText("Tag duration time"));
    fireEvent.click(screen.getByRole("button", { name: "Add tag duration" }));

    const searchInputs = screen.getAllByPlaceholderText("Search tags");
    fireEvent.change(searchInputs[1], { target: { value: "Mood" } });
    fireEvent.click(screen.getByRole("option", { name: "Mood" }));
    fireEvent.change(screen.getAllByLabelText("Tag duration unit")[1], { target: { value: "percent" } });
    fireEvent.change(screen.getByLabelText("Tag duration percent"), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [
          expect.objectContaining({ tagId: 1, unit: "seconds", value: 30 }),
          expect.objectContaining({ tagId: 2, unit: "percent", value: 10 }),
        ],
      }),
    }));
  });
});
