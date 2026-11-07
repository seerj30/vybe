# ADR 0008: Fly.io deployment for the closed beta

- Status: accepted (configuration only; nobody has deployed yet)
- Date: 2026-11-07
- Spec: [v1.4 §13 and changelog item 7](../spec/VYBE_Technical_Specification_v1.3.md#v14-2026-11-07--signed-17-decisions);
  hosting: [decision 7](../decisions/07-hosting.md) (signed, option A; the product owner chose Fly.io on 2026-11-07);
  operations: [ADR 0006](0006-operations.md)
- Procedure: [deploy-and-rollback.md](../runbooks/deploy-and-rollback.md), section "Fly.io"

## Context

Decision 7 is signed: Supabase Pro (PostgreSQL and Auth), one always-on API container in the same region
(a second one before the social beta), the API through the transaction pooler, `/api` on the same origin
as the web app, a budget ceiling of USD 150 per month. The container host is Fly.io. ADR 0006 made the
image host-neutral: one image with commands, health probes, a fail-fast configuration check, and
forwarded headers only from known proxy networks (never `/0` without `TrustAnyHop`). The web app needs
the hosting to serve `/runtime-config.json` and the CSP header with `frame-ancestors 'none'`.

The region and the accounts do not exist yet. People create them and set the secrets; this ADR and its
files prepare everything else.

## Decision

1. **Two Fly apps.** `vybe-web` is public: Caddy serves the Angular build and proxies `/api/*`.
   `vybe-api` has **no public IP** (`fly deploy --no-public-ips`): only the web app reaches it, over the
   organization's private network (6PN) at `vybe-api.internal:8080`. Files: `infra/fly/api.fly.toml`,
   `infra/fly/web.fly.toml`, `infra/web/`.
2. **Direct 6PN, not Flycast.** Caddy resolves the AAAA records of `vybe-api.internal` every 5 s and
   spreads requests over the machines. Then the API's peer is always the Caddy machine (an `fdaa::/16`
   address), and Caddy sends one `X-Forwarded-For` value. With Flycast, the Fly proxy would be one more
   hop with an address that Fly does not document, and `ForwardLimit = 1` would read the wrong value.
   Caddy runs no active health checks on dynamic upstreams; a failed connect retries another machine
   (`lb_try_duration 10s`) and a failed machine is skipped for 30 s.
3. **Client address.** The Fly edge sets `Fly-Client-IP`. Caddy trusts that header only from private
   peers (`trusted_proxies static private_ranges`, `client_ip_headers Fly-Client-IP`) and replaces any
   `X-Forwarded-For` of the client with it. The API trusts forwarded headers only from `fdaa::/16`
   (`ReverseProxy__KnownNetworks__0`, private, not `/0`); after the first deploy, people narrow it to the
   organization's `/48`. A local test proved the chain: 11 guest creations with one `Fly-Client-IP` get
   429 on the 11th, also with a forged `X-Forwarded-For`; 11 with different values all get 201. The smoke
   test repeats it on Fly, because it depends on the edge replacing a forged `Fly-Client-IP`.
