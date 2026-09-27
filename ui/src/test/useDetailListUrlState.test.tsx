import { render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  DetailListStateCacheProvider,
  useDetailListUrlState,
  useDetailTabUrlState,
  useRememberDetailTabUrlParams,
} from "../hooks/useDetailListUrlState";
import { registerNavigationBlocker } from "../router/location";

function DetailListProbe({ stateKey = "videos" }: { stateKey?: string }) {
  const [, forceRender] = useState(0);
  const { filter, setFilter, displayMode } = useDetailListUrlState({
    stateKey,
    resetKey: `performer-${stateKey}`,
    builtInFilter: { page: 1, perPage: 24, sort: "date", direction: "desc" },
    defaultFilterKey: stateKey,
    defaultDisplayMode: "grid" as const,
    allowedDisplayModes: ["grid", "list"] as const,
    allowInfinitePageSize: true,
  });

  return (
    <div>
      <div data-testid="sort">{filter.sort}</div>
      <div data-testid="page">{filter.page}</div>
      <div data-testid="seed">{filter.seed}</div>
      <div data-testid="display-mode">{displayMode}</div>
      <button type="button" onClick={() => setFilter({ ...filter, seed: 999 })}>
        Change seed
      </button>
      <button type="button" onClick={() => setFilter({ ...filter, sort: "random", seed: 999 })}>
        Use random
      </button>
      <button type="button" onClick={() => forceRender((value) => value + 1)}>
        Rerender
      </button>
    </div>
  );
}

const TAB_URL_KEYS = ["awTier", "awDuo"];

function TabProbe({
  defaultTab = "videos",
  tabUrlKeys,
}: {
  defaultTab?: "videos" | "galleries";
  tabUrlKeys?: readonly string[];
}) {
  const { activeTab, setActiveTab } = useDetailTabUrlState<"videos" | "galleries">(defaultTab, tabUrlKeys);
  return (
    <div>
      <div data-testid="tab">{activeTab}</div>
      <button type="button" onClick={() => setActiveTab("galleries")}>
        Galleries
      </button>
      <button type="button" onClick={() => setActiveTab("videos")}>
        Videos
      </button>
    </div>
  );
}

