import { expect, test, type Page } from "@playwright/test";
import { blockEngagementWrites } from "./capture-helpers";
import {
  captureAnnotatedManualScreenshot,
  openManualCapturePage,
} from "./manual-capture-helpers";
import { arrangeManualVideoFixtures } from "./manual-video-fixtures";

const featuredVideoTitle = "A Favor Between Professionals";

async function preventAutoplay(page: Page) {
  await page.addInitScript(() => {
    HTMLMediaElement.prototype.play = function () {
      this.pause();
      return Promise.resolve();
    };
  });
}

async function pinVideo(
  page: Page,
  seconds: number,
  video = page.locator("video").last(),
) {
  await expect(video).toBeVisible();
  await video.evaluate(async (element: HTMLVideoElement, time) => {
    if (element.readyState < 1)
      await new Promise<void>((resolve) =>
        element.addEventListener("loadedmetadata", () => resolve(), {
          once: true,
        }),
      );
    element.pause();
    const sought = new Promise<void>((resolve) =>
      element.addEventListener("seeked", () => resolve(), { once: true }),
    );
    element.currentTime = Math.min(time, Math.max(0, element.duration - 0.1));
    await sought;
    element.pause();
  }, seconds);
  await expect
    .poll(() =>
      video.evaluate((element: HTMLVideoElement) => ({
        paused: element.paused,
        second: Math.floor(element.currentTime),
      })),
    )
    .toEqual({ paused: true, second: seconds });
  await expect(page.getByText(new RegExp(`^0:0${seconds} /`))).toBeVisible();
}

async function arrangeManualFixtures(page: Page) {
  return arrangeManualVideoFixtures(page);
}

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

async function openFeaturedVideo(page: Page) {
  await preventAutoplay(page);
  await openManualCapturePage(
    page,
    "/video/25",
    page.getByRole("heading", { name: featuredVideoTitle, level: 3 }),
  );
}

async function openFeaturedVideoWithFixtures(page: Page) {
  await openFeaturedVideo(page);
  await arrangeManualFixtures(page);
  await openFeaturedVideo(page);
  await expect(page.getByLabel("Page Visits")).toBeHidden();
}

async function showRawSegments(page: Page) {
  await page.getByRole("tab", { name: "Segments (2)" }).click();
  const profile = page.getByRole("combobox", { name: "Profile" });
  await profile.selectOption({ label: "Raw" });
  await expect(profile.locator("option:checked")).toHaveText("Raw");
  await expect(
    page.getByRole("button", { name: /The handoff 1×/ }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: /The briefing 1×/ }),
  ).toBeVisible();
  await expect(page.getByText("Timeline overlays")).toBeVisible();
}

test("captures a scraper result without creating a backend scrape attempt", async ({
  page,
}) => {
  await page.route(/\/api\/system\/scrapers(?:\?.*)?$/, async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify([
        {
          id: "documentation-capture",
          name: "Demo catalog",
          entityType: "video",
          supportedScrapes: ["name"],
          urls: [],
          sourcePath: "/documentation-capture",
        },
      ]),
    });
  });
  await page.route(/\/api\/scrape-attempts$/, async (route) => {
    expect(route.request().method()).toBe("POST");
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        id: "documentation-capture-attempt",
        scraperId: "documentation-capture",
        entityType: "video",
        entityId: 25,
        inputKind: "name",
        resultJson: JSON.stringify({
          Title: featuredVideoTitle,
          Date: "2026-02-20",
          Studio: "Barely Dressed Pictures",
          Performers: ["Lucia Ferrer", "Darius King", "Elias Grant"],
          Tags: ["2020s", "Mystery", "Thriller"],
          Details:
            "A professional delivery draws an escort and a retired handler into the same unfinished case.",
        }),
        status: "Success",
        createdAt: "2026-09-05T00:00:00Z",
      }),
    });
  });
  await page.route(
    /\/api\/scrape-attempts\/resolve-relations$/,
    async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ tags: [], performers: [] }),
      });
    },
  );

  await openFeaturedVideoWithFixtures(page);
  await page.getByRole("button", { name: "Operations" }).click();
  await page.getByRole("button", { name: "Scrape / Metadata…" }).click();
  const dialog = page.getByRole("dialog", { name: "Scrape / Metadata" });
  const query = dialog.getByRole("textbox", { name: "Title or name..." });
  await expect(query).toBeVisible();
  await query.press("Enter");
  await expect(dialog.getByRole("button", { name: "Save" })).toBeVisible();
  await expect(
    dialog.locator("select").first().locator("option:checked"),
  ).toHaveText("Demo catalog (Scraper)");
  await expect(
    dialog.getByText("Lucia Ferrer", { exact: true }).last(),
  ).toBeVisible();
  await pinVideo(page, 4);

  await captureAnnotatedManualScreenshot(page, "scraper-run", [
    {
      label: "Start a scrape",
      tone: "green",
      targets: [
        dialog.getByRole("heading", { name: "Scrape / Metadata" }),
        query,
      ],
      padding: 7,
    },
    {
      label: "Review before applying",
      tone: "blue",
      targets: [
        dialog.getByText(featuredVideoTitle, { exact: true }).last(),
        dialog.getByRole("button", { name: "Save" }),
      ],
      padding: 7,
      labelAlign: "right",
    },
  ]);
});

