import { expect, test } from '@playwright/test';
import { blockEngagementWrites } from './capture-helpers';
import { captureManualElementScreenshot, openManualCapturePage } from './manual-capture-helpers';

test.beforeEach(async ({ page }) => {
  await blockEngagementWrites(page);
});

test('captures the Settings navigation for finding logs', async ({ page }) => {
  const ready = page.getByRole('heading', { name: 'Logs', level: 2 });
  await openManualCapturePage(page, '/settings/system-info/logs', ready);
  const settingsNavigation = page.getByRole('heading', { name: 'Settings', level: 1 }).locator('xpath=ancestor::aside[1]');
  await expect(settingsNavigation.getByRole('button', { name: 'Logs', exact: true })).toHaveClass(/bg-card/);

  await captureManualElementScreenshot(page, 'settings-logs-navigation', settingsNavigation);
});
