import { expect, test } from '@playwright/test';
import { blockEngagementWrites } from './capture-helpers';
import { captureAnnotatedManualScreenshot, openManualCapturePage } from './manual-capture-helpers';

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test('captures performer content and discovery tabs', async ({ page }) => {
  await openManualCapturePage(page, '/performer/21', page.getByRole('heading', { name: 'Lucia Ferrer', level: 1 }));
  const contentTabs = ['Videos', 'Galleries', 'Images', 'Audios', 'Texts', 'Groups', 'Faces']
    .map((name) => page.getByRole('tab', { name, exact: true }));
  for (const tab of contentTabs) await expect(tab).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'performer-page', [
    {
      label: 'Content tabs with counts',
      tone: 'green',
      targets: contentTabs,
      padding: 5,
    },
    {
      label: 'Appears With',
      tone: 'blue',
      targets: page.getByRole('tab', { name: 'Appears With' }),
      padding: 5,
    },
    {
      label: 'Similar performers',
      tone: 'purple',
      targets: page.getByRole('tab', { name: 'Similar' }),
      padding: 5,
      labelPlacement: 'below',
      labelAlign: 'right',
    },
  ], { screenshotHeight: 1301 });
});

test('captures studio media and sub-studio tabs', async ({ page }) => {
  await openManualCapturePage(page, '/studio/1', page.getByRole('heading', { name: 'Barely Dressed Pictures', level: 1 }));
  const mediaTabs = ['Videos', 'Performers', 'Galleries', 'Images', 'Audios', 'Texts', 'Groups']
    .map((name) => page.getByRole('tab', { name, exact: true }));
  for (const tab of mediaTabs) await expect(tab).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'studio-page', [
    {
      label: 'Studio media by type',
      tone: 'green',
      targets: mediaTabs,
      padding: 5,
    },
    {
      label: 'Nested sub-studios',
      tone: 'blue',
      targets: page.getByRole('tab', { name: 'Sub-studios' }),
      padding: 5,
    },
  ], { screenshotHeight: 1137 });
});

test('captures the full-size image viewer', async ({ page }) => {
  const title = 'Lucia Ferrer: Pull-Up Training Post';
  await openManualCapturePage(page, '/image/69', page.getByRole('heading', { name: title, level: 3 }));
  const image = page.getByRole('img', { name: title, exact: true });
  await expect(image).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'images-view', [{
    label: 'Full-size image viewer',
    tone: 'green',
    targets: image,
    padding: 5,
  }]);
});

test('captures a gallery image grid', async ({ page }) => {
  await openManualCapturePage(page, '/gallery/7', page.getByRole('heading', { name: 'Three Working Lives', level: 1 }));
  const galleryImages = [
    'Cressida Maraschino: Return to the Set',
    'Darius King: Location Arrival',
    'Darius King: Rehearsal Break',
    'Darius King: Publicity Interview',
    'Lucia Ferrer: Festival Conversation',
  ].map((name) => page.getByRole('img', { name }));
  for (const image of galleryImages) await expect(image).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'galleries-view', [{
    label: 'A gallery\'s images together',
    tone: 'green',
    targets: galleryImages,
    padding: 5,
  }]);
});

test('captures the audio player controls', async ({ page }) => {
  const title = 'The Doorway and the Pause';
  await openManualCapturePage(page, '/audio/28', page.getByRole('heading', { name: title, level: 3 }));
  const controls = [
    page.getByRole('button', { name: 'Back 15 seconds' }),
    page.getByRole('button', { name: 'Play', exact: true }),
    page.getByRole('button', { name: 'Forward 15 seconds' }),
    page.getByRole('button', { name: /Volume:/ }),
    page.getByRole('slider', { name: 'Seek audio' }),
  ];
  for (const control of controls) await expect(control).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'audios-view', [{
    label: 'Audio player, scrubber and volume',
    tone: 'green',
    targets: controls,
    padding: 5,
  }]);
});

test('captures the text reader', async ({ page }) => {
  await openManualCapturePage(page, '/text/27', page.getByRole('heading', { name: 'Lucia Ferrer: Movement Notebook', level: 3 }));
  const readerSections = [
    page.getByRole('heading', { name: 'Before the camera arrives', level: 2 }),
    page.getByRole('heading', { name: 'The pause at the door', level: 2 }),
    page.getByRole('heading', { name: 'After the rain', level: 2 }),
  ];
  for (const section of readerSections) await expect(section).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'texts-view', [{
    label: 'Document reader',
    tone: 'green',
    targets: readerSections,
    padding: 12,
  }]);
});
