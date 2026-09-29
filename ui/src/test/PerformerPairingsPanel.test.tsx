import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { PerformerPairings, PerformerPairingVideo } from "../api/types";
import { PerformerPairingsPanel } from "../components/pairings/PerformerPairingsPanel";
import { CareerStrip } from "../components/pairings/pairingParts";

const { mockPairings } = vi.hoisted(() => ({ mockPairings: vi.fn() }));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, performers: { ...actual.performers, pairings: mockPairings } };
});

function video(id: number, cast: number[], date: string): PerformerPairingVideo {
  return {
    id,
    title: `Shared video ${id}`,
    date,
    studioId: 7,
    studioName: "Meridian",
    duration: 1800,
    width: 1920,
    height: 1080,
    updatedAt: "2026-01-01T00:00:00.0000000Z",
    performerIds: [1, ...cast].sort((a, b) => a - b),
  };
}

// Partner shares six videos with the focal performer, one of them with Guest as well.
const pairings: PerformerPairings = {
  performerId: 1,
  videoCount: 9,
  soloVideoYears: { "2014": 2 },
  videos: [
    video(10, [2], "2016-11-04"),
    video(11, [2], "2016-02-21"),
    video(12, [2, 3], "2015-06-01"),
    video(13, [2], "2014-03-02"),
    video(14, [2], "2013-06-10"),
    video(15, [2], "2012-01-14"),
    video(16, [3], "2011-05-01"),
  ],
  coStars: [
    { id: 2, name: "Partner", gender: "Female", favorite: true, videoCount: 12 },
    { id: 3, name: "Guest", gender: "Male", favorite: false, videoCount: 4 },
  ],
};

function renderPanel() {
  const onNavigate = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <PerformerPairingsPanel performer={{ id: 1, name: "Focal" }} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );
  return { onNavigate, client };
}

// The co-stars on the current page, top to bottom, each by their row.
async function findRows() {
  return within(await screen.findByRole("list", { name: "Co-stars" })).getAllByRole("listitem");
}

function rowNames() {
  return within(screen.getByRole("list", { name: "Co-stars" }))
    .getAllByRole("listitem")
    .map((row) => within(row).getAllByRole("link")[1].textContent);
}

function filterFromHref(href: string | null) {
  const params = new URL(href ?? "", "http://localhost").searchParams;
  return { tab: params.get("tab"), filters: JSON.parse(params.get("filters") ?? "{}") };
}

