import { expect, test, type Page } from '@playwright/test';
import { blockEngagementWrites } from './capture-helpers';
import { captureAnnotatedManualScreenshot, openManualCapturePage } from './manual-capture-helpers';

const videoGridPath = '/videos?q=&page=1&perPage=20&sort=date&direction=desc&view=grid&filters=%7B%7D&searchMode=text';

async function openVideoGrid(page: Page) {
  await openManualCapturePage(page, videoGridPath, page.getByRole('heading', { name: 'Videos', level: 1 }));
  await expect(page.getByRole('link', { name: 'Open video Exit Music for Two' })).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test('captures the shared list-page controls with matching manual callouts', async ({ page }) => {
  await openVideoGrid(page);

  await expect(page.getByRole('button', { name: 'Grid' })).toHaveClass(/text-accent/);
  await expect(page.getByRole('combobox', { name: 'Primary sort' })).toHaveValue('date');
  await expect(page.getByRole('combobox', { name: 'Items per page' })).toHaveValue('20');

  await captureAnnotatedManualScreenshot(page, 'list-page-anatomy', [
    {
      label: 'View switcher',
      tone: 'green',
      targets: [
        page.getByRole('button', { name: 'Grid' }),
        page.getByRole('button', { name: 'Vertical Viewer' }),
      ],
      padding: 5,
      labelPlacement: 'below',
    },
    {
      label: 'Sort & direction',
      tone: 'blue',
      targets: [
        page.getByRole('combobox', { name: 'Primary sort' }),
        page.getByRole('button', { name: 'Sort descending' }),
      ],
      padding: 5,
    },
    {
      label: 'Filters & saved filters',
      tone: 'purple',
      targets: [
        page.getByRole('button', { name: 'Saved filters' }),
        page.getByRole('button', { name: 'Filters', exact: true }),
      ],
      padding: 5,
    },
    {
      label: 'Page size',
      tone: 'orange',
      targets: page.getByRole('combobox', { name: 'Items per page' }),
      padding: 5,
    },
    {
      label: 'Card size',
      tone: 'pink',
      targets: page.getByRole('slider', { name: /Card size/ }),
      padding: 5,
    },
    {
      label: 'Create new',
      tone: 'teal',
      targets: page.getByRole('button', { name: '+ New' }),
      padding: 5,
      labelAlign: 'right',
    },
  ]);
});

test('captures card actions and linked metadata with matching manual callouts', async ({ page }) => {
  await openVideoGrid(page);

  const firstCardLink = page.getByRole('link', { name: 'Open video Exit Music for Two' });
  await firstCardLink.hover();
  const saveForLater = page.getByRole('button', { name: 'Save for Later' }).first();
  const quickView = page.getByRole('button', { name: 'Quick View' }).first();
  await expect(saveForLater).toBeVisible();
  await expect(quickView).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'card-options', [
    {
      label: 'Save for Later',
      tone: 'green',
      targets: saveForLater,
      padding: 5,
    },
    {
      label: 'Quick View',
      tone: 'green',
      targets: quickView,
      padding: 5,
    },
    {
      label: 'Open linked people and groups',
      tone: 'blue',
      targets: [
        page.getByRole('link', { name: 'Bella Bloom' }).first(),
        page.getByRole('link', { name: 'Marisol Vega' }).first(),
        page.getByRole('link', { name: 'Arun Sen' }).first(),
      ],
      padding: 5,
    },
  ]);
});
