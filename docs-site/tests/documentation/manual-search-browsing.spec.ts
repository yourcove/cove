import { expect, test } from "@playwright/test";
import { blockEngagementWrites } from "./capture-helpers";
import {
  captureAnnotatedManualScreenshot,
  openManualCapturePage,
} from "./manual-capture-helpers";
import {
  arrangeVerticalVideo,
  verticalVideoTitle,
} from "./vertical-video-fixtures";

const videoGridPath =
  "/videos?q=&page=1&perPage=20&sort=date&direction=desc&view=grid&filters=%7B%7D&searchMode=text";
const feedPath =
  "/videos?q=Exit%20Music&page=1&perPage=infinite&sort=date&direction=desc&view=feed&filters=%7B%7D&searchMode=text";
const verticalPath =
  "/videos?q=&page=1&perPage=infinite&sort=date&direction=desc&view=vertical&filters=%7B%7D&searchMode=text";

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test("captures search scoped to the current content page", async ({ page }) => {
  await openManualCapturePage(
    page,
    videoGridPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  const search = page.getByRole("textbox", { name: "Search list" });
  await search.fill("Rook");
  await expect(
    page.getByRole("link", { name: "Open video Rook: New Orders" }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Open video Exit Music for Two" }),
  ).toHaveCount(0);

  await captureAnnotatedManualScreenshot(page, "search-bar", [
    {
      label: "Search this content type",
      tone: "green",
      targets: search,
      padding: 5,
    },
  ]);
});

test("captures global search across related library records", async ({
  page,
}) => {
  await openManualCapturePage(
    page,
    videoGridPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  await arrangeVerticalVideo(page);
  await openManualCapturePage(
    page,
    videoGridPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  const search = page.getByRole("combobox", { name: "Search all..." });
  await search.fill("Lucia");
  const results = page.getByRole("listbox");
  await expect(results).toBeVisible();
  await expect(results.getByText("Performers", { exact: true })).toBeAttached();
  await expect(results.getByText("Galleries", { exact: true })).toBeAttached();
  await expect(results.getByText("Images", { exact: true })).toBeAttached();
  await expect(
    results.getByText("Lucia Ferrer", { exact: true }).first(),
  ).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "global-search", []);
});

test("captures a curated group and its mixed library items", async ({
  page,
}) => {
  await openManualCapturePage(
    page,
    "/group/8",
    page.getByRole("heading", { name: "New Voices and Old Orders", level: 1 }),
  );
  await expect(page.getByRole("tab", { name: "Items" })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Open video Soft Launch" }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", {
      name: "Open video A Favor Between Professionals",
    }),
  ).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "group-detail", []);
});

test("captures the feed layout and its view control", async ({ page }) => {
  await page.route("**/api/stream/video/*/preview/status", async (route) => {
    await route.fulfill({
      contentType: "application/json",
      json: { available: false },
    });
  });
  await openManualCapturePage(
    page,
    feedPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  const feedButton = page.getByRole("button", { name: "Feed", exact: true });
  await expect(feedButton).toHaveClass(/text-accent/);
  await expect(page.getByRole("article").first()).toContainText(
    "Exit Music for Two",
  );
  await expect(
    page.getByRole("img", { name: "Exit Music for Two" }),
  ).toBeVisible();

  await captureAnnotatedManualScreenshot(page, "feed-view", [
    {
      label: "Switch a list into feed view",
      tone: "green",
      targets: feedButton,
      padding: 5,
      labelAlign: "right",
    },
  ], { screenshotHeight: 978 });
});

test("captures the vertical layout and its view control", async ({ page }) => {
  await page.route("**/api/system/config", async (route) => {
    const response = await route.fetch();
    const config = await response.json();
    config.ui.feedVideoSource = "video";
    await route.fulfill({ response, json: config });
  });
  await openManualCapturePage(
    page,
    videoGridPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  await arrangeVerticalVideo(page);
  await openManualCapturePage(
    page,
    verticalPath,
    page.getByRole("heading", { name: "Videos", level: 1 }),
  );
  const verticalButton = page.getByRole("button", {
    name: "Vertical Viewer",
    exact: true,
  });
  await expect(verticalButton).toHaveClass(/text-accent/);
  const firstItem = page.getByRole("article").first();
  await expect(firstItem).toContainText(verticalVideoTitle);
  await expect(firstItem).toContainText("Full video");
  const video = firstItem.locator("video");
  await expect(video).toBeVisible();
  await expect
    .poll(() =>
      video.evaluate((element: HTMLVideoElement) => ({
        width: element.videoWidth,
        height: element.videoHeight,
        readyState: element.readyState,
      })),
    )
    .toMatchObject({ width: 720, height: 1280, readyState: 4 });
  await video.evaluate(async (element: HTMLVideoElement) => {
    element.pause();
    const seeked = new Promise<void>((resolve) =>
      element.addEventListener("seeked", () => resolve(), { once: true }),
    );
    element.currentTime = 2;
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
    .toMatchObject({ paused: true, currentTime: 2 });

  await captureAnnotatedManualScreenshot(page, "vertical-view", [
    {
      label: "Switch a list into vertical view",
      tone: "green",
      targets: verticalButton,
      padding: 5,
      labelAlign: "right",
    },
  ]);
});

test("captures the tag relationship graph", async ({ page }) => {
  await openManualCapturePage(
    page,
    "/tags",
    page.getByRole("heading", { name: "Tags", level: 1 }),
  );
  const graphButton = page.getByRole("button", { name: "Graph/Tree" });
  await graphButton.click();
  await expect(page.getByPlaceholder("Find a tag in the graph")).toBeVisible();
  await expect(page.getByText(/\d+ nodes/)).toBeVisible();
  await expect(page.getByText(/[1-9]\d* parent-child links/)).toBeVisible();
  await expect(page.getByText(/4 tag groups \/ clusters/)).toBeVisible();
  const graph = page.getByRole("img", { name: "Tag relationship graph" });
  await expect
    .poll(async () => (await graph.boundingBox())?.height ?? 0)
    .toBeGreaterThan(1200);
  await expect
    .poll(async () => {
      const graphBox = await graph.boundingBox();
      const rootBox = await graph.locator('[data-node-name="Demo Archive"]').boundingBox();
      if (!graphBox || !rootBox) return false;
      const rootCenterX = rootBox.x + rootBox.width / 2;
      const rootCenterY = rootBox.y + rootBox.height / 2;
      return (
        Math.abs(rootCenterX - (graphBox.x + graphBox.width / 2)) < graphBox.width * 0.15 &&
        Math.abs(rootCenterY - (graphBox.y + graphBox.height / 2)) < graphBox.height * 0.15
      );
    })
    .toBe(true);
  await page.getByRole("button", { name: "Fit All" }).click();
  await expect
    .poll(() =>
      graph.locator("[data-cluster-halo]").evaluateAll((halos) => {
        const graphBox = (halos[0] as SVGGraphicsElement | undefined)?.ownerSVGElement?.getBoundingClientRect();
        if (!graphBox) return false;
        return halos.every((halo) => {
          const box = halo.getBoundingClientRect();
          return box.left >= graphBox.left && box.top >= graphBox.top && box.right <= graphBox.right && box.bottom <= graphBox.bottom;
        });
      }),
    )
    .toBe(true);

  await captureAnnotatedManualScreenshot(page, "tags-graph", [
    {
      label: "Tag relationship graph",
      tone: "green",
      targets: graphButton,
      padding: 5,
    },
  ]);
});