4. **Hosting headers.** Caddy adds, only when the response has none (`?`), so `/api` keeps the API's
   stricter values: the app's CSP (`apps/web/src/app/auth/content-security-policy.ts`) plus
   `frame-ancestors 'none'`, HSTS (`includeSubDomains`, like the API default), `Referrer-Policy:
   no-referrer`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`. `connect-src` gets the
   Supabase origin from the environment. Cache: hashed build files (`name-XXXXXXXX.ext`) are
   `public, max-age=31536000, immutable`; everything else is `no-cache`; `/runtime-config.json` is
   `no-store`. A missing `.js`/`.css` file is 404, not `index.html`. `/health/*` is 404 at the web origin.
   No access log: paths carry capabilities (invite tokens, deletion request ids).
5. **Runtime config.** `VYBE_SUPABASE_URL` and `VYBE_SUPABASE_ANON_KEY` are public values but differ per
   environment, so they are Fly secrets of the web app, not files in the image or the repository. The
   entrypoint checks their format (https origin; key characters `A-Za-z0-9._-`) and writes
   `/tmp/vybe-runtime/runtime-config.json`; a bad value stops the container. Without both, the file is
   404 and the app is guest-only (local and CI).
6. **One release command.** Fly runs one `release_command` before any machine changes, and a non-zero exit
   stops the deploy. A new `release` command of the API image runs `migrate`, then `seed`, in one process
   (`OpsCommands`), so the deploy order of ADR 0006 (migrate → seed → rollout) stays automatic.
7. **Owner credential on the API app.** The release machine has the app's secrets, so
   `ConnectionStrings__Migrations` is a secret of `vybe-api`, and the API machines also have it in their
   environment. ADR 0006 says not to give it to the API process. Reasons to accept it for the beta: the
   API code never reads it; the migration role is a dedicated `vybe_owner` that owns only schema `vybe`
   (not `postgres`); the other options (a separate Fly app for migrations started with `fly machine run`)
   need a wait-for-exit step that nobody can test without an account. Follow-up before the public
   launch: move the release step to its own app, so the API machines lose the owner credential.
8. **Always on, zero-downtime deploys.** API: `min_machines_running = 1`, `auto_stop_machines = "off"`
   (the background workers run in the API process), `shared-cpu-2x` with 1 GB. Web: one `shared-cpu-1x`
   with 256 MB, also always on. Both use `strategy = "bluegreen"`: the new machine starts next to the old
   one and gets traffic after its service check passes (API: `GET /health/ready`; the top-level
   `/health/live` check is for monitoring only, because Fly does not route by top-level checks).
   `kill_timeout = 35s` (ASP.NET Core waits 30 s for running requests). `--ha=false` keeps one machine;
   `fly scale count 2` adds the second before the social beta.
9. **Pipeline.** `.github/workflows/deploy.yml` runs only by hand or on a `v*` tag, never on pull
   requests, in the GitHub environment `production` (required reviewers). It checks the config (region
   set), installs a pinned flyctl with a SHA-256 check, builds both images with the commit SHA as the tag,
   pushes them to `registry.fly.io`, deploys the API, then the web app, then runs a smoke test. One deploy
   token per app (`FLY_API_TOKEN_API`, `FLY_API_TOKEN_WEB`), each only in the step that needs it. One
   deploy at a time (`concurrency`, never cancelled).
10. **CI check without a token.** `flyctl config validate` and even `flyctl config show --local` need a
    Fly.io access token, so pull requests cannot run them. `infra/fly/check-config.py` (Python standard
    library) refuses unknown keys and checks the rules above (port, always on, release command, health
    paths, private non-`/0` proxy network, no secret-like names in `[env]`, same region, matching
    `VYBE_API_HOST`). The `deploy-config` CI job runs it, builds both images, and starts the web image to
    check its headers and that `index.html` has no inline script.
11. **No inline script in the build.** Angular's critical-CSS inlining adds an inline `<script>` that loads
    the full stylesheet; `script-src 'self'` blocks it, so the page stayed without its full styles. The
    production build sets `optimization.styles.inlineCritical = false` (`apps/web/angular.json`). A CSP
    hash would break at each Angular update.
12. **Pinned images.** `node:22.23.4-alpine3.24` (build) and `caddy:2.11.9-alpine` (runtime), each by digest.
    Caddy 2.11.9 is two days old on this date, newer than the usual two-week wait, because 2.11.8 and 2.11.9
    have security fixes. Its binary has `cap_net_bind_service=p` only, so it runs as UID 10001 with all
    capabilities dropped. The container writes only to `/tmp`.

## Not done

- The region (`REGION-NOT-SET` in both files; the deploy workflow stops while it is not set) and every
  account, secret, DNS record and certificate: people do them (runbook, one-time setup).
- A caching CDN in front of the Fly edge. Decision 7 says "behind a CDN"; the Fly edge already ends TLS
  near the player, and hashed files are cached by browsers for a year. A CDN (for example Cloudflare)
  changes the client address chain: Caddy must then read the CDN's client header from the CDN's ranges
  only. Decide it with real LCP data (spec §13).
- `SSL Mode=Require` checks no server certificate. `VerifyFull` needs the Supabase root certificate in
  the image (follow-up).
- Staging and preview environments (spec §13): the files have one production environment for the beta.
  A staging copy is two more apps with the same files and another region value.
- The release step in its own app (decision 7 above).

## Consequences

- A deploy is one approved workflow run; a bad migration stops it before any machine changes.
- Rollback is `fly deploy --image` of the previous SHA; migrations stay (forward-only, ADR 0006).
- The local compose file runs the production web image on port 8081 in front of the API, so the proxy,
  headers and cache rules can be tried without Fly.io.
- Changing an app name or the region is a PR to both `fly.toml` files; CI checks that they match.
- Cost at beta size: about USD 45–100 per month without point-in-time recovery, about 145–200 with it
  (estimates in the runbook). The product owner decides point-in-time recovery before the social beta.
