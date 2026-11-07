import { randomUUID } from "node:crypto";
import { defineConfig, devices } from "@playwright/test";
// PostgreSQL with `services/api/db/schema.sql` applied (`pnpm --filter @vybe/e2e db:up` locally).
import { DATABASE_URL } from "./specs/db.ts";
// Fake Supabase Auth for sign-in, linking and admin roles (tests/e2e/fake-auth; port E2E_AUTH_PORT).
import { AUTH_ANON_KEY, AUTH_ISSUER, AUTH_PORT, AUTH_URL } from "./fake-auth/config.ts";

// Override the ports to run several worktrees at the same time (tests/e2e/scripts/db-up.sh).
const PORT = Number(process.env["E2E_WEB_PORT"] ?? 4321);
const API_PORT = Number(process.env["E2E_API_PORT"] ?? 5246);
const isCI = process.env["CI"] !== undefined;
// A new secret per run for the fake Auth test endpoints; the workers inherit it from this process.
process.env["E2E_AUTH_TEST_SECRET"] ??= randomUUID();


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
      // Test-only stand-in for Supabase Auth: JWKS, password sign-in, `/auth/v1/user`, roles and `amr`.
      command: "node fake-auth/server.ts",
      url: `${AUTH_URL}/auth/v1/.well-known/jwks.json`,
      // Never reuse: the server must have this run's test secret.
      reuseExistingServer: false,
      timeout: 30_000,
      env: { E2E_AUTH_PORT: String(AUTH_PORT), E2E_WEB_PORT: String(PORT), E2E_AUTH_TEST_SECRET: process.env["E2E_AUTH_TEST_SECRET"] },
    },
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
        RateLimits__LinkPerIpPerMinute: "10000",
        // Supabase bearer tokens from the fake Auth (http is accepted in Development only).
        Auth__Supabase__Authority: AUTH_ISSUER,
        Auth__Supabase__ApiKey: AUTH_ANON_KEY,
        // Exports and deletions finish within seconds.
        Privacy__PollSeconds: "1",
      },
    },
    {
      // The dev build proxies /api to the API (apps/web/proxy.conf.json), so the guest cookie is first-party.
      // It keeps the "Tuning (dev)" panel; the tests do not depend on it.
      command: `pnpm --filter @vybe/web dev --port ${PORT}`,
      url: `http://localhost:${PORT}`,
      reuseExistingServer: !isCI,
      timeout: 180_000,
      env: { E2E_API_PORT: String(API_PORT), E2E_AUTH_PORT: String(AUTH_PORT) },
    },
  ],
});
