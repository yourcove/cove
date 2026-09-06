import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/documentation',
  outputDir: '../gitignored/dev/docs-playwright',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: process.env.COVE_DEV_APP_URL ?? 'http://127.0.0.1:5173',
    viewport: { width: 1700, height: 1300 },
    deviceScaleFactor: 1,
    screenshot: 'off',
    trace: 'off',
    video: 'off',
  },
});