test("captures tag meaning, aliases, and relationships", async ({ page }) => {
  await openManualCapturePage(
    page,
    "/tag/7",
    page.getByRole("heading", { name: "Mystery", level: 1 }),
  );
  await expect(
    page.getByRole("link", { name: `Open video ${featuredVideoTitle}` }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Edit" }).click();
  const editPanel = page
    .getByRole("heading", { name: "Edit Tag: Mystery" })
    .locator("..")
    .locator("..");
  await expect(
    editPanel.getByRole("textbox", { name: "Tag name" }),
  ).toHaveValue("Mystery");
  await expect(editPanel.getByText("Aliases", { exact: true })).toBeVisible();
  await expect(
    editPanel.getByText("Parent Tags", { exact: true }),
  ).toBeVisible();
  await expect(
    editPanel.getByText("Child Tags", { exact: true }),
  ).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "tagging-basics", [
    {
      label: "Reusable tag definition",
      tone: "green",
      targets: [
        editPanel.getByRole("textbox", { name: "Tag name" }),
        editPanel.getByRole("textbox", { name: "Tag description" }),
      ],
      padding: 7,
    },
    {
      label: "Aliases & relationships",
      tone: "blue",
      targets: [
        editPanel.getByText("Aliases", { exact: true }).locator(".."),
        editPanel.getByText("Parent Tags", { exact: true }).locator(".."),
        editPanel.getByText("Child Tags", { exact: true }).locator(".."),
      ],
      padding: 7,
    },
  ]);
});

test("captures whole-video tags, occurrence tags, and timeline context", async ({
  page,
}) => {
  await openFeaturedVideoWithFixtures(page);
  await showRawSegments(page);
  await page.getByRole("tab", { name: "Edit" }).click();
  const occurrenceToggle = page.getByRole("button", {
    name: /Performer Occurrence Tags/,
  });
  await occurrenceToggle.click();
  const occurrenceSearches = page.getByRole("combobox", {
    name: "Search tags for this occurrence...",
  });
  await expect(occurrenceSearches).toHaveCount(3);
  await expect(page.getByText("Actor", { exact: true }).last()).toBeVisible();
  await pinVideo(page, 4);
  await expect(page.getByText("Timeline overlays")).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "occurrence-tagging", [
    {
      label: "Whole-video tags",
      tone: "green",
      targets: page
        .getByRole("combobox", { name: "Search tags..." })
        .locator("../.."),
      padding: 7,
    },
    {
      label: "Performer occurrence tags",
      tone: "blue",
      targets: [
        occurrenceToggle,
        occurrenceSearches.first(),
        occurrenceSearches.last(),
      ],
      padding: 7,
    },
    {
      label: "Segments on the timeline",
      tone: "purple",
      targets: [
        page.getByText("Timeline overlays"),
        page.getByText("Segments · Raw"),
      ],
      padding: 7,
      labelAlign: "right",
    },
  ]);
});

test("captures raw segments and their display profile", async ({ page }) => {
  await openFeaturedVideoWithFixtures(page);
  await showRawSegments(page);
  const profile = page.getByRole("combobox", { name: "Profile" });
  const documentationProfile = profile
    .locator("option")
    .filter({ hasText: "Documentation capture" });
  await expect(documentationProfile).toHaveCount(1);
  await profile.selectOption(
    (await documentationProfile.getAttribute("value")) ?? "",
  );
  await expect(profile.locator("option:checked")).toContainText(
    "Documentation capture",
  );
  await expect(
    page.getByText("Segments · Documentation capture"),
  ).toBeVisible();
  await pinVideo(page, 4);

  await captureAnnotatedManualScreenshot(page, "segments-derived", [
    {
      label: "Source segment ranges",
      tone: "green",
      targets: [
        page.getByRole("button", { name: /The handoff 1×/ }),
        page.getByRole("button", { name: /The briefing 1×/ }),
      ],
      padding: 7,
    },
    {
      label: "Display profile",
      tone: "blue",
      targets: page.getByRole("combobox", { name: "Profile" }),
      padding: 7,
    },
    {
      label: "Player overlays",
      tone: "blue",
      targets: [
        page.getByText("Timeline overlays"),
        page.getByText("Segments · Documentation capture"),
      ],
      padding: 7,
      labelAlign: "right",
    },
  ]);
});

