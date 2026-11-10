# Runbook: browser matrix and visual baselines

- Owner: stream L. Spec §14: critical browser E2E, and visual regression across the supported browser and
  device matrix.
- Configuration: [`tests/e2e/playwright.config.ts`](../../tests/e2e/playwright.config.ts) (projects),
  [`tests/e2e/specs/visual.spec.ts`](../../tests/e2e/specs/visual.spec.ts) (screens),
  [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) (job `e2e`),
  [`.github/workflows/e2e-browsers.yml`](../../.github/workflows/e2e-browsers.yml).

## What runs where

| Projects | What | Pull request | Master push | Nightly (02:17 UTC) | Manual |
|---|---|---|---|---|---|
| `desktop-chromium`, `mobile-chromium` | All functional specs | yes (`ci.yml`) | yes (`ci.yml`) | no | no |
| `visual-desktop-chromium`, `visual-mobile-chromium` | Visual baselines | yes (`ci.yml`) | yes (`ci.yml`) | no | rewrite only |
| `desktop-firefox`, `desktop-webkit` | Critical journeys E01–E12 (tests whose title starts with the id) | only when the matrix changes | yes | yes | yes |
| `visual-desktop-firefox`, `visual-desktop-webkit`, `visual-mobile-webkit` | Visual baselines | only when the matrix changes | yes | yes | yes |

Why: a pull request keeps one engine, so its check time stays near the old time (the slow tests are the
60-second runs, and each engine runs them again). Chromium is the engine of most players. Firefox and WebKit
find engine problems after a merge and every night, before a release. "When the matrix changes" means a pull
request that changes the Playwright config, the visual spec, the baselines or `e2e-browsers.yml`.
Firefox has no mobile emulation, so the phone size runs in Chromium and WebKit only. The touch spec uses a
Chromium protocol (CDP) and runs in Chromium only.

`e2e-browsers.yml` serves the web app over HTTPS (`E2E_WEB_HTTPS=1`, a self-signed certificate of the Angular
dev server). WebKit does not keep a `Secure` cookie on `http://localhost` (Chromium and Firefox do), so without
HTTPS WebKit loses the guest cookie (`__Host-vybe_guest`) after `POST /guests`. Production is always HTTPS.

## Stable screenshots

- Each test makes a new guest with the default avatar; the catalog comes from the seed.
- The visual projects fix the locale (`en-US`), the time zone (UTC), the color scheme and reduced motion.
  CSS animations are off and the text caret is hidden.
- Masks (magenta in the baseline): the handle, the guest expiry date and the HUD values (clock and score).
  The game canvas is hidden in the HUD screenshot: the basket path depends on a random seed.
- Tolerance: at most 1 % of the pixels may differ (`maxDiffPixelRatio: 0.01`, color `threshold: 0.2`).
- The baselines are Linux screenshots (system fonts differ per OS). So the visual projects run only on Linux,
  or when `E2E_VISUAL=1` is set. On macOS and Windows, the local suite skips them.

## Update the baselines after an intended UI change

1. Push the branch of the change. In the pull request, the `e2e` job of `ci.yml` fails on the changed screens.
2. Open the failed run, download the `playwright-report` artifact, and examine the `*-diff.png` files.
   Make sure that each difference is intended.
3. Run the rewrite for all engines on your branch:
   ```bash
   gh workflow run e2e-browsers.yml --ref <your-branch> -f update_snapshots=true
   ```
4. When the run is complete, download the baselines into the repository:
   ```bash
   gh run download <run-id> --dir tests/e2e/specs/__screenshots__ --pattern 'visual-baselines-*'
   ```
   Each artifact has one folder per project. Move the folders so that the result is
   `tests/e2e/specs/__screenshots__/<project>/<screen>.png`, then delete the `visual-baselines-*` folders.
5. Examine the new images (`git diff --stat`, open them). Commit them in the same pull request.

A new screen: add a test to `visual.spec.ts`. The first run writes the missing baseline, fails, and uploads it
in the `visual-baselines-*` artifact (`e2e-browsers.yml`) or the `playwright-report` artifact (`ci.yml`).
Commit it like in step 4.

## Run the matrix locally

1. Install the engines once: `pnpm --filter @vybe/e2e exec playwright install firefox webkit`.
2. Run with your ports (see `docs/roadmap.md`), `E2E_BROWSERS=all` and `E2E_WEB_HTTPS=1`:
   ```bash
   E2E_BROWSERS=all E2E_WEB_HTTPS=1 pnpm --filter @vybe/e2e exec playwright test --project=desktop-firefox --project=desktop-webkit
   ```
   Stop a dev server that runs on the web port over HTTP first: Playwright reuses a running server locally.
The visual projects need Linux. On macOS, compare screenshots in CI, not locally.
