import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactElement } from "react";
import { describe, expect, it, vi } from "vitest";
import { FilterDialog, RemoteIdFilterEditor, PERFORMER_CRITERIA, VIDEO_CRITERIA, TAG_CRITERIA, STUDIO_CRITERIA } from "../components/FilterDialog";
import type { CriterionModifier } from "../api/types";

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
  const metadataServiceModifiers: CriterionModifier[] = [
    "EQUALS",
    "NOT_EQUALS",
    "INCLUDES",
    "EXCLUDES",
    "MATCHES_REGEX",
    "NOT_MATCHES_REGEX",
    "IS_NULL",
    "NOT_NULL",
  ];

  it("renders configured metadata service names with endpoint fallback labels", () => {
    render(
      <RemoteIdFilterEditor
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[
          { endpoint: "https://named.example/graphql", name: "Named Service", apiKey: "", maxRequestsPerMinute: 0 },
          { endpoint: "https://fallback.example/graphql", name: "", apiKey: "", maxRequestsPerMinute: 0 },
        ]}
      />,
    );

    expect(screen.getByRole("option", { name: "Named Service" })).toHaveValue("https://named.example/graphql");
    expect(screen.getByRole("option", { name: "https://fallback.example/graphql" })).toHaveValue("https://fallback.example/graphql");
  });

  it("shows an explicit no-services state", () => {
    render(<RemoteIdFilterEditor onChange={vi.fn()} modifiers={metadataServiceModifiers} metadataServers={[]} />);

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toBeEnabled();
    expect(screen.getByRole("option", { name: "Any metadata service" })).toBeInTheDocument();
  });

  it("keeps a legacy unconfigured endpoint selected", () => {
    render(
      <RemoteIdFilterEditor
        value={{ value: "", endpoint: "https://legacy.example/graphql", modifier: "NOT_NULL" }}
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[]}
      />,
    );

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toHaveValue("https://legacy.example/graphql");
    expect(screen.getByRole("option", { name: "https://legacy.example/graphql (unconfigured)" })).toBeInTheDocument();
  });

  it("uses the configured label for a saved endpoint with different casing", () => {
    render(
      <RemoteIdFilterEditor
        value={{ value: "remote-123", endpoint: "HTTPS://SERVICE.EXAMPLE/GRAPHQL", modifier: "EQUALS" }}
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[
          { endpoint: "https://service.example/graphql", name: "Configured Service", apiKey: "", maxRequestsPerMinute: 0 },
        ]}
      />,
    );

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toHaveValue("HTTPS://SERVICE.EXAMPLE/GRAPHQL");
    expect(screen.getByRole("option", { name: "Configured Service" })).toHaveValue("HTTPS://SERVICE.EXAMPLE/GRAPHQL");
    expect(screen.queryByText(/unconfigured/i)).not.toBeInTheDocument();
  });

  it("emits a paired endpoint and value payload", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Remote ID"));
    fireEvent.change(screen.getByLabelText("Remote ID value"), { target: { value: "remote-123" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      remoteIdValueCriterion: { value: "remote-123", modifier: "EQUALS" },
    });
  });

  it("applies the video segment-presence criterion", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Has Segments"));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      hasSegmentsCriterion: { value: true },
    });
  });

  it.each(metadataServiceModifiers)("preserves selected metadata services for %s", (modifier) => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ remoteIdCriterion: { value: "https://service.example/graphql", modifier } }}
        onApply={onApply}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      remoteIdCriterion: { value: "https://service.example/graphql", modifier },
    });
  });

  it.each([[VIDEO_CRITERIA], [PERFORMER_CRITERIA], [STUDIO_CRITERIA], [TAG_CRITERIA]])(
    "preserves legacy blank-value null filters",
    (criteria) => {
      const onApply = vi.fn();
      render(
        <FilterDialog
          open
          onClose={vi.fn()}
          criteria={criteria}
          activeFilter={{ remoteIdCriterion: { value: "   ", modifier: "IS_NULL" } }}
          onApply={onApply}
        />,
      );

      fireEvent.click(screen.getByRole("button", { name: "Apply" }));
      expect(onApply).toHaveBeenCalledWith({
        remoteIdCriterion: { value: "   ", modifier: "IS_NULL" },
      });
    },
  );

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
