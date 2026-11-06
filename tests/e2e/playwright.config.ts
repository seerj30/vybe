import { defineConfig, devices } from "@playwright/test";

const PORT = 4321;
const API_PORT = 5246;
const isCI = process.env["CI"] !== undefined;

/** PostgreSQL with `services/api/db/schema.sql` applied (`pnpm --filter @vybe/e2e db:up` locally). */
const DATABASE_URL = process.env["E2E_DATABASE_URL"] ?? "postgres://vybe:vybe@localhost:55432/vybe";

function npgsql(url: string): string {
  const u = new URL(url);
  return `Host=${u.hostname};Port=${u.port || "5432"};Database=${u.pathname.slice(1)};Username=${decodeURIComponent(u.username)};Password=${decodeURIComponent(u.password)}`;
}

export default defineConfig({
  testDir: "./specs",
  fullyParallel: true,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  reporter: isCI ? [["github"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "desktop-chromium",
      use: { ...devices["Desktop Chrome"] },
      testIgnore: /touch\.spec\.ts/,
    },
    {
      name: "mobile-chromium",
      use: {
        browserName: "chromium",
        viewport: { width: 375, height: 812 },
        deviceScaleFactor: 2,
        isMobile: true,
        hasTouch: true,
      },
      testMatch: /touch\.spec\.ts/,
    },
  ],
  webServer: [
    {
      // The real API (ADR 0003) on PostgreSQL; it seeds rulesets and the catalog at start-up.
      command: "dotnet run --project ../../services/api/src/Vybe.Api --no-launch-profile",
      url: `http://localhost:${API_PORT}/api/v1/catalog/items`,
      reuseExistingServer: !isCI,
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
        ConnectionStrings__Vybe: npgsql(DATABASE_URL),
        Content__SeedOnStartup: "true",
        // Every test creates guests from the same address.
        RateLimits__GuestsPerIpPerMinute: "10000",
      },
    },
    {
      // The dev build proxies /api to the API (apps/web/proxy.conf.json), so the guest cookie is first-party.
      // It keeps the "Tuning (dev)" panel; the tests do not depend on it.
      command: `pnpm --filter @vybe/web dev --port ${PORT}`,
      url: `http://localhost:${PORT}`,
      reuseExistingServer: !isCI,
      timeout: 180_000,
    },
  ],
});