describe("PerformerPairingsPanel", () => {
  beforeEach(() => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith");
    mockPairings.mockReset();
    mockPairings.mockResolvedValue(pairings);
  });

  it("ranks co-stars by videos together in one list and links each pairing to its shared videos", async () => {
    const { onNavigate } = renderPanel();

    const [frequent, recurring] = await findRows();
    expect(screen.queryByRole("heading")).not.toBeInTheDocument();
    expect(within(frequent).getByText("Partner")).toBeInTheDocument();
    expect(within(frequent).getByText("6")).toBeInTheDocument();
    expect(within(frequent).getByText("5 as a duo · 1 with others")).toBeInTheDocument();
    expect(within(frequent).getByText("6 of the 12 videos they appear in")).toBeInTheDocument();
    const viewAll = within(frequent).getByRole("link", { name: /View all 6 videos/ });
    expect(filterFromHref(viewAll.getAttribute("href"))).toEqual({
      tab: "videos",
      filters: { performersCriterion: { value: [2], modifier: "INCLUDES" } },
    });

    fireEvent.click(viewAll);

    expect(onNavigate).toHaveBeenCalledWith({
      page: "performer",
      id: 1,
      detailTab: "videos",
      listFilter: { q: "", page: 1 },
      listObjectFilter: { performersCriterion: { value: [2], modifier: "INCLUDES" } },
    });
    expect(within(recurring).getByText("Guest")).toBeInTheDocument();
    expect(within(recurring).getByRole("link", { name: /View all 2 videos/ })).toBeInTheDocument();
  });

  it("filters to one tier from the toolbar, with a count on each tier", async () => {
    renderPanel();
    await screen.findByRole("list", { name: "Co-stars" });
    expect(screen.getByRole("button", { name: "All 2" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Frequent 1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "One-time 0" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Recurring 1" }));

    expect(rowNames()).toEqual(["Guest"]);
    expect(screen.getByRole("button", { name: "Everyone 1" })).toBeInTheDocument();
    expect(window.location.search).toContain("awTier=recurring");
  });

  it("recounts with duos only and links a single video straight to it", async () => {
    renderPanel();
    await screen.findByRole("list", { name: "Co-stars" });

    fireEvent.click(screen.getByRole("button", { name: "Duos only" }));

    const [frequent, once] = await findRows();
    const viewAll = within(frequent).getByRole("link", { name: /View all 5 videos/ });
    expect(filterFromHref(viewAll.getAttribute("href")).filters).toEqual({
      performersCriterion: { value: [2], modifier: "INCLUDES" },
      performerCountCriterion: { value: 2, modifier: "EQUALS" },
    });
    expect(within(once).getByText("Guest")).toBeInTheDocument();
    expect(within(once).getByRole("link", { name: /Open video/ })).toHaveAttribute("href", "/video/16");
    expect(window.location.search).toContain("awDuo=true");
  });

  it("builds a lineup from ticked co-stars with the videos they are all in", async () => {
    renderPanel();
    await screen.findByRole("list", { name: "Co-stars" });

    fireEvent.click(screen.getByRole("checkbox", { name: "Add Partner to the lineup" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Add Guest to the lineup" }));

    const lineup = screen.getByRole("region", { name: "Lineup" });
    expect(lineup).toHaveTextContent("Focal + Partner + Guest");
    expect(new URLSearchParams(window.location.search).get("awLineup")).toBe("2,3");
    expect(lineup).toHaveTextContent("All of them: 1 video");
    expect(lineup).toHaveTextContent("Any of them: 7 videos");
    const [openAll, openAny] = within(lineup).getAllByRole("link", { name: "Open" });
    expect(filterFromHref(openAll.getAttribute("href")).filters).toEqual({
      performersCriterion: { value: [2, 3], modifier: "INCLUDES_ALL" },
    });
    expect(filterFromHref(openAny.getAttribute("href")).filters).toEqual({
      performersCriterion: { value: [2, 3], modifier: "INCLUDES" },
    });

    fireEvent.click(screen.getByRole("button", { name: "Duos only" }));

    expect(lineup).toHaveTextContent("All of them: 0 videos");
    expect(lineup).toHaveTextContent("Duos only is on, so no video has all of them");
    expect(lineup).toHaveTextContent("Any of them: 6 videos");

    fireEvent.click(within(lineup).getByRole("button", { name: "Clear the lineup" }));

    expect(screen.queryByRole("region", { name: "Lineup" })).not.toBeInTheDocument();
  });

  it("restores a lineup and a search from the URL", async () => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith&awLineup=3&awQ=gue");
    renderPanel();

    const lineup = await screen.findByRole("region", { name: "Lineup" });
    expect(lineup).toHaveTextContent("Focal + Guest");
    expect(screen.getByRole("searchbox", { name: "Search co-stars" })).toHaveValue("gue");
    expect(rowNames()).toEqual(["Guest"]);
  });

  it("keeps a loaded list when a later refetch fails", async () => {
    const { client } = renderPanel();
    await screen.findByRole("list", { name: "Co-stars" });
    mockPairings.mockRejectedValue(new Error("API Error 502: upstream"));

    await client.invalidateQueries({ queryKey: ["performer-appears-with"] });

    expect(screen.getByRole("list", { name: "Co-stars" })).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows no counts on the filters before the co-stars have loaded", () => {
    mockPairings.mockReturnValue(new Promise(() => {}));

    renderPanel();

    expect(screen.getByRole("button", { name: "All" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Frequent" })).toBeInTheDocument();
    expect(screen.getByText("Loading co-stars…")).toBeInTheDocument();
  });

  it("plays a pairing's videos from the newest", async () => {
    const { onNavigate } = renderPanel();
    const [frequent] = await findRows();

    fireEvent.click(within(frequent).getByRole("button", { name: "Play all" }));

    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 10 });
  });

  it("keeps a chosen gender's chip when no co-star of that gender is left", async () => {
    renderPanel();
    await screen.findByRole("list", { name: "Co-stars" });

    fireEvent.click(screen.getByRole("button", { name: "Male 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Favorites" }));

    expect(screen.getByRole("button", { name: "Male 0" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByText("No co-stars match these filters.")).toBeInTheDocument();
  });
});

describe("PerformerPairingsPanel paging", () => {
  // Partner shares 25 videos, Guest 2, and co-stars 101 to 125 one video each, newest numbers latest.
  const manyPairings: PerformerPairings = {
    performerId: 1,
    videoCount: 52,
    soloVideoYears: {},
    videos: [
      ...Array.from({ length: 25 }, (_, index) => video(200 + index, [2], "2015-01-01")),
      video(300, [3], "2014-01-01"),
      video(301, [3], "2014-01-02"),
      ...Array.from({ length: 25 }, (_, index) =>
        video(400 + index, [101 + index], `2016-01-${String(index + 1).padStart(2, "0")}`),
      ),
    ],
    coStars: [
      {
        id: 2,
        name: "Partner",
        gender: "Female",
        favorite: true,
        videoCount: 25,
      },
      { id: 3, name: "Guest", gender: "Male", favorite: false, videoCount: 2 },
      ...Array.from({ length: 25 }, (_, index) => ({
        id: 101 + index,
        name: `Extra ${index + 1}`,
        gender: "Female",
        favorite: false,
      })),
    ],
  };
  // Most videos together first, then the latest video together.
  const ranked = ["Partner", "Guest", ...Array.from({ length: 25 }, (_, index) => `Extra ${25 - index}`)];

  beforeEach(() => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith");
    mockPairings.mockReset();
    mockPairings.mockResolvedValue(manyPairings);
  });

  it("pages through every tier in one ranked list", async () => {
    renderPanel();
    await findRows();

    expect(rowNames()).toEqual(ranked.slice(0, 20));
    const pages = screen.getByRole("navigation", { name: "Co-star pages" });
    expect(within(pages).getByRole("button", { name: "Page 1" })).toHaveAttribute("aria-current", "page");

    fireEvent.click(within(pages).getByRole("button", { name: "Page 2" }));

    expect(rowNames()).toEqual(ranked.slice(20));
    expect(new URLSearchParams(window.location.search).get("awPage")).toBe("2");
  });

  it("goes back to the first page when a filter or the page size changes", async () => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith&awPage=2");
    renderPanel();
    await findRows();
    expect(rowNames()).toEqual(ranked.slice(20));

    fireEvent.click(screen.getByRole("button", { name: "Female 26" }));

    expect(rowNames()).toEqual(["Partner", ...ranked.slice(2, 21)]);
    expect(window.location.search).not.toContain("awPage");

    fireEvent.click(
      within(screen.getByRole("navigation", { name: "Co-star pages" })).getByRole("button", { name: "Page 2" }),
    );
    expect(new URLSearchParams(window.location.search).get("awPage")).toBe("2");
    fireEvent.change(screen.getByTitle("Items per page"), {
      target: { value: "40" },
    });

    expect(rowNames()).toHaveLength(26);
    expect(screen.queryByRole("navigation", { name: "Co-star pages" })).not.toBeInTheDocument();
    const params = new URLSearchParams(window.location.search);
    expect(params.get("awPerPage")).toBe("40");
    expect(params.get("awPage")).toBeNull();
  });

  it("shows the last page for a page past the end, and corrects the URL", async () => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith&awPage=9");
    renderPanel();
    await findRows();

    expect(rowNames()).toEqual(ranked.slice(20));
    const pages = screen.getByRole("navigation", { name: "Co-star pages" });
    expect(within(pages).getByRole("button", { name: "Page 2" })).toHaveAttribute("aria-current", "page");
    expect(new URLSearchParams(window.location.search).get("awPage")).toBe("2");
    expect(screen.getByRole("list", { name: "Co-stars" })).toHaveAttribute("start", "21");
  });

  it("goes back to the first page for a new sort or search, and clears filters but not the page size", async () => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith&awPage=2&awPerPage=10&awFav=true");
    renderPanel();
    await findRows();

    fireEvent.click(screen.getByRole("button", { name: "Favorites" }));
    fireEvent.click(
      within(screen.getByRole("navigation", { name: "Co-star pages" })).getByRole("button", { name: "Page 2" }),
    );
    fireEvent.change(screen.getByRole("combobox", { name: "Sort" }), { target: { value: "name" } });
    expect(new URLSearchParams(window.location.search).get("awPage")).toBeNull();

    fireEvent.click(
      within(screen.getByRole("navigation", { name: "Co-star pages" })).getByRole("button", { name: "Page 2" }),
    );
    fireEvent.change(screen.getByRole("searchbox", { name: "Search co-stars" }), { target: { value: "extra" } });
    await waitFor(() => expect(new URLSearchParams(window.location.search).get("awQ")).toBe("extra"));
    expect(new URLSearchParams(window.location.search).get("awPage")).toBeNull();

    fireEvent.change(screen.getByRole("searchbox", { name: "Search co-stars" }), { target: { value: "nobody" } });
    fireEvent.click(await screen.findByRole("button", { name: "Clear filters" }));

    const params = new URLSearchParams(window.location.search);
    expect(params.get("awQ")).toBeNull();
    expect(params.get("awPerPage")).toBe("10");
  });

  it("keeps the page when a chip already chosen is picked again or a co-star joins the lineup", async () => {
    window.history.replaceState(null, "", "/performer/1?tab=appearsWith&awPage=2");
    renderPanel();
    await findRows();

    fireEvent.click(screen.getByRole("button", { name: "All 27" }));
    fireEvent.click(screen.getByRole("button", { name: "Everyone 27" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Add Extra 1 to the lineup" }));

    const params = new URLSearchParams(window.location.search);
    expect(params.get("awPage")).toBe("2");
    expect(params.get("awLineup")).toBe("101");
    expect(rowNames()).toEqual(ranked.slice(20));
  });

  it("pages from the bottom pager too", async () => {
    renderPanel();
    await findRows();

    fireEvent.click(
      within(screen.getByRole("navigation", { name: "Co-star pages, bottom" })).getByRole("button", {
        name: "Next page",
      }),
    );

    expect(rowNames()).toEqual(ranked.slice(20));
  });
});

describe("CareerStrip", () => {
  it("groups years so a long career keeps a bounded number of bars", () => {
    const { container } = render(
      <CareerStrip
        yearCounts={
          new Map([
            [1990, 2],
            [2020, 1],
          ])
        }
        firstYear={1921}
        lastYear={2026}
      />,
    );

    const bars = container.querySelectorAll("[aria-hidden='true'] > span");
    expect(bars.length).toBeLessThanOrEqual(40);
    expect([...bars].some((bar) => bar.getAttribute("title")?.includes("1990"))).toBe(true);
  });
});
