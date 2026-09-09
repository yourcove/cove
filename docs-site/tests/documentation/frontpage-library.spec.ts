import { expect, test } from '@playwright/test';
import { captureScreenshotPair, openAuthenticatedPage, prepareDefaultAppearance } from './capture-helpers';

const feedPath = '/videos?q=&page=1&perPage=infinite&sort=date&direction=desc&view=feed&filters=%7B%7D&searchMode=text';
const rolesPath = '/settings/security-access/roles-permissions';

test('capture the frontpage feed browsing screenshot', async ({ page }) => {
  await openAuthenticatedPage(page, feedPath, page.getByRole('heading', { level: 1, name: 'Videos' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('button', { name: 'Feed' })).toHaveClass(/text-accent/);
  await expect(page.getByRole('combobox', { name: 'Primary sort' })).toHaveValue('date');
  await expect(page.getByRole('button', { name: 'Sort descending' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Items per page' })).toHaveValue('infinite');
  await expect(page.getByRole('link', { name: 'Open video Exit Music for Two' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open video A Favor Between Professionals' })).toBeAttached();
  await page.mouse.move(0, 0);

  await captureScreenshotPair(page, 'feed-viewer');
});

test('capture the frontpage global search screenshot', async ({ page }) => {
  await openAuthenticatedPage(page, '/', page.getByRole('combobox', { name: 'Dashboard' }));
  await prepareDefaultAppearance(page);

  const search = page.getByRole('combobox', { name: 'Search all...' });
  await search.fill('Lucia');
  await expect(search).toHaveValue('Lucia');
  const results = page.getByRole('listbox', { name: 'Global search results' });
  await expect(results).toBeVisible();
  for (const section of ['Performers', 'Galleries', 'Images', 'Audios', 'Texts']) {
    await expect(results.getByText(section, { exact: true })).toBeAttached();
  }
  await expect(results.getByRole('option', { name: 'Lucia Ferrer', exact: true })).toBeVisible();

  await captureScreenshotPair(page, 'global-search');
});

test('capture the frontpage role permissions screenshot', async ({ page }) => {
  await openAuthenticatedPage(page, rolesPath, page.getByRole('heading', { level: 2, name: 'Roles' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('row')).toHaveCount(6);
  await page.getByRole('button', { name: '+ New role' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'New role' })).toBeVisible();
  await expect(page.getByText('Permissions (0 selected)', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { level: 4, name: 'Access' })).toBeVisible();

  await captureScreenshotPair(page, 'roles-permission-editor');
});

test('capture the homepage dashboard screenshot', async ({ page }) => {
  await openAuthenticatedPage(page, '/', page.getByRole('combobox', { name: 'Dashboard' }));
  await prepareDefaultAppearance(page);

  await expect(page.getByRole('combobox', { name: 'Dashboard' }).locator('option:checked')).toHaveText('Home');
  for (const section of ['Continue Watching', 'Recently Released Videos', 'Recently Added Studios', 'Recently Released Groups', 'Recently Added Performers']) {
    await expect(page.getByRole('heading', { level: 2, name: section })).toBeAttached();
  }
  for (const image of ['Dangerously Overdressed', 'Exit Music for Two', 'Available Light Cooperative', 'New Voices and Old Orders', 'Noor Haddad']) {
    await expect(page.getByRole('img', { name: image }).first()).toBeVisible();
  }

  await captureScreenshotPair(page, 'dashboard-home');
});
