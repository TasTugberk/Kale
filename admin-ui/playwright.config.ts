import { defineConfig, devices } from "@playwright/test";

/**
 * Browser tests against a running stack (scripts/dev-stack.sh): a real Chromium with JavaScript, through the
 * real sign-in at AuthService. Run them with scripts/browser-test.sh, which passes the addresses and the first
 * admin's password. The unit tests (vitest, test/) don't need any of this.
 */
export default defineConfig({
  testDir: "e2e",
  // One at a time: the tests share one stack and one admin.
  workers: 1,
  timeout: 60_000,
  reporter: "list",
  use: {
    ...devices["Desktop Chrome"],
    baseURL: process.env.KALE_E2E_UI_URL,
    // English texts (Accept-Language), so the tests can find buttons by their names.
    locale: "en-US",
    trace: "retain-on-failure",
  },
});
