// Dev-server proxy: /api goes to the local API, so the guest cookie is first-party.
// E2E_API_PORT lets parallel worktrees run their own API (tests/e2e/playwright.config.ts).
const apiPort = process.env["E2E_API_PORT"] ?? "5246";

export default {
  "/api": {
    target: `http://localhost:${apiPort}`,
    secure: false,
  },
};
