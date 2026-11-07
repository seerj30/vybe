# ADR 0008: Fly.io deployment for the closed beta

- Status: accepted (configuration only; nobody has deployed yet)
- Date: 2026-11-07 (review fixes the same day)
- Spec: [v1.4 §13 and changelog item 7](../spec/VYBE_Technical_Specification_v1.3.md#v14-2026-11-07--signed-17-decisions);
  hosting: [decision 7](../decisions/07-hosting.md) (signed, option A; the product owner chose Fly.io on 2026-11-07);
  operations: [ADR 0006](0006-operations.md)
- Procedure: [deploy-and-rollback.md](../runbooks/deploy-and-rollback.md), section "Fly.io"

## Context

Decision 7 is signed: Supabase Pro (PostgreSQL and Auth), one always-on API container in the same region
(a second one before the social beta), the API through the transaction pooler, `/api` on the same origin
as the web app, a budget ceiling of USD 150 per month. The container host is Fly.io. ADR 0006 made the
image host-neutral: one image with commands, health probes, a fail-fast configuration check, forwarded
headers only from known proxy networks (never `/0` without `TrustAnyHop`), and the owner credential only
for the migration step, never for the API process. The web app needs the hosting to serve
`/runtime-config.json` and the CSP header with `frame-ancestors 'none'`.

The region and the accounts do not exist yet. People create them and set the secrets; this ADR and its
files prepare everything else.

## Decision

1. **Two Fly apps in a dedicated organization.** `vybe-web` is public: Caddy serves the Angular build and
   proxies `/api` and `/api/*`. `vybe-api` has **no public IP**: the workflow deploys it with
   `--no-public-ips`, then fails when `fly ips list` shows any address that is not private or when
   `https://<api app>.fly.dev` answers. Only the web app reaches it, over the private network (6PN) at
   `vybe-api.internal:8080`. The Fly organization holds only these two apps, because every machine of an
   organization shares its private network.
2. **Direct 6PN, not Flycast.** Caddy resolves the AAAA records of `vybe-api.internal` every 5 s and
   spreads requests over the machines. Then the API's peer is always a Caddy machine, and Caddy sends one
   `X-Forwarded-For` value. With Flycast, the Fly proxy would be one more hop with an address that Fly
   does not document, and `ForwardLimit = 1` would read the wrong value. Caddy runs no active health checks
   on dynamic upstreams; a failed connect retries another machine (`lb_try_duration 10s`), and a failed
   machine is skipped for 30 s. Timeouts: dial 3 s, response header 30 s; client header 10 s, body 30 s,
   idle 2 min; request bodies over 64 KB stop at Caddy (the API's own limit is 16 KB).
3. **Client address.** The Fly edge sets `Fly-Client-IP`. Caddy trusts that header only from private
   peers (`trusted_proxies static private_ranges`, `client_ip_headers Fly-Client-IP`) and replaces any
   `X-Forwarded-For` of the client with it. The API trusts forwarded headers only from the organization's
   `/48` (`ReverseProxy__KnownNetworks__0`). The prefix is known only after the first machines exist, so
   the first deploy (workflow input `first_deploy`) may use `fdaa::/16`; every later deploy fails
   (`check-config.py --require-region`) until a PR sets the `/48`. A CI job puts the web image in front of a
   stub upstream and checks that a forged `X-Forwarded-For` never arrives. Locally, 11 guest creations with
   one `Fly-Client-IP` get 429 on the 11th, also with a forged `X-Forwarded-For`. The runbook smoke test
   repeats it on Fly, because it depends on the edge replacing a forged `Fly-Client-IP`.
4. **Hosting headers and cache.** Caddy adds, only when the response has none (`?`), so `/api` keeps the
   API's stricter values: the app's CSP (`apps/web/src/app/auth/content-security-policy.ts`) plus
   `frame-ancestors 'none'`, HSTS (`includeSubDomains`, like the API default), `Referrer-Policy:
   no-referrer`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`; it removes `Server` and `Via`.
   `connect-src` gets the Supabase origin from the environment. Hashed build files (`name-XXXXXXXX.ext`) are
   `public, max-age=31536000, immutable`; everything else is `no-cache`; `/runtime-config.json` is
   `no-store`. A missing path with a file extension is 404, not `index.html` (client routes have no
   extension). `/health` and `/health/*` are 404 at the web origin.
5. **Logs without capabilities.** No access log. The warning and error log uses a `filter` encoder that
   deletes the request URI, headers and client address: paths carry capabilities (invite tokens, deletion
   request ids), headers carry cookies and tokens. A local proxy error showed method and host only.
6. **The release step runs in the pipeline, not on Fly.** A Fly `release_command` machine gets every app
   secret, so the owner credential would also be on every API machine (against ADR 0006 #2). So the
   workflow builds the image and runs `release` (a new command: `migrate`, then `seed`, in one process) on
   the GitHub runner, with `ConnectionStrings__Migrations` from the `production` environment secret
   `VYBE_MIGRATIONS_CONNECTION_STRING`, passed by name (`docker run -e NAME`), never as an argument. Only
   then it runs `fly deploy`. The migration role is a dedicated `vybe_owner` that owns only schema `vybe`.
   Mode `release-only` runs this step alone: the first-deploy dry run (it proves that the chiseled
   ENTRYPOINT passes the argument) and one-off `migrate`/`seed`.
7. **Deploys only reviewed, tested commits, by digest.** Triggers: by hand or a `v*` tag; never pull
   requests. The job fails unless the commit is on `master` (`git merge-base --is-ancestor`, full
   history) and has a successful `ci.yml` run. It runs in the GitHub environment `production` (required
   reviewers). It deploys the digest from the push, not the tag, and writes both digests in the run summary.
   One deploy token per app (`FLY_API_TOKEN_API`, `FLY_API_TOKEN_WEB`), each only in the steps that need
   it. flyctl comes from the release tarball, checked by SHA-256. One deploy at a time, never cancelled.
8. **Image-only rollback.** Mode `rollback` deploys two given digests (checked against the app names)
   without a build and without the release step. The seed of an older image can fail on content that a
   newer release published, and its migrate has nothing to do (forward-only). Tested locally: the image of
   b1487eb became ready on the schema and content of the current image; its seed failed. When the old image
   cannot run on the new schema or content, the fix is a roll-forward.
9. **Runtime config.** `VYBE_SUPABASE_URL` and `VYBE_SUPABASE_ANON_KEY` are public values but differ per
   environment, so they are Fly secrets of the web app, not files in the image or the repository. The
   entrypoint checks the whole value (a `case` pattern, so a second line cannot pass) and writes
   `/tmp/vybe-runtime/runtime-config.json`; a bad value stops the container. Without both, the file is 404
   and the app is guest-only (local and CI).
10. **Always on, zero-downtime deploys.** API: `min_machines_running = 1`, `auto_stop_machines = "off"`
    (the background workers run in the API process), `shared-cpu-2x` with 1 GB. Web: one `shared-cpu-1x`
    with 256 MB, also always on. Both use `strategy = "bluegreen"`: the new machine starts next to the old
    one and gets traffic after its service check passes (API: `GET /health/ready`; the top-level
    `/health/live` check is for monitoring only, because Fly does not route by top-level checks).
    `kill_timeout = 35s` (ASP.NET Core waits 30 s for running requests). `--ha=false` keeps one machine;
    `fly scale count 2` adds the second before the social beta.
11. **CI check without a token.** `flyctl config validate` and even `flyctl config show --local` need a
    Fly.io access token, so pull requests cannot run them. `infra/fly/check-config.py` (Python standard
    library) refuses unknown keys (so also a `release_command`) and checks the rules above. The
    `deploy-config` CI job runs it, builds both images, and starts the web image in front of the stub
    upstream (headers, 404s, body limit, forwarded address, no inline script).
12. **No inline script in the build.** Angular's critical-CSS inlining adds an inline `<script>` that loads
    the full stylesheet; `script-src 'self'` blocks it, so the page stayed without its full styles. The
    production build sets `optimization.styles.inlineCritical = false` (`apps/web/angular.json`). A CSP
    hash would break at each Angular update.
13. **Pinned images.** Every base image is pinned by digest, with a comment on how to update it:
    `node:22.23.4-alpine3.24`, `caddy:2.11.9-alpine`, and the .NET `sdk:10.0-noble` and
    `aspnet:10.0-noble-chiseled-extra` images. Caddy 2.11.9 is two days old on this date, newer than the usual
    two-week wait, because 2.11.8 and 2.11.9 have security fixes. Its binary has
    `cap_net_bind_service=p` only, so it runs as UID 10001 with all capabilities dropped. The container
    writes only to `/tmp`.

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
- GitHub-hosted runners have no fixed address: with Supabase network restrictions on, the release step
  needs a self-hosted runner with a fixed address.

## Consequences

- A deploy is one approved workflow run of a commit on `master` with green CI; a failed migration stops
  it before any machine changes.
- No Fly machine has the owner credential. One-off `migrate` and `seed` go through the workflow
  (`release-only`); `replay-deletions` uses the API's own settings and can run on a temporary Fly machine.
- A rollback is the `rollback` mode with the digests of a good run; migrations stay (forward-only).
- The local compose file runs the production web image on port 8081 in front of the API, so the proxy,
  headers and cache rules can be tried without Fly.io.
- Changing an app name or the region is a PR to both `fly.toml` files; CI checks that they match.
- Cost at beta size: about USD 45–100 per month without point-in-time recovery, about 145–200 with it
  (estimates in the runbook). The product owner decides point-in-time recovery before the social beta.
