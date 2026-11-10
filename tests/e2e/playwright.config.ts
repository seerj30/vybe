import { randomUUID } from "node:crypto";
import { defineConfig, devices, type Project } from "@playwright/test";
// PostgreSQL with `services/api/db/schema.sql` applied (`pnpm --filter @vybe/e2e db:up` locally).
import { DATABASE_URL } from "./specs/db.ts";
// Fake Supabase Auth for sign-in, linking and admin roles (tests/e2e/fake-auth; port E2E_AUTH_PORT).
import { AUTH_ANON_KEY, AUTH_ISSUER, AUTH_PORT, AUTH_SERVICE_ROLE_KEY, AUTH_URL } from "./fake-auth/config.ts";

// Override the ports to run several worktrees at the same time (tests/e2e/scripts/db-up.sh).
const PORT = Number(process.env["E2E_WEB_PORT"] ?? 4321);
const API_PORT = Number(process.env["E2E_API_PORT"] ?? 5246);
const isCI = process.env["CI"] !== undefined;
// E2E_WEB_HTTPS=1 serves the web app over HTTPS (self-signed). WebKit keeps the `Secure` guest cookie
// (`__Host-vybe_guest`) only on HTTPS, also on localhost; production is always HTTPS. e2e-browsers.yml sets it.
const https = process.env["E2E_WEB_HTTPS"] === "1";
const WEB_URL = `${https ? "https" : "http"}://localhost:${PORT}`;
// A new secret per run for the fake Auth test endpoints; the workers inherit it from this process.
process.env["E2E_AUTH_TEST_SECRET"] ??= randomUUID();


// Browser matrix (spec §14). Pull requests run Chromium (functional + visual); E2E_BROWSERS=all adds Firefox
// and WebKit (.github/workflows/e2e-browsers.yml: master pushes, nightly, manual).
const allBrowsers = process.env["E2E_BROWSERS"] === "all";
// Visual baselines are Linux screenshots (system fonts differ per OS): the visual projects run on Linux (CI),
// or anywhere with E2E_VISUAL=1 (for example in the Playwright Docker image).
const visual = process.platform === "linux" || process.env["E2E_VISUAL"] === "1";
// The critical journeys (spec §14 E01–E12, titles start with the id) for the other engines.
const CRITICAL = /\bE(0[1-9]|1[0-2])\b/;
const VISUAL_SPEC = /visual\.spec\.ts/;
// Fixed locale, time zone and motion, so screenshots do not depend on the machine.
const VISUAL_USE = { locale: "en-US", timezoneId: "UTC", reducedMotion: "reduce", colorScheme: "dark" } as const;
const MOBILE = { viewport: { width: 375, height: 812 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true };
// Headless Firefox on a Linux runner (no GPU, no display) has no WebGL, and the court needs it (Three.js).
// e2e-browsers.yml runs Firefox headed in Xvfb (E2E_FIREFOX_HEADED=1) with Mesa software OpenGL.
const FIREFOX = {
  ...devices["Desktop Firefox"],
  headless: process.env["E2E_FIREFOX_HEADED"] !== "1",
  launchOptions: { firefoxUserPrefs: { "webgl.force-enabled": true } },
};

const chromiumProjects: Project[] = [
  {
    name: "desktop-chromium",
    use: { ...devices["Desktop Chrome"] },
    testIgnore: [/touch\.spec\.ts/, VISUAL_SPEC],
  },
  {
    name: "mobile-chromium",
    use: { browserName: "chromium", ...MOBILE },
    testMatch: /touch\.spec\.ts/,
  },
];

const otherEngineProjects: Project[] = [
  { name: "desktop-firefox", use: FIREFOX, grep: CRITICAL, testIgnore: VISUAL_SPEC },
  { name: "desktop-webkit", use: { ...devices["Desktop Safari"] }, grep: CRITICAL, testIgnore: VISUAL_SPEC },
];

const visualProjects: Project[] = [
  { name: "visual-desktop-chromium", use: { ...devices["Desktop Chrome"], ...VISUAL_USE }, testMatch: VISUAL_SPEC },
  { name: "visual-mobile-chromium", use: { browserName: "chromium", ...MOBILE, ...VISUAL_USE }, testMatch: VISUAL_SPEC },
  ...(allBrowsers
    ? [
        { name: "visual-desktop-firefox", use: { ...FIREFOX, ...VISUAL_USE }, testMatch: VISUAL_SPEC },
        { name: "visual-desktop-webkit", use: { ...devices["Desktop Safari"], ...VISUAL_USE }, testMatch: VISUAL_SPEC },
        // iPhone-size WebKit; isMobile is not supported in Firefox.
        { name: "visual-mobile-webkit", use: { browserName: "webkit", ...MOBILE, ...VISUAL_USE }, testMatch: VISUAL_SPEC },
      ]
    : []),
];

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
    baseURL: WEB_URL,
    ignoreHTTPSErrors: https,
    trace: "retain-on-failure",
  },
  // One baseline per project, no OS suffix: only Linux runs the visual projects (see `visual` above).
  snapshotPathTemplate: "{testDir}/__screenshots__/{projectName}/{arg}{ext}",
  expect: {
    // Small tolerances for anti-aliasing; masked dynamic text (visual.spec.ts) keeps the rest fixed.
    toHaveScreenshot: { maxDiffPixelRatio: 0.01, threshold: 0.2, animations: "disabled", caret: "hide", scale: "css" },
  },
  projects: [...chromiumProjects, ...(allBrowsers ? otherEngineProjects : []), ...(visual ? visualProjects : [])],
  webServer: [
    {
      // Test-only stand-in for Supabase Auth: JWKS, password sign-in, `/auth/v1/user`, roles and `amr`.
      command: "node fake-auth/server.ts",
      url: `${AUTH_URL}/auth/v1/.well-known/jwks.json`,
      // Never reuse: the server must have this run's test secret.
      reuseExistingServer: false,
      timeout: 30_000,
      env: {
        E2E_AUTH_PORT: String(AUTH_PORT),
        E2E_WEB_PORT: String(PORT),
        E2E_WEB_HTTPS: https ? "1" : "0",
        E2E_AUTH_TEST_SECRET: process.env["E2E_AUTH_TEST_SECRET"],
      },
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
        // J4 deletes the Auth user through the fake Auth admin API (spec §17 decision 8).
        Auth__Supabase__ServiceRoleKey: AUTH_SERVICE_ROLE_KEY,
        // Exports and deletions finish within seconds.
        Privacy__PollSeconds: "1",
      },
    },
    {
      // The dev build proxies /api to the API (apps/web/proxy.conf.json), so the guest cookie is first-party.
      // It keeps the "Tuning (dev)" panel; the tests do not depend on it.
      command: `pnpm --filter @vybe/web dev --port ${PORT}${https ? " --ssl" : ""}`,
      url: WEB_URL,
      ignoreHTTPSErrors: https,
      reuseExistingServer: !isCI,
      timeout: 180_000,
      env: { E2E_API_PORT: String(API_PORT), E2E_AUTH_PORT: String(AUTH_PORT), E2E_WEB_HTTPS: https ? "1" : "0" },
    },
  ],
});
