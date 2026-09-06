import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import sharp from 'sharp';
import { themeFrameDefinitions } from '../../src/lib/themeFrameDefinitions';

const docsSiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const originalFrameDirectory = path.join(docsSiteRoot, 'public/images/screenshots/theme-reel');
const thumbnailDirectory = path.join(docsSiteRoot, 'public/images/screenshots/theme-reel-thumbnails');
const videoCapturePath = '/videos?q=&page=1&perPage=40&sort=date&direction=desc&view=grid&filters=%7B%7D&searchMode=text';
const frozenMotionCss = '*, *::before, *::after { animation: none !important; caret-color: transparent !important; scroll-behavior: auto !important; transition: none !important; }';

function requiredEnvironment(name: 'COVE_DEV_APP_USERNAME' | 'COVE_DEV_APP_PASSWORD') {
  const value = process.env[name];
  if (!value) throw new Error(`${name} must be set when the demo app requires authentication.`);
  return value;
}

function optionName(label: string) {
  const escaped = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`^${escaped}(?:\\s|$)`);
}

async function openThemeSettings(page: Page) {
  await page.goto('/settings/my/theme');
  const username = page.getByRole('textbox', { name: 'Username' });
  const themeHeading = page.getByRole('heading', { level: 2, name: 'Theme' });
  await expect(username.or(themeHeading)).toBeVisible();
  if (await username.isVisible()) {
    await username.fill(requiredEnvironment('COVE_DEV_APP_USERNAME'));
    await page.getByRole('textbox', { name: 'Password' }).fill(requiredEnvironment('COVE_DEV_APP_PASSWORD'));
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('link', { name: 'Settings' })).toBeVisible({ timeout: 15_000 });
    await page.goto('/settings/my/theme');
  }
  await expect(themeHeading).toBeVisible();
}

async function selectTheme(page: Page, label: string, themeId?: string) {
  const persisted = page.waitForResponse((response) => {
    const request = response.request();
    return request.method() === 'PUT' && new URL(response.url()).pathname.endsWith('/auth/me/ui-preferences') && response.ok();
  }, { timeout: 15_000 });
  await page.getByRole('button', { name: optionName(label) }).click();
  if (themeId) await expect(page.locator('html')).toHaveAttribute('data-theme', themeId);
  await persisted;
}

async function waitForVisibleImages(page: Page) {
  await expect.poll(() => page.evaluate(() => [...document.images]
    .filter((image) => {
      const bounds = image.getBoundingClientRect();
      return bounds.bottom > 0 && bounds.top < window.innerHeight && bounds.right > 0 && bounds.left < window.innerWidth;
    })
    .every((image) => image.complete && image.naturalWidth > 0)), { timeout: 15_000 }).toBe(true);
  await page.evaluate(() => document.fonts.ready);
}

test('capture the homepage theme reel from the demo library', async ({ page }) => {
  test.setTimeout(180_000);
  await mkdir(originalFrameDirectory, { recursive: true });
  await mkdir(thumbnailDirectory, { recursive: true });
  await openThemeSettings(page);

  const paletteText = await page.getByRole('button', { name: /^Color Palette\(/ }).innerText();
  const originalThemeLabel = paletteText.match(/\((.+)\)/)?.[1];
  expect(originalThemeLabel, 'The active color palette should be shown in Theme settings.').toBeTruthy();
  const originalThemeId = await page.locator('html').getAttribute('data-theme');
  expect(originalThemeId, 'The active theme should identify itself on the document element.').toBeTruthy();

  try {
    for (const frame of themeFrameDefinitions) {
      await test.step(frame.label, async () => {
        await openThemeSettings(page);
        await selectTheme(page, frame.themeLabel ?? frame.label, frame.themeId);
        await page.goto(videoCapturePath);
        await expect(page.getByRole('heading', { level: 1, name: 'Videos' })).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('data-theme', frame.themeId);
        await page.addStyleTag({ content: frozenMotionCss });

        await expect(page.getByRole('textbox', { name: 'Search list' })).toHaveValue('');
        await expect(page.getByRole('combobox', { name: 'Primary sort' })).toHaveValue('date');
        await expect(page.getByRole('button', { name: 'Sort descending' })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Filters', exact: true })).toBeVisible();
        await expect(page.getByRole('combobox', { name: 'Items per page' })).toHaveValue('40');
        await expect(page.getByRole('button', { name: 'Grid', exact: true })).toHaveClass(/text-accent/);
        await page.getByRole('slider', { name: /^Card size:/ }).fill('1');
        await expect(page.getByRole('slider', { name: 'Card size: 275px' })).toHaveValue('1');
        await expect(page.getByRole('link', { name: /^Open video / })).toHaveCount(25);
        await waitForVisibleImages(page);

        const originalPath = path.join(originalFrameDirectory, `${frame.name}.png`);
        const thumbnailPath = path.join(thumbnailDirectory, `${frame.name}.webp`);
        await page.screenshot({ path: originalPath, animations: 'disabled', caret: 'hide' });
        await sharp(originalPath).resize(850, 650).webp({ quality: 82 }).toFile(thumbnailPath);

        await expect.poll(async () => sharp(originalPath).metadata()).toMatchObject({ width: 1700, height: 1300, format: 'png' });
        await expect.poll(async () => sharp(thumbnailPath).metadata()).toMatchObject({ width: 850, height: 650, format: 'webp' });
      });
    }
  } finally {
    await openThemeSettings(page);
    await selectTheme(page, originalThemeLabel!, originalThemeId!);
  }
});
