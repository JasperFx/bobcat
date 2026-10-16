import { defineConfig, devices } from '@playwright/test'

// Driven by ../smoke.sh, which starts the packed `bobcat ui` and passes its address in
// BOBCAT_UI_URL. Chromium only: this proves the page the tool ships renders, not browser parity.
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 30_000,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['github']] : 'list',
  use: {
    baseURL: process.env.BOBCAT_UI_URL,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
