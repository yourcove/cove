import { expect, test } from '@playwright/test';
import { blockEngagementWrites } from './capture-helpers';
import { captureAnnotatedManualScreenshot, openManualCapturePage } from './manual-capture-helpers';

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test('captures the available color palettes', async ({ page }) => {
  await openManualCapturePage(page, '/settings/my/theme', page.getByRole('heading', { name: 'Theme', level: 2 }));
  const palettes = [
    'Default', 'Legacy', 'Light', 'Dark Midnight', 'Dark Emerald', 'Dark Rosé', 'Dark Ocean',
    'Copper Noir', 'Golden Hour', 'Signal Dark', 'Rainbow', 'Liquid Glass', 'Neon Glow',
    'Sunset Gradient', 'Aurora', 'Cyberpunk', 'Deep Space', 'Synthwave', 'Ember', 'Cinema Dark', 'Custom',
  ].map((name) => page.getByRole('button', { name: new RegExp(`^${name}(?: |$)`) }));
  for (const palette of palettes) await expect(palette).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'theme-picker', [{
    label: 'Color palette',
    tone: 'green',
    targets: palettes,
    padding: 8,
  }]);
});

test('captures style and layout choices', async ({ page }) => {
  await openManualCapturePage(page, '/settings/my/theme', page.getByRole('heading', { name: 'Theme', level: 2 }));
  await page.getByRole('button', { name: /^Color Palette/ }).click();
  await page.getByRole('button', { name: /^Style/ }).click();
  await page.getByRole('button', { name: /^Layout/ }).click();

  const styles = ['Default Balanced corners', 'Glass', 'Rounded', 'Gradient', 'Animated', 'Floating']
    .map((name) => page.getByRole('button', { name: new RegExp(`^${name}`) }));
  const layouts = ['Default Standard layout', 'Theater Detail', 'Detail Tabs']
    .map((name) => page.getByRole('button', { name: new RegExp(`^${name}`) }));
  for (const option of [...styles, ...layouts]) await expect(option).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'style-layout-options', [
    { label: 'Pick a style (extra options appear)', tone: 'green', targets: styles, padding: 8 },
    { label: 'Pick a layout', tone: 'blue', targets: layouts, padding: 8 },
  ]);
});

test('captures segment display profiles and rules', async ({ page }) => {
  await openManualCapturePage(page, '/settings/library/display-profiles', page.getByRole('heading', { name: 'Display Profiles', level: 2 }));
  const profileChoices = [
    page.getByRole('button', { name: /^Default Global/ }),
    page.getByRole('button', { name: /^Raw Global/ }),
  ];
  for (const choice of profileChoices) await expect(choice).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: 'video' })).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'display-profiles', [
    { label: 'Pick a display profile', tone: 'green', targets: profileChoices, padding: 7 },
  ]);
});

test('captures the security and access map', async ({ page }) => {
  await openManualCapturePage(page, '/settings/security-access/users', page.getByRole('heading', { name: 'Users', level: 2 }));
  const securityPages = ['Authentication', 'Users', 'Roles', 'Content rules', 'API tokens', 'Share links', 'Audit log']
    .map((name) => page.getByRole('button', { name, exact: true }));
  for (const link of securityPages) await expect(link).toBeVisible();
  const usersTable = page.getByRole('table');
  await expect(usersTable).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'security-overview', [
    { label: 'Users, roles, rules and sharing live together', tone: 'green', targets: securityPages, padding: 7 },
    { label: 'Each page controls a layer of access', tone: 'blue', targets: usersTable, padding: 7 },
  ], {
    textValues: [{ target: usersTable.getByRole('row').nth(1).getByRole('cell').nth(4), value: 'Recently' }],
  });
});

test('captures user accounts and their assigned roles', async ({ page }) => {
  await openManualCapturePage(page, '/settings/security-access/users', page.getByRole('heading', { name: 'Users', level: 2 }));
  const usersTable = page.getByRole('table');
  await expect(usersTable.getByRole('row')).toHaveCount(2);

  await captureAnnotatedManualScreenshot(page, 'users-admin', [{
    label: 'Users and their roles',
    tone: 'green',
    targets: usersTable,
    padding: 7,
  }], {
    textValues: [{ target: usersTable.getByRole('row').nth(1).getByRole('cell').nth(4), value: 'Recently' }],
  });
});

test('captures a role and its granted permissions', async ({ page }) => {
  await openManualCapturePage(page, '/settings/security-access/roles-permissions', page.getByRole('heading', { name: 'Roles', level: 2 }));
  const roleRows = page.getByRole('table').getByRole('row').filter({ has: page.getByRole('button', { name: 'View' }) });
  const viewButtonLocator = roleRows.getByRole('button', { name: 'View' });
  await expect(viewButtonLocator).toHaveCount(5);
  const viewButtons = await viewButtonLocator.all();
  const memberRow = roleRows.filter({ hasText: 'Member' });
  await memberRow.getByRole('button', { name: 'View' }).click();
  await expect(page.getByRole('heading', { name: 'View role: Member', level: 2 })).toBeVisible();
  const permissionGroups = ['Access', 'AI', 'Audios', 'Audit', 'Extensions']
    .map((name) => page.getByRole('heading', { name, level: 4 }));
  for (const group of permissionGroups) await expect(group).toBeVisible();

  await captureAnnotatedManualScreenshot(page, 'roles-permissions', [
    {
      label: 'Open a role',
      tone: 'blue',
      targets: viewButtons,
      padding: 7,
      labelAlign: 'right',
    },
    { label: 'Permissions this role grants', tone: 'green', targets: permissionGroups, padding: 8 },
  ]);
});
