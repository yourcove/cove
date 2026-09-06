import { expect, test, type Cookie, type Locator, type Page } from '@playwright/test';
import { blockEngagementWrites, captureScreenshotPair, openAuthenticatedPage, prepareDefaultAppearance } from './capture-helpers';
import { arrangeHeroFaceFixtures, heroFaceLabels, heroFaceNames } from './hero-face-fixtures';
import { arrangeVerticalVideo, verticalVideoTitle } from './vertical-video-fixtures';

const occurrenceVideoPath = '/video/23';
const occurrenceVideoId = 23;
const occurrenceVideoTitle = 'Rook: New Orders';
const occurrencePerformerId = 21;
const occurrencePerformerName = 'Lucia Ferrer';
const occurrenceTagId = 18;
const occurrenceTagName = 'Actor';
const segmentsVideoPath = '/video/19';
const segmentsVideoId = 19;
const videoGridPath = '/videos?q=&page=1&perPage=20&sort=date&direction=desc&view=grid&filters=%7B%7D&searchMode=text';
const verticalViewerPath = '/videos?q=Night%20Courier&page=1&perPage=infinite&sort=date&direction=desc&view=vertical&filters=%7B%7D&searchMode=text';
let authenticationCookies: Cookie[] | undefined;

async function openGalleryPage(page: Page, pagePath: string, ready: Locator) {
  if (authenticationCookies) {
    await page.context().addCookies(authenticationCookies);
    await page.goto(pagePath);
    await expect(ready).toBeVisible();
    return;
  }

  await openAuthenticatedPage(page, pagePath, ready);
  authenticationCookies = await page.context().cookies();
}

async function pauseVideoAt(page: Page, seconds: number) {
  const video = page.locator('video').first();
  await expect(video).toBeVisible();
  await video.evaluate(async (element: HTMLVideoElement, requestedTime) => {
    element.pause();
    if (element.readyState < 1) {
      await new Promise<void>((resolve) => element.addEventListener('loadedmetadata', () => resolve(), { once: true }));
    }
    const seeked = new Promise<void>((resolve) => element.addEventListener('seeked', () => resolve(), { once: true }));
    element.currentTime = Math.min(requestedTime, element.duration);
    await seeked;
  }, seconds);
}

