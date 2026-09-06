import { expect, test } from '@playwright/test';
import { blockEngagementWrites, captureScreenshotPair, openAuthenticatedPage, prepareDefaultAppearance } from './capture-helpers';

const scenePath = '/video/25';
const sceneTitle = 'A Favor Between Professionals';

test('capture the frontpage video detail screenshot from the selected demo scene', async ({ page }) => {
  await blockEngagementWrites(page);
  await openAuthenticatedPage(page, scenePath, page.getByRole('tab', { name: 'Details' }));
  await prepareDefaultAppearance(page);
  await page.getByRole('tab', { name: 'Details' }).click();
  await expect(page.getByRole('heading', { level: 3, name: sceneTitle })).toBeVisible();

  const ratingBreakdown = page.getByRole('button', { name: 'Rating Breakdown' });
  if (await ratingBreakdown.getAttribute('aria-expanded') === 'true') await ratingBreakdown.click();
  await expect(ratingBreakdown).toHaveAttribute('aria-expanded', 'false');

  await expect(page.getByRole('heading', { level: 6, name: 'Tags' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 6, name: 'Performers' })).toBeVisible();
  await expect(page.getByRole('link', { name: /^Open performer / })).toHaveCount(3);

  const video = page.locator('video');
  await expect(video).toBeVisible();
  await video.evaluate(async (element: HTMLVideoElement) => {
    element.pause();
    if (element.readyState < 1) {
      await new Promise<void>((resolve) => element.addEventListener('loadedmetadata', () => resolve(), { once: true }));
    }
    element.currentTime = Math.min(4, element.duration);
    await new Promise<void>((resolve) => element.addEventListener('seeked', () => resolve(), { once: true }));
  });
  await expect(page.getByText('0:04 / 0:15', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Set loop start (A)' }).click();
  await expect(page.getByRole('button', { name: 'A', exact: true })).toBeVisible();

  await page.locator('.media-detail-layout-sidebar-content').evaluate((element) => { element.scrollTop = 0; });
  await captureScreenshotPair(page, 'video-detail-timeline');
});
