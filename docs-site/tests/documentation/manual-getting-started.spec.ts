import { expect, test } from '@playwright/test';
import { blockEngagementWrites } from './capture-helpers';
import { captureAnnotatedManualScreenshot, openManualCapturePage } from './manual-capture-helpers';

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test('captures the manual navigation overview with its content-page callout', async ({ page }) => {
  const ready = page.getByRole('heading', { name: 'Continue Watching' });
  await openManualCapturePage(page, '/', ready);
  for (const imageName of [
    'Dangerously Overdressed',
    'Exit Music for Two',
    'Available Light Cooperative',
    'New Voices and Old Orders',
    'Noor Haddad',
    'Three Working Lives',
  ]) {
    await expect(page.getByRole('img', { name: imageName }).first()).toBeVisible();
  }

  await captureAnnotatedManualScreenshot(page, 'nav-bar', [{
    label: 'Content pages',
    tone: 'green',
    targets: [
      page.getByRole('link', { name: 'Videos' }),
      page.getByRole('link', { name: 'Studios' }),
    ],
    padding: 5,
  }]);
});

test('captures the manual library path screen with its add-path callout', async ({ page }) => {
  const ready = page.getByRole('heading', { name: 'Paths & Storage', level: 2 });
  await openManualCapturePage(page, '/settings/library/paths-storage', ready);

  const pathInput = page.getByRole('textbox', { name: 'D:\\\\Media\\\\Videos' });
  await expect(pathInput).toBeVisible();
  const addPath = page.getByRole('button', { name: 'Add path' });
  const exclusionCheckboxes = ['Exclude videos', 'Exclude images', 'Exclude audio', 'Exclude texts']
    .map((name) => ({ target: page.getByRole('checkbox', { name }), checked: false }));

  await captureAnnotatedManualScreenshot(page, 'library-paths', [{
    label: 'Add a content folder',
    tone: 'green',
    targets: addPath,
    padding: 5,
  }], {
    inputValues: [{ target: pathInput, value: 'D:\\Media\\Library' }],
    checkboxValues: exclusionCheckboxes,
  });
});

test('captures the manual scan and generate screen with ordered callouts', async ({ page }) => {
  const ready = page.getByRole('heading', { name: 'Scan & Generate', level: 2 });
  await openManualCapturePage(page, '/settings/operations/scan-generate', ready);

  const taskCard = (name: 'Scan' | 'Generate') => page
    .getByRole('heading', { name, level: 4 })
    .locator('xpath=ancestor::div[contains(concat(" ", normalize-space(@class), " "), " rounded-xl ")][1]');
  const scanCard = taskCard('Scan');
  const generateCard = taskCard('Generate');
  await expect(scanCard.getByRole('button', { name: 'Run', exact: true })).toBeVisible();
  await expect(generateCard.getByRole('button', { name: 'Run', exact: true })).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'settings-scan-generate', [
    {
      label: 'Scan first',
      tone: 'green',
      targets: scanCard,
      padding: 9,
    },
    {
      label: 'Generate next',
      tone: 'blue',
      targets: generateCard,
      padding: 9,
    },
  ]);
});