describe("detail list URL state", () => {
  beforeEach(() => {
    localStorage.clear();
    window.history.replaceState(null, "", "/performer/477");
  });

  it("resolves the saved default before the first render and mints its random seed", async () => {
    localStorage.setItem(
      "cove-default-filter-videos",
      JSON.stringify({
        findFilter: { page: 3, perPage: 40, sort: "random", direction: "asc" },
        objectFilter: { favorite: true },
        uiOptions: { displayMode: "list" },
      }),
    );
    vi.spyOn(Math, "random").mockReturnValue(0.5);

    render(
      <DetailListStateCacheProvider>
        <DetailListProbe />
      </DetailListStateCacheProvider>,
    );

    expect(screen.getByTestId("sort")).toHaveTextContent("random");
    expect(screen.getByTestId("page")).toHaveTextContent("1");
    expect(screen.getByTestId("seed")).toHaveTextContent("1073741823");
    expect(screen.getByTestId("display-mode")).toHaveTextContent("list");
    await waitFor(() => {
      expect(window.location.search).toContain("seed=1073741823");
    });
  });

  it("gives explicit URL state precedence over the saved default", () => {
    localStorage.setItem(
      "cove-default-filter-videos",
      JSON.stringify({
        findFilter: { sort: "random", direction: "asc" },
      }),
    );
    window.history.replaceState(null, "", "/performer/477?sort=random&direction=desc&seed=2468");

    render(
      <DetailListStateCacheProvider>
        <DetailListProbe />
      </DetailListStateCacheProvider>,
    );

    expect(screen.getByTestId("sort")).toHaveTextContent("random");
    expect(screen.getByTestId("seed")).toHaveTextContent("2468");
  });

  it("keeps changed list state explicit in the URL after later renders", async () => {
    const user = userEvent.setup();
    render(
      <DetailListStateCacheProvider>
        <DetailListProbe />
      </DetailListStateCacheProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Use random" }));
    await waitFor(() => expect(window.location.search).toContain("sort=random"));

    await user.click(screen.getByRole("button", { name: "Rerender" }));
    await waitFor(() => {
      expect(window.location.search).toContain("sort=random");
      expect(window.location.search).toContain("seed=999");
    });
  });

  it("stores the non-default tab in the URL and removes the previous tab's list state", async () => {
    const user = userEvent.setup();
    window.history.replaceState(
      null,
      "",
      "/performer/477?sort=random&seed=2468&page=3&filters=%7B%22favorite%22%3Atrue%7D",
    );
    render(<TabProbe />);

    await user.click(screen.getByRole("button", { name: "Galleries" }));

    await waitFor(() => {
      expect(screen.getByTestId("tab")).toHaveTextContent("galleries");
      expect(window.location.search).toBe("?tab=galleries");
    });

    await user.click(screen.getByRole("button", { name: "Videos" }));
    await waitFor(() => expect(window.location.search).toBe(""));
  });

  it("drops a tab's own URL keys when switching tabs, including to the default tab", async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, "", "/performer/477?tab=galleries&awTier=frequent&awDuo=true&keep=1");
    render(<TabProbe tabUrlKeys={TAB_URL_KEYS} />);

    await user.click(screen.getByRole("button", { name: "Videos" }));

    await waitFor(() => {
      expect(screen.getByTestId("tab")).toHaveTextContent("videos");
      expect(window.location.search).toBe("?keep=1");
    });
  });

  it("brings a tab's own URL keys back when the tab is picked again", async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, "", "/performer/477?tab=galleries&awTier=frequent&keep=1");
    render(
      <DetailListStateCacheProvider>
        <TabProbe tabUrlKeys={TAB_URL_KEYS} />
      </DetailListStateCacheProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Videos" }));
    await waitFor(() => expect(window.location.search).toBe("?keep=1"));

    await user.click(screen.getByRole("button", { name: "Galleries" }));

    await waitFor(() => {
      expect(screen.getByTestId("tab")).toHaveTextContent("galleries");
      expect(new URLSearchParams(window.location.search).get("awTier")).toBe("frequent");
      expect(new URLSearchParams(window.location.search).get("tab")).toBe("galleries");
    });
  });

  it("brings back what a tab remembered when it was left some other way", async () => {
    const user = userEvent.setup();
    function Rememberer() {
      useRememberDetailTabUrlParams("galleries", "awTier=once&awDuo=true");
      return null;
    }
    const { rerender } = render(
      <DetailListStateCacheProvider>
        <Rememberer />
        <TabProbe tabUrlKeys={TAB_URL_KEYS} />
      </DetailListStateCacheProvider>,
    );
    rerender(
      <DetailListStateCacheProvider>
        <TabProbe tabUrlKeys={TAB_URL_KEYS} />
      </DetailListStateCacheProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Galleries" }));

    await waitFor(() => expect(window.location.search).toBe("?awTier=once&awDuo=true&tab=galleries"));
  });

  it("stays on the tab when a navigation blocker refuses the switch", async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, "", "/performer/477?tab=galleries&awTier=frequent");
    const unregister = registerNavigationBlocker(() => false);
    try {
      render(<TabProbe tabUrlKeys={TAB_URL_KEYS} />);

      await user.click(screen.getByRole("button", { name: "Videos" }));

      expect(screen.getByTestId("tab")).toHaveTextContent("galleries");
      expect(window.location.search).toBe("?tab=galleries&awTier=frequent");
    } finally {
      unregister();
    }
  });

  it("keeps the open tab's own URL keys when that tab is picked again", async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, "", "/performer/477?tab=galleries&awTier=frequent");
    function StaleRememberer() {
      useRememberDetailTabUrlParams("galleries", "awTier=once");
      return null;
    }
    const { rerender } = render(
      <DetailListStateCacheProvider>
        <StaleRememberer />
        <TabProbe tabUrlKeys={TAB_URL_KEYS} />
      </DetailListStateCacheProvider>,
    );
    rerender(
      <DetailListStateCacheProvider>
        <TabProbe tabUrlKeys={TAB_URL_KEYS} />
      </DetailListStateCacheProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Galleries" }));

    expect(screen.getByTestId("tab")).toHaveTextContent("galleries");
    expect(window.location.search).toBe("?tab=galleries&awTier=frequent");
  });

  it("adopts a changed default when the URL does not select a tab", async () => {
    const { rerender } = render(<TabProbe />);

    expect(screen.getByTestId("tab")).toHaveTextContent("videos");

    rerender(<TabProbe defaultTab="galleries" />);

    await waitFor(() => expect(screen.getByTestId("tab")).toHaveTextContent("galleries"));
  });

  it("keeps an explicitly selected URL tab when the default changes", async () => {
    window.history.replaceState(null, "", "/performer/477?tab=videos");
    const { rerender } = render(<TabProbe />);

    rerender(<TabProbe defaultTab="galleries" />);

    await waitFor(() => expect(screen.getByTestId("tab")).toHaveTextContent("videos"));
    expect(window.location.search).toBe("?tab=videos");
  });

  it("keeps a selected tab that matches the default when the default later changes", async () => {
    // Selecting the current default removes the "tab" parameter, so the URL looks untouched. The app
    // config resolving afterwards moves which tab ranks first, and must not reopen a different one.
    const user = userEvent.setup();
    const { rerender } = render(<TabProbe />);

    await user.click(screen.getByRole("button", { name: "Videos" }));
    expect(screen.getByTestId("tab")).toHaveTextContent("videos");
    expect(window.location.search).toBe("");

    rerender(<TabProbe defaultTab="galleries" />);

    await waitFor(() => expect(screen.getByTestId("tab")).toHaveTextContent("videos"));
  });

  it("still follows the URL after a tab has been selected", async () => {
    // Remembering the viewer's choice must not make back and forward navigation inert.
    const user = userEvent.setup();
    render(<TabProbe />);

    await user.click(screen.getByRole("button", { name: "Galleries" }));
    expect(screen.getByTestId("tab")).toHaveTextContent("galleries");

    window.history.replaceState(null, "", "/performer/477?tab=videos");
    window.dispatchEvent(new Event("popstate"));

    await waitFor(() => expect(screen.getByTestId("tab")).toHaveTextContent("videos"));
  });

  it("restores a visited tab's state from the page cache", async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <DetailListStateCacheProvider>
        <DetailListProbe key="videos" stateKey="videos" />
      </DetailListStateCacheProvider>,
    );
    await user.click(screen.getByRole("button", { name: "Use random" }));
    await waitFor(() => {
      expect(window.location.search).toContain("sort=random");
      expect(window.location.search).toContain("seed=999");
    });

    window.history.replaceState(null, "", "/performer/477?tab=galleries");
    rerender(
      <DetailListStateCacheProvider>
        <DetailListProbe key="galleries" stateKey="galleries" />
      </DetailListStateCacheProvider>,
    );
    window.history.replaceState(null, "", "/performer/477");
    rerender(
      <DetailListStateCacheProvider>
        <DetailListProbe key="videos" stateKey="videos" />
      </DetailListStateCacheProvider>,
    );

    expect(screen.getByTestId("sort")).toHaveTextContent("random");
    expect(screen.getByTestId("seed")).toHaveTextContent("999");
    await waitFor(() => {
      expect(window.location.search).toContain("sort=random");
      expect(window.location.search).toContain("seed=999");
    });
  });
});
