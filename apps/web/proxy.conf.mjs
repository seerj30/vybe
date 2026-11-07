// Dev-server proxy: /api goes to the local API, so the guest cookie is first-party.
// E2E_API_PORT lets parallel worktrees run their own API (tests/e2e/playwright.config.ts).
const apiPort = process.env["E2E_API_PORT"] ?? "5246";
// E2E_AUTH_PORT: the fake Supabase Auth of the browser tests (tests/e2e/fake-auth) serves the runtime
// config that turns sign-in on. Without it, /runtime-config.json is 404 and the app is guest-only.
const authPort = process.env["E2E_AUTH_PORT"];

export default {
  "/api": {
    target: `http://localhost:${apiPort}`,
    secure: false,
  },
  ...(authPort === undefined ? {} : { "/runtime-config.json": { target: `http://localhost:${authPort}`, secure: false, changeOrigin: true } }),
};