test('capture the audio detail screenshot', async ({ page }) => {
  await blockEngagementWrites(page);
  const title = 'The Doorway and the Pause';
  await openGalleryPage(page, '/audio/28', page.getByRole('heading', { level: 3, name: title }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('button', { name: 'Back 15 seconds' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Play', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Forward 15 seconds' })).toBeVisible();
  await expect(page.getByRole('slider', { name: 'Seek audio' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save for Later' })).toHaveAttribute('aria-pressed', 'false');
  const ratingFills = page.locator('[data-rating-star] > span');
  await expect(ratingFills).not.toHaveCount(0);
  await expect.poll(() => ratingFills.evaluateAll((fills) => fills.every((fill) => (fill as HTMLElement).style.width === '0%'))).toBe(true);

  await captureScreenshotPair(page, 'audio-detail');
});

test('capture the video search and filter controls screenshot', async ({ page }) => {
  await openGalleryPage(page, videoGridPath, page.getByRole('heading', { level: 1, name: 'Videos' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('button', { name: 'Grid' })).toHaveClass(/text-accent/);
  await expect(page.getByRole('combobox', { name: 'Primary sort' })).toHaveValue('date');
  await expect(page.getByRole('combobox', { name: 'Items per page' })).toHaveValue('20');
  await expect(page.getByRole('link', { name: 'Open video Exit Music for Two' })).toBeVisible();
  await page.getByRole('button', { name: 'Filters', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: 'Filters' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('complementary', { name: 'Filter criteria' })).toBeVisible();
  await expect(dialog.getByRole('heading', { level: 3, name: 'Choose a filter' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Apply' })).toBeVisible();

  await captureScreenshotPair(page, 'search-and-filters');
});

test('capture the performer occurrence tagging controls screenshot', async ({ page }) => {
  await blockEngagementWrites(page);
  await openGalleryPage(page, occurrenceVideoPath, page.getByRole('tab', { name: 'Details' }));

  const applicationsResponse = await page.request.get(`/api/tagapplications?hostType=video&hostId=${occurrenceVideoId}&contextType=performer&contextId=${occurrencePerformerId}`);
  expect(applicationsResponse.ok()).toBe(true);
  const applications = await applicationsResponse.json() as { tag: { id: number } }[];
  if (!applications.some((application) => application.tag.id === occurrenceTagId)) {
    const response = await page.request.post('/api/tagapplications', {
      data: {
        hostType: 'video',
        hostId: occurrenceVideoId,
        contextType: 'performer',
        contextId: occurrencePerformerId,
        tagId: occurrenceTagId,
        sourceKey: 'documentation-capture',
      },
    });
    expect(response.ok()).toBe(true);
    await page.reload();
  }

  await prepareDefaultAppearance(page);
  await page.getByRole('tab', { name: 'Edit' }).click();
  await expect(page.getByRole('heading', { level: 3, name: occurrenceVideoTitle })).toBeVisible();

  const occurrenceTags = page.getByRole('button', { name: /Performer Occurrence Tags.*[1-9]\d* tag assignments?/ });
  await occurrenceTags.click();
  await expect(page.getByRole('combobox', { name: 'Search tags for this occurrence...' })).toHaveCount(4);
  const performerCard = page.getByText(occurrencePerformerName, { exact: true }).last().locator('..').locator('..');
  await expect(performerCard).toContainText('1 tag');
  await expect(performerCard.getByText(occurrenceTagName, { exact: true })).toBeVisible();
  await pauseVideoAt(page, 4);

  await captureScreenshotPair(page, 'occurrence-tagging');
});

test('capture raw video segments and timeline overlays screenshot', async ({ page }) => {
  await blockEngagementWrites(page);
  await openGalleryPage(page, segmentsVideoPath, page.getByRole('tab', { name: 'Details' }));

  const segments = [
    { startSec: 1, endSec: 5, title: 'The missing room', colorHint: '#3b82f6' },
    { startSec: 7, endSec: 12, title: 'Following the echo', colorHint: '#f97316' },
  ];
  const existingResponse = await page.request.get(`/api/videos/${segmentsVideoId}/segments`);
  expect(existingResponse.ok()).toBe(true);
  const existingSegments = await existingResponse.json() as { id: number; startSec: number; endSec?: number; sourceKey?: string; title?: string; kind?: string; colorHint?: string }[];
  for (const segment of segments) {
    const existing = existingSegments.find((candidate) => candidate.sourceKey === 'documentation-capture' && candidate.title === segment.title);
    const matches = existing
      && existing.startSec === segment.startSec
      && existing.endSec === segment.endSec
      && existing.kind === 'chapter'
      && existing.colorHint === segment.colorHint;
    if (!existing) {
      const response = await page.request.post(`/api/videos/${segmentsVideoId}/segments`, {
        data: { ...segment, kind: 'chapter', sourceKey: 'documentation-capture' },
      });
      expect(response.ok()).toBe(true);
    } else if (!matches) {
      const response = await page.request.put(`/api/videos/${segmentsVideoId}/segments/${existing.id}`, {
        data: { ...segment, kind: 'chapter', sourceKey: 'documentation-capture', tagId: null, refId: null, payload: null, sourceRunId: null, confidence: null },
      });
      expect(response.ok()).toBe(true);
    }
  }

  await page.reload();
  await prepareDefaultAppearance(page);
  await page.getByRole('tab', { name: 'Segments' }).click();
  await page.getByRole('combobox', { name: 'Profile' }).selectOption({ label: 'Raw' });
  await expect(page.getByText('The missing room', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Following the echo', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('2 segments', { exact: true })).toBeVisible();
  await expect(page.getByText('0:01–0:05', { exact: true })).toBeVisible();
  await expect(page.getByText('0:07–0:12', { exact: true })).toBeVisible();
  await pauseVideoAt(page, 8);

  await captureScreenshotPair(page, 'segments-player-raw');
});

test('capture the curated group detail screenshot', async ({ page }) => {
  await openGalleryPage(page, '/group/8', page.getByRole('heading', { level: 1, name: 'New Voices and Old Orders' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('tab', { name: 'Items' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open video Soft Launch' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open video A Favor Between Professionals' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'New Voices and Old Orders' })).toBeVisible();

  await captureScreenshotPair(page, 'group-detail');
});

test('capture the vertical video viewer screenshot', async ({ page }) => {
  await blockEngagementWrites(page);
  await page.route('**/api/system/config', async (route) => {
    const response = await route.fetch();
    const config = await response.json();
    config.ui.feedVideoSource = 'video';
    await route.fulfill({ response, json: config });
  });
  await openGalleryPage(page, videoGridPath, page.getByRole('heading', { level: 1, name: 'Videos' }));
  await arrangeVerticalVideo(page);
  await openGalleryPage(page, verticalViewerPath, page.getByRole('heading', { level: 1, name: 'Videos' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('button', { name: 'Vertical Viewer', exact: true })).toHaveClass(/text-accent/);
  await expect(page.getByRole('combobox', { name: 'Items per page' })).toBeDisabled();
  const firstItem = page.getByRole('article').first();
  await expect(firstItem).toContainText(verticalVideoTitle);
  await expect(firstItem).toContainText('Full video');
  const video = firstItem.locator('video');
  await expect(video).toBeVisible();
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => ({
    width: element.videoWidth,
    height: element.videoHeight,
    readyState: element.readyState,
  }))).toMatchObject({ width: 720, height: 1280, readyState: 4 });
  await pauseVideoAt(page, 2);
  await page.mouse.move(1650, 125);
  const autoScrollSpeed = page.getByRole('slider', { name: 'Seconds before next vertical item' });
  await expect(autoScrollSpeed).toBeVisible();
  await expect(autoScrollSpeed.locator('..')).toHaveClass(/opacity-100/);

  await captureScreenshotPair(page, 'vertical-viewer');
});

test('capture the facial recognition screenshot', async ({ page }) => {
  await blockEngagementWrites(page);
  await page.route('**/api/system/config', async (route) => {
    const response = await route.fetch();
    const config = await response.json() as { interface: { menuItems: string[] } };
    const menuItems = config.interface.menuItems.filter((item) => item !== 'faces');
    const imagesIndex = menuItems.indexOf('images');
    menuItems.splice(imagesIndex >= 0 ? imagesIndex + 1 : 0, 0, 'faces');
    config.interface.menuItems = menuItems;
    await route.fulfill({ response, json: config });
  });
  await openGalleryPage(page, '/video/25', page.getByRole('tab', { name: 'Details' }));
  await arrangeHeroFaceFixtures(page);
  await page.route('**/api/faces?*', async (route) => {
    const response = await route.fetch();
    const payload = await response.json() as { items?: { label?: string; updatedAt?: string; performerFaceCount?: number; performerFaceIndex?: number; coverImageUrl?: string }[]; totalCount?: number };
    const items = (payload.items ?? [])
      .filter((face) => face.label && heroFaceLabels.includes(face.label))
      .sort((left, right) => heroFaceLabels.indexOf(left.label!) - heroFaceLabels.indexOf(right.label!));
    for (const face of items) {
      face.updatedAt = '2026-08-30T00:00:00Z';
      face.performerFaceCount = 1;
      face.performerFaceIndex = 1;
    }
    payload.items = items;
    payload.totalCount = items.length;
    await route.fulfill({ response, json: payload });
  });
  await openGalleryPage(page, '/faces?filters=%7B%7D', page.getByRole('heading', { level: 1, name: 'Faces' }));
  await prepareDefaultAppearance(page);
  await expect(page.getByRole('link', { name: 'Faces', exact: true })).toHaveAttribute('aria-current', 'page');

  const cardSize = page.getByRole('slider');
  await expect(cardSize).toHaveCount(1);
  await cardSize.fill('2');
  await expect(page.getByText('1-3 of 3', { exact: true })).toBeVisible();
  for (const name of heroFaceNames) {
    const faceCard = page.getByRole('link', { name: `Open face ${name}` });
    await expect(faceCard).toBeVisible();
    const faceCardContent = faceCard.locator('..');
    await expect(faceCardContent).toContainText('Updated 2026-08-30');
    await expect(faceCardContent).toContainText('Linked performer');
    await expect(faceCardContent.getByTitle('Detections')).toHaveText('3');
    await expect(faceCardContent.getByTitle('Images')).toContainText('3');
    await expect(faceCardContent.getByTitle('Videos')).toHaveCount(0);
    const portrait = faceCardContent.locator('img').first();
    await expect(portrait).toBeVisible();
    await expect.poll(() => portrait.evaluate((image: HTMLImageElement) => ({
      naturalWidth: image.naturalWidth,
      usesBackendMedia: !image.currentSrc.startsWith('data:'),
    }))).toMatchObject({ naturalWidth: 640, usesBackendMedia: true });
  }

  await captureScreenshotPair(page, 'ai-faces');
});

test('capture the extension discovery screenshot', async ({ page }) => {
  await openGalleryPage(page, '/settings/extensions/discover', page.getByRole('heading', { level: 2, name: 'Discover' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('heading', { level: 3, name: 'Find and Install Extensions' })).toBeVisible();
  const registrySearch = page.getByRole('textbox', { name: 'Search the extension registry...' });
  await registrySearch.fill('AI');
  for (const extension of ['AI Audio', 'AI Core', 'AI Faces', 'AI Tagging']) {
    await expect(page.getByText(extension, { exact: true })).toBeVisible({ timeout: 15_000 });
  }
  await expect(page.getByRole('button', { name: 'Install' }).first()).toBeVisible();

  await captureScreenshotPair(page, 'extensions-registry');
});
