import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { expect, type Locator, type Page } from "@playwright/test";
import sharp from "sharp";

const docsSiteRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../..",
);
const screenshotDirectory = path.join(
  docsSiteRoot,
  "public/images/screenshots",
);
const thumbnailDirectory = path.join(screenshotDirectory, "thumbnails");

export const frozenMotionCss =
  '*, *::before, *::after { animation: none !important; caret-color: transparent !important; scroll-behavior: auto !important; transition: none !important; } [aria-label^="Page Visits"] { display: none !important; }';

function requiredEnvironment(
  name: "COVE_DEV_APP_USERNAME" | "COVE_DEV_APP_PASSWORD",
) {
  const value = process.env[name];
  if (!value)
    throw new Error(
      `${name} must be set when the demo app requires authentication.`,
    );
  return value;
}

export async function openAuthenticatedPage(
  page: Page,
  pagePath: string,
  ready: Locator,
) {
  await page.goto(pagePath);
  const username = page.getByRole("textbox", { name: "Username" });
  await expect(username.or(ready)).toBeVisible();
  if (await username.isVisible()) {
    await username.fill(requiredEnvironment("COVE_DEV_APP_USERNAME"));
    await page
      .getByRole("textbox", { name: "Password" })
      .fill(requiredEnvironment("COVE_DEV_APP_PASSWORD"));
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("link", { name: "Settings" })).toBeVisible({
      timeout: 15_000,
    });
    await page.goto(pagePath);
  }
  await expect(ready).toBeVisible();
}

export async function prepareDefaultAppearance(page: Page) {
  await expect(page.locator("html")).toHaveAttribute("data-theme", "default");
  await expect(page.locator("html")).toHaveAttribute(
    "data-component-style",
    "default",
  );
  await expect(page.locator("html")).toHaveAttribute("data-layout", "default");
  await page.addStyleTag({ content: frozenMotionCss });
}

export async function blockEngagementWrites(page: Page) {
  await page.route("**/api/playback/intervals", async (route) => {
    await route.fulfill({ status: 204 });
  });
  await page.route("**/api/engagement/**", async (route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const ratingsMatch = pathname.match(
      /^\/api\/engagement\/[^/]+\/(\d+)\/ratings$/,
    );
    if (request.method() === "GET" && ratingsMatch) {
      await route.fulfill({
        contentType: "application/json",
        json: { hostId: Number(ratingsMatch[1]), ratings: {} },
      });
      return;
    }
    if (
      request.method() === "GET" &&
      /^\/api\/engagement\/[^/]+\/\d+$/.test(pathname)
    ) {
      await route.fulfill({
        status: 404,
        contentType: "application/json",
        json: null,
      });
      return;
    }
    await route.continue();
  });
  await page.route("**/api/engagement/batch", async (route) => {
    await route.fulfill({ contentType: "application/json", json: [] });
  });
  await page.route("**/api/me/bookmarks/batch", async (route) => {
    const payload = route.request().postDataJSON() as {
      hostType: string;
      hostIds: number[];
    };
    await route.fulfill({
      contentType: "application/json",
      json: payload.hostIds.map((hostId) => ({
        hostType: payload.hostType,
        hostId,
        saved: false,
        createdAt: null,
      })),
    });
  });
  await page.route("**/engagement/interactions", async (route) => {
    if (route.request().method() === "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 204 });
  });
}

export async function waitForVisibleImages(page: Page) {
  await expect
    .poll(
      () =>
        page.evaluate(() =>
          [...document.images]
            .filter((image) => {
              const bounds = image.getBoundingClientRect();
              return (
                bounds.bottom > 0 &&
                bounds.top < window.innerHeight &&
                bounds.right > 0 &&
                bounds.left < window.innerWidth
              );
            })
            .every((image) => image.complete && image.naturalWidth > 0),
        ),
      { timeout: 15_000 },
    )
    .toBe(true);
  await page.evaluate(() => document.fonts.ready);
}

export async function captureScreenshotPair(page: Page, name: string) {
  await mkdir(screenshotDirectory, { recursive: true });
  await mkdir(thumbnailDirectory, { recursive: true });
  const originalPath = path.join(screenshotDirectory, `${name}.png`);
  const thumbnailPath = path.join(thumbnailDirectory, `${name}.webp`);

  await waitForVisibleImages(page);
  await page.screenshot({
    path: originalPath,
    animations: "disabled",
    caret: "hide",
  });
  await sharp(originalPath)
    .resize(850, 650)
    .webp({ quality: 82 })
    .toFile(thumbnailPath);

  await expect
    .poll(async () => sharp(originalPath).metadata())
    .toMatchObject({ width: 1700, height: 1300, format: "png" });
  await expect
    .poll(async () => sharp(thumbnailPath).metadata())
    .toMatchObject({ width: 850, height: 650, format: "webp" });
}