test("captures the compilation playlist and player", async ({ page }) => {
  await preventAutoplay(page);
  await openManualCapturePage(
    page,
    "/compilation/8/play",
    page.getByRole("heading", { name: "New Voices and Old Orders", level: 3 }),
  );
  const playlistItems = [
    page.getByRole("button", { name: /1\. Soft Launch/ }),
    page.getByRole("button", { name: /5\. A Favor Between Professionals/ }),
  ];
  await expect(playlistItems[0]).toBeVisible();
  await expect(playlistItems[1]).toBeVisible();
  const compilationVideo = page.locator("video").last();
  await expect(compilationVideo).toBeVisible();
  await pinVideo(page, 2, compilationVideo);

  await captureAnnotatedManualScreenshot(page, "compilation-play", [
    {
      label: "Group playlist",
      tone: "green",
      targets: playlistItems,
      padding: 7,
    },
    {
      label: "Compilation player",
      tone: "blue",
      targets: compilationVideo,
      padding: 7,
      labelAlign: "right",
    },
  ]);
});

test("captures dynamic and built-in groups", async ({ page }) => {
  await openManualCapturePage(
    page,
    "/groups",
    page.getByRole("heading", { name: "Groups", level: 1 }),
  );
  const groupsResponse = await page.request.get(
    "/api/groups?page=1&perPage=1000",
  );
  const groupsPayload = (await groupsResponse.json()) as
    | { items?: Array<{ name: string }> }
    | Array<{ name: string }>;
  const groups = Array.isArray(groupsPayload)
    ? groupsPayload
    : (groupsPayload.items ?? []);
  if (!groups.some((group) => group.name === "Recently Updated Videos")) {
    const created = await page.request.post("/api/groups", {
      data: {
        name: "Recently Updated Videos",
        description: "A saved filter for the manual",
        kind: "dynamic",
        querySourceKey: "filter",
        queryJson: JSON.stringify({
          entityTypes: ["video"],
          findFilters: {
            video: {
              page: 1,
              perPage: 40,
              sort: "updated_at",
              direction: "desc",
            },
          },
        }),
      },
    });
    expect(created.ok()).toBeTruthy();
  }
  await openManualCapturePage(
    page,
    "/groups",
    page.getByRole("heading", { name: "Groups", level: 1 }),
  );
  await expect(page.getByLabel("Page Visits")).toBeHidden();
  await page.addStyleTag({
    content: ".entity-card .card-popovers { visibility: hidden !important; }",
  });
  const ordinaryDynamic = page
    .getByRole("link", { name: "Open group Recently Updated Videos" })
    .locator("..");
  await expect(ordinaryDynamic).toBeVisible();
  const builtInCards = [
    page
      .getByRole("link", { name: "Open group Continue Watching" })
      .locator(".."),
    page.getByRole("link", { name: "Open group Watch History" }).locator(".."),
    page.getByRole("link", { name: "Open group Save for Later" }).locator(".."),
  ];
  for (const card of builtInCards) await expect(card).toBeVisible();
  const dynamicBadges = page.getByText("Dynamic", { exact: true });
  await expect(dynamicBadges).toHaveCount(4);

  await captureAnnotatedManualScreenshot(page, "dynamic-groups", [
    {
      label: "Dynamic groups",
      tone: "green",
      targets: ordinaryDynamic,
      padding: 5,
    },
    {
      label: "Built in to Cove",
      tone: "blue",
      targets: builtInCards,
      padding: 7,
      labelAlign: "right",
    },
  ]);
});

test("captures the scrubber swimlanes and playback controls", async ({
  page,
}) => {
  await openFeaturedVideoWithFixtures(page);
  await showRawSegments(page);
  const facesToggle = page.getByRole("button", { name: /Show faces/ });
  if (await facesToggle.isVisible()) await facesToggle.click();
  await expect(page.getByText("Faces", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("button", { name: /object \(98%\) at 0:08/ }),
  ).toBeVisible();
  const speed = page.getByRole("button", { name: "1x" });
  const loopStart = page.getByRole("button", { name: "Set loop start (A)" });
  const loopVideo = page.getByRole("button", { name: "Loop video" });
  await expect(speed).toBeVisible();
  await expect(loopStart).toBeVisible();
  await expect(loopVideo).toBeVisible();
  await pinVideo(page, 4);

  await captureAnnotatedManualScreenshot(page, "video-scrubber", [
    {
      label: "Segments, detections & faces",
      tone: "green",
      targets: [
        page.getByText("Timeline overlays"),
        page.getByText("Segments · Raw"),
        page.getByText("Faces", { exact: true }),
      ],
      padding: 7,
      labelAlign: "right",
    },
    {
      label: "Speed & loop controls",
      tone: "blue",
      targets: [speed, loopStart, loopVideo],
      padding: 7,
      labelAlign: "right",
    },
  ]);
});
