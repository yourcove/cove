import { expect, test, type Page } from "@playwright/test";
import { blockEngagementWrites } from "./capture-helpers";
import {
  captureAnnotatedManualScreenshot,
  openManualCapturePage,
} from "./manual-capture-helpers";
import { arrangeManualVideoFixtures } from "./manual-video-fixtures";

const videoPath = "/video/25";
const videosPath =
  "/videos?q=Exit%20Music&page=1&perPage=20&sort=date&direction=desc&view=grid&filters=%7B%7D&searchMode=text";
const taggerPath =
  "/videos?q=Favor&page=1&perPage=20&sort=date&direction=desc&view=tagger&filters=%7B%7D&searchMode=text";

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

async function openVideoDetails(page: Page) {
  await openManualCapturePage(
    page,
    videoPath,
    page.getByRole("heading", {
      name: "A Favor Between Professionals",
      level: 3,
    }),
  );
  await arrangeManualVideoFixtures(page);
  await openManualCapturePage(
    page,
    videoPath,
    page.getByRole("heading", {
      name: "A Favor Between Professionals",
      level: 3,
    }),
  );
  const ratingBreakdown = page.getByRole("button", {
    name: "Rating Breakdown",
  });
  if ((await ratingBreakdown.getAttribute("aria-expanded")) === "true")
    await ratingBreakdown.click();

  const video = page.locator("video");
  await expect(video).toBeVisible();
  await expect
    .poll(() =>
      video.evaluate((element: HTMLVideoElement) => element.readyState),
    )
    .toBeGreaterThanOrEqual(2);
  await video.evaluate(async (element: HTMLVideoElement) => {
    element.pause();
    const seeked = new Promise<void>((resolve) =>
      element.addEventListener("seeked", () => resolve(), { once: true }),
    );
    element.currentTime = 4;
    await seeked;
    element.pause();
  });
  await expect
    .poll(() =>
      video.evaluate((element: HTMLVideoElement) => ({
        paused: element.paused,
        currentTime: element.currentTime,
      })),
    )
    .toMatchObject({ paused: true, currentTime: 4 });
  await expect(page.getByText("0:04 / 0:15", { exact: true })).toBeVisible();
}

test("captures the anatomy of the current demo video page", async ({
  page,
}) => {
  await openVideoDetails(page);

  await captureAnnotatedManualScreenshot(page, "video-detail", [
    {
      label: "Player",
      tone: "green",
      targets: page.getByTestId("media-detail-layout-media"),
      padding: 3,
    },
    {
      label: "Tabs",
      tone: "blue",
      targets: page.locator(".media-detail-layout-tabs-rail"),
      padding: 3,
    },
    {
      label: "Studio, performers, tags & groups",
      tone: "purple",
      targets: page.locator(".media-detail-layout-sidebar-content"),
      padding: 3,
      labelAlign: "right",
    },
  ]);
});

test("captures rating, favorite, and organized actions", async ({ page }) => {
  await openVideoDetails(page);
  const ratingStars = Array.from({ length: 5 }, (_, index) =>
    page.getByRole("button", { name: "Set rating" }).nth(index),
  );

  await captureAnnotatedManualScreenshot(page, "video-detail-actions", [
    {
      label: "5-star rating",
      tone: "green",
      targets: ratingStars,
      padding: 4,
    },
    {
      label: "Favorite",
      tone: "blue",
      targets: page.getByRole("button", { name: "Favorite" }),
      padding: 4,
      labelPlacement: "below",
    },
    {
      label: "Organized",
      tone: "orange",
      targets: page.getByRole("button", { name: "Organized" }),
      padding: 4,
      labelPlacement: "below",
      labelAlign: "right",
    },
  ]);
});

test("captures the current demo video edit form", async ({ page }) => {
  await openVideoDetails(page);
  await page.getByRole("tab", { name: "Edit" }).click();
  await expect(page.getByRole("textbox", { name: "Title" })).toHaveValue(
    "A Favor Between Professionals",
  );
  await expect(
    page.getByRole("combobox", { name: "Search tags..." }),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Save" })).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "video-edit", []);
});

test("captures adding a video from a URL", async ({ page }) => {
  await openManualCapturePage(
    page,
    videosPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  await page.getByRole("button", { name: "+ New" }).click();
  await expect(
    page.getByRole("heading", { name: "Create Video" }),
  ).toBeVisible();
  const sourceGroup = page.getByRole("group", { name: "Create source" });
  const urlMode = sourceGroup.getByRole("button", { name: "URL" });
  await urlMode.click();
  const urlInput = page.getByPlaceholder("https://example.com/video");
  await urlInput.fill("https://media.example/library/night-courier");
  await page.getByRole("checkbox", { name: "Scrape/store metadata" }).check();

  await captureAnnotatedManualScreenshot(page, "new-from-url", [
    {
      label: "Choose URL and paste a link",
      tone: "green",
      targets: [urlMode, urlInput],
      padding: 5,
    },
  ]);
});

test("captures metadata suggestions in the tagger", async ({ page }) => {
  await page.route("**/api/system/config", async (route) => {
    const response = await route.fetch();
    const config = await response.json();
    config.scraping.metadataServers = [
      {
        endpoint: "https://metadata.example.test/graphql",
        apiKey: "",
        name: "Cove Demo Catalog",
        maxRequestsPerMinute: 60,
      },
    ];
    await route.fulfill({ response, json: config });
  });
  await page.route(
    "**/api/videos/*/metadata-server/search**",
    async (route) => {
      await route.fulfill({
        contentType: "application/json",
        json: [
          {
            endpoint: "https://metadata.example.test/graphql",
            serverName: "Cove Demo Catalog",
            id: "a-favor-between-professionals",
            title: "A Favor Between Professionals",
            code: "BDP-25",
            date: "2026-02-20",
            director: "Mara Voss",
            details:
              "A courier, an escort, and a retired handler disagree about one overdue delivery.",
            studioName: "Barely Dressed Pictures",
            duration: 15,
            performerNames: ["Lucia Ferrer", "Darius King", "Elias Grant"],
            tagNames: ["2020s", "Mystery", "Thriller"],
            urls: [],
            fingerprintAlgorithms: [],
            matchCount: 0,
            fingerprints: [],
            studioCandidate: {
              remoteId: "studio-bdp",
              name: "Barely Dressed Pictures",
              existsLocally: true,
            },
            performerCandidates: [
              {
                remoteId: "performer-lucia",
                name: "Lucia Ferrer",
                existsLocally: true,
              },
              {
                remoteId: "performer-darius",
                name: "Darius King",
                existsLocally: true,
              },
              {
                remoteId: "performer-elias",
                name: "Elias Grant",
                existsLocally: true,
              },
            ],
            tagCandidates: [
              { remoteId: "tag-2020s", name: "2020s", existsLocally: true },
              { remoteId: "tag-mystery", name: "Mystery", existsLocally: true },
              {
                remoteId: "tag-thriller",
                name: "Thriller",
                existsLocally: true,
              },
            ],
          },
        ],
      });
    },
  );

  await openManualCapturePage(
    page,
    taggerPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  const source = page
    .locator("select")
    .filter({ has: page.locator('option[value^="metadata-server:"]') });
  await expect(source).toHaveValue(
    "metadata-server:https://metadata.example.test/graphql",
  );
  const query = page.getByPlaceholder("Search query...");
  await expect(query).toBeVisible();
  await query.locator("..").getByRole("button").first().click();
  const save = page.getByRole("button", { name: "Save", exact: true });
  await expect(save).toBeVisible();
  await expect(
    page.getByText("Mara Voss", { exact: true }).first(),
  ).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "tagger-view", [
    {
      label: "Item and suggested match",
      tone: "green",
      targets: [
        page.getByTitle("Open video A Favor Between Professionals"),
        save.locator(".."),
      ],
      padding: 5,
    },
    {
      label: "Review and apply",
      tone: "blue",
      targets: save,
      padding: 5,
      labelPlacement: "below",
      labelAlign: "right",
    },
  ]);
});

test("captures field provenance on the current demo video", async ({
  page,
}) => {
  await openVideoDetails(page);
  const tag = page.getByRole("button", { name: "2020s", exact: true });
  await tag.hover();
  const popup = page
    .locator("body > div.fixed.z-\\[200\\]")
    .filter({ hasText: "Tag Sources" });
  await expect(popup).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "field-provenance", [
    {
      label: "Hover a field to see where its value came from",
      tone: "green",
      targets: tag.locator(".."),
      padding: 5,
    },
  ]);
});
