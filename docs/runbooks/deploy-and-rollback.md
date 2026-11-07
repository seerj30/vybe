# Runbook: deploy and rollback

- Owner: stream D (operations). On-call names: see [incident-response.md](incident-response.md).
- Applies to: the API container (`services/api/Dockerfile`) on any container host, and the Fly.io
  deployment of [decision 7](../decisions/07-hosting.md) (signed: Supabase Pro + Fly.io, `/api` on the same
  origin as the web app). Fly.io procedure: section [Fly.io](#flyio-decision-7).
- Related: [migrations.md](migrations.md), [secret-rotation.md](secret-rotation.md), [ADR 0006](../adr/0006-operations.md),
  [ADR 0008](../adr/0008-fly-deployment.md).

## What a release contains

| Part | Command or artifact |
|---|---|
| Image | `docker build -f services/api/Dockerfile -t <registry>/vybe-api:<git-sha> .` (from the repository root) |
| Migration step | `docker run --rm <env> <image> migrate` |
| Seed step | `docker run --rm <env> <image> seed` (rulesets, catalog, quests; idempotent) |
| Release step | `docker run --rm <env> <image> release` (migrate, then seed, in one process: Fly.io `release_command`) |
| API | `<image>` with no command, port 8080 |

Tag every image with the git commit SHA. Do not deploy `latest`.

## Settings

Put every secret in the platform secret store (spec §13). Never put a secret in a file in this repository.
In every environment except `Development` and `Testing` (the integration tests), the API does not start
when a required setting is missing. The error lists the names of all missing settings (never their values).
The image sets `ASPNETCORE_ENVIRONMENT=Production`; set `Staging` on staging.

| Setting (environment variable) | Required | Secret | Notes |
|---|---|---|---|
| `ConnectionStrings__Vybe` | yes | yes | API role (owns no tables). Supavisor transaction pooler: add `Max Auto Prepare=0`. |
| `ConnectionStrings__Migrations` | for `migrate`/`seed` | yes | Owner role. Direct connection or session-mode pooler. Never the transaction pooler. Do not give it to the API process. |
| `Auth__Supabase__Authority` | yes | no | `https://<ref>.supabase.co/auth/v1` |
| `Auth__Supabase__ApiKey` | yes | no (publishable key) | Supabase publishable (anon) key. |
| `Challenges__InviteSecret` | yes | yes | Base64 of at least 32 random bytes: `openssl rand -base64 32`. |
| `Challenges__ShareBaseUrl` | yes | no | Web origin, absolute `https` URL. |
| `Storage__Exports__Bucket` | yes | no | Private bucket for J4 data exports. |
| `Storage__Exports__Region` | yes | no | Supabase: the project region. |
| `Storage__Exports__ServiceUrl` | for non-AWS stores | no | Supabase: `https://<ref>.storage.supabase.co/storage/v1/s3`. |
| `Storage__Exports__AccessKeyId`, `Storage__Exports__SecretAccessKey` | yes, unless the host gives a workload role | yes | Supabase: Storage S3 access keys (storage only, not the service role key). |
| `Content__Root` | set in the image | no | `/app/content`. |
| `Cors__AllowedOrigins__0` … | only for a cross-origin web app | no | Absolute `https` origins. Same-origin `/api` needs none. |
| `ReverseProxy__KnownNetworks__0` … | yes | no | CIDR of the proxy. See "Forwarded headers". `127.0.0.1/32` when there is no proxy. |
| `ReverseProxy__TrustAnyHop` | no (default `false`) | no | Must be `true` to accept a `/0` network. See "Forwarded headers". |
| `RateLimits__WritesPerPlayerPerMinute` | no (default 120) | no | Writes per player (per client address when not signed in). |
| `RateLimits__ReadsPerMinute` | no (default 600) | no | Reads per player (per client address when not signed in). `/health` is not limited. |
| `Security__HstsIncludeSubDomains` | no (default `true`) | no | `false` if another subdomain of the site is not on https. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | no | Turns on OTLP export of traces and metrics. Must be `https`; `http` only to a collector on localhost (sidecar). |
| `OTEL_EXPORTER_OTLP_HEADERS` | no | yes | Collector credentials, if the collector needs them. |
| `Sentry__Dsn` | no | yes | Turns on Sentry (errors only). Empty = off. |
| `Sentry__Release` | no | no | The image SHA, so Sentry groups errors by release. |
| `OTEL_SERVICE_NAME` | no (default `vybe-api`) | no | Service name on traces and metrics. |
| `Jobs__IdempotencyCleanupSeconds` | no (default 3600) | no | Interval of the idempotency-record cleanup. |
| `Jobs__DeletionLogSeconds` | no (default 300) | no | Interval of the copy of deletion requests to the deletion log ([backup-restore.md](backup-restore.md)). |
| `Jobs__Enabled` | no (default `true`) | no | `false` on instances that must not run the background workers. |

Platform settings for the container:

- At least one instance always on, with CPU outside requests. The background workers (outbox, sweeper,
  privacy, idempotency cleanup) run in the API process. A scale-to-zero host stops them.
- Read-only root filesystem: supported. Mount a writable `tmpfs` at `/tmp`.
- Run an init process as PID 1 if the host supports it (`docker run --init`, compose `init: true`).
- Liveness probe: `GET /health/live`. Readiness probe: `GET /health/ready` (database reachable and all
  migrations of this image applied). Do not route `/health/*` through the public edge.
- Stop timeout: 30 s or more, so running requests can complete.

### Forwarded headers

The guest rate limit and the logs use the client address. Behind a proxy, set
`ReverseProxy__KnownNetworks__<n>` to the CIDR ranges of the proxy. The API then reads
`X-Forwarded-For` and `X-Forwarded-Proto` only from those addresses. HSTS is sent only on https requests,
so `X-Forwarded-Proto` must come from a configured proxy.

A `/0` network (`0.0.0.0/0`, `::/0`) trusts every peer: any client that reaches the container can forge its
address and get a new rate-limit bucket for each request. The API does not start with a `/0` network unless
`ReverseProxy__TrustAnyHop=true`. Set it only if the host does not publish its proxy ranges **and** the
container is reachable only through the proxy (no public port). Prefer the published ranges.

## Deploy procedure

1. Make sure that CI on `master` is green for the commit.
2. Build and push the image with the commit SHA tag.
3. Read the migrations of the release (see [migrations.md](migrations.md)). Make sure that each one is an
   expand step that the running (old) image can use.
4. Run the migration step against the target environment:
   `docker run --rm -e ConnectionStrings__Migrations=<secret> <image> migrate`.
   Make sure that the exit code is 0 and the log line is "The database schema is up to date".
5. Run the seed step: `docker run --rm -e ConnectionStrings__Migrations=<secret> <image> seed`.
   Make sure that the exit code is 0. A changed ruleset with an existing id stops the seed: publish a new
   ruleset version instead.
6. Deploy the new image (rolling or blue/green). The new instances receive traffic only after
   `/health/ready` returns 200.
7. Smoke test through the public origin:
   - `POST /api/v1/guests` with `X-Requested-With: vybe-web` returns 201.
   - `GET /api/v1/catalog` with the guest cookie returns 200.
   - The response has `Strict-Transport-Security` and `Content-Security-Policy` headers.
8. Watch the dashboards for 30 minutes: 5xx rate, p95 latency, wallet errors, auth failures (spec §12).

Order for staging first: do steps 2–8 on staging, let it soak, then do steps 4–8 on production with the
same image.

## Rollback procedure

A rollback never reverts the database (spec §13). The schema is expand-contract, so the previous image
works with the new schema.

1. Redeploy the previous image tag (the last known good SHA).
2. Wait until `/health/ready` returns 200 on the old instances. The old image sees no pending migrations,
   because the new schema contains all of its migrations.
3. Do the smoke test of step 7.
4. Write down the bad SHA and the reason in the incident record.
5. Do not run `dotnet ef database update <older migration>` against production. If data is damaged, use
   [backup-restore.md](backup-restore.md).

If the previous image does not start on the new schema, the migration was not an expand step. Treat this
as an incident (SEV-2 or higher): roll forward with a fix, and add the missing compatibility to the
migration review.

## Fly.io (decision 7)

Decision 7 is signed: Supabase Pro (PostgreSQL and Auth) and an always-on API container on Fly.io in the
same region, with `/api` on the same origin as the web app. Design: [ADR 0008](../adr/0008-fly-deployment.md).
This section is the procedure for people. Nobody has created the accounts yet.

### What runs where

| Part | Where | File |
|---|---|---|
| Web app and `/api` proxy (Caddy, non-root) | Fly app `vybe-web`, public, TLS at the Fly edge | [`infra/web/`](../../infra/web/), [`infra/fly/web.fly.toml`](../../infra/fly/web.fly.toml) |
| API (one image: API, `release`, `seed`, `migrate`, `replay-deletions`) | Fly app `vybe-api`, **no public IP**, private network only | [`services/api/Dockerfile`](../../services/api/Dockerfile), [`infra/fly/api.fly.toml`](../../infra/fly/api.fly.toml) |
| PostgreSQL and Auth | Supabase Pro, same region | — |
| Export bucket (J4 exports, deletion log) | Supabase Storage, S3 protocol | — |
| Pipeline | GitHub Actions, environment `production` | [`.github/workflows/deploy.yml`](../../.github/workflows/deploy.yml) |

Request path: browser → Fly edge (TLS, sets `Fly-Client-IP`) → `vybe-web` (Caddy) → `vybe-api.internal:8080`
over the private network (6PN). Caddy sends the client address as the only `X-Forwarded-For` value. The API
reads it only from `fdaa::/16` (`ReverseProxy__KnownNetworks__0` in `api.fly.toml`).

### One-time setup

Do these steps in this order. Write each value into the password manager of the team, never into a file in
the repository, a ticket or a chat.

1. **Region.** The product owner and the engineering lead choose the Supabase region. Then choose the Fly
   region nearest to it. Measure the latency with the load test before the beta (decision 7: same region).

   | Supabase region | Fly region |
   |---|---|
   | `eu-central-1` (Frankfurt) — recommended if most players are in the EU | `fra` |
   | `eu-west-2` (London) | `lhr` |
   | `eu-west-3` (Paris) | `cdg` |
   | `us-east-1` (N. Virginia) | `iad` |

   Open a PR that sets `primary_region` in both `infra/fly/*.fly.toml` files. The deploy workflow stops
   while the value is `REGION-NOT-SET`.
2. **Accounts.** Create a Fly.io organization and a Supabase organization (Pro plan) with the company
   billing account. Turn on two-factor authentication for every member. Give access to the named on-call
   people only ([incident-response.md](incident-response.md)).
3. **Supabase project** in the chosen region:
   - Settings → JWT keys: turn on the asymmetric signing keys (ES256 or RS256). The API does not accept HS256.
   - Authentication → URL configuration: Site URL = the web origin; Redirect URLs = `<web origin>/`.
   - Data API: do **not** add schema `vybe` to the exposed schemas.
   - Compute: start with the smallest size; the load test decides ([load-test.md](load-test.md)).
4. **Database roles.** Connect with `psql` as `postgres` (session pooler, port 5432). Make the roles
   without a password in the SQL, then set each password with `\password <role>`, so the password is not
   in a history:
   ```sql
   CREATE ROLE vybe_owner LOGIN;            -- owns schema vybe; migrations and seed only
   GRANT CREATE ON DATABASE postgres TO vybe_owner;
   CREATE ROLE vybe_api LOGIN;              -- the API: data access only (infra/db/grants.sql)
   ```
   `vybe_owner` owns only the VYBE schema. Do not use `postgres` for migrations: the release machine has
   this credential (see "Owner credential on the API app" in ADR 0008).
5. **Connection strings** (Supavisor, IPv4; the user name has the form `role.<project ref>`):
   - `ConnectionStrings__Vybe` (transaction pooler, port 6543):
     `Host=<pooler host>;Port=6543;Database=postgres;Username=vybe_api.<ref>;Password=<password>;SSL Mode=Require;Max Auto Prepare=0;Maximum Pool Size=20`
   - `ConnectionStrings__Migrations` (session pooler, port 5432, never 6543):
     `Host=<pooler host>;Port=5432;Database=postgres;Username=vybe_owner.<ref>;Password=<password>;SSL Mode=Require`
   - With two API machines, the total pool (2 × 20) must stay below the pooler client limit of the compute size.
6. **Export bucket.** Supabase Storage → new **private** bucket (for example `vybe-exports`). Storage →
   S3 connection: write down the endpoint (`https://<ref>.storage.supabase.co/storage/v1/s3`) and the
   region, and create an S3 access key. Lifecycle rules: see [backup-restore.md](backup-restore.md)
   (`exports/` only; `deletion-log/` 90 days or more).
7. **Fly apps.** On a trusted machine with `flyctl` (`fly auth login`):
   ```bash
   fly apps create vybe-api --org <org>
   fly apps create vybe-web --org <org>
   ```
   If a name is taken, use another name and change `app` (and `VYBE_API_HOST` in `web.fly.toml`) in a PR.
8. **API secrets** (names only here). Write the `NAME=VALUE` lines in a file that only you can read,
   import it with `fly secrets import` (values go through stdin, not the command line or the shell
   history), then delete the file. `--stage` keeps them for the first deploy:
   ```bash
   umask 077; "${EDITOR:-vi}" /tmp/vybe-api.secrets
   fly secrets import --stage -a vybe-api < /tmp/vybe-api.secrets && rm /tmp/vybe-api.secrets
   ```
   | Name | Value |
   |---|---|
   | `ConnectionStrings__Vybe` | step 5, transaction pooler, `vybe_api` |
   | `ConnectionStrings__Migrations` | step 5, session pooler, `vybe_owner` |
   | `Auth__Supabase__Authority` | `https://<ref>.supabase.co/auth/v1` |
   | `Auth__Supabase__ApiKey` | the publishable (anon) key |
   | `Auth__Supabase__ServiceRoleKey` | the secret (service role) key; needed when stream J (J4 deletes the Supabase Auth user) is on `master` |
   | `Challenges__InviteSecret` | `openssl rand -base64 32` |
   | `Challenges__ShareBaseUrl` | `https://<web origin>` |
   | `Storage__Exports__Bucket`, `Storage__Exports__Region`, `Storage__Exports__ServiceUrl` | step 6 |
   | `Storage__Exports__AccessKeyId`, `Storage__Exports__SecretAccessKey` | step 6 |
   | `Sentry__Dsn`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` | optional |

   Check the names with `fly secrets list -a vybe-api` (names and digests only, never values). The
   settings table above describes every setting. `api.fly.toml` already sets `ASPNETCORE_ENVIRONMENT`,
   `ReverseProxy__KnownNetworks__0` and the Kestrel limit; the workflow sets `Sentry__Release`.
9. **Web settings** (public values, but per environment, so not in the repository):
   ```bash
   fly secrets set --stage -a vybe-web VYBE_SUPABASE_URL='https://<ref>.supabase.co' VYBE_SUPABASE_ANON_KEY='<publishable key>'
   ```
   The container writes `/runtime-config.json` from them and adds the origin to `connect-src`. A wrong
   format stops the container with a message. Without both, sign-in is off (guest-only).
10. **Deploy tokens and the GitHub environment.**
    ```bash
    fly tokens create deploy -a vybe-api -x 8760h   # -> GitHub secret FLY_API_TOKEN_API
    fly tokens create deploy -a vybe-web -x 8760h   # -> GitHub secret FLY_API_TOKEN_WEB
    ```
    GitHub → Settings → Environments → `production`: required reviewers (the engineering lead and the
    on-call primary), "Prevent self-review", deployment branches and tags = `master` and `v*` only.
    Add the two secrets to this environment only (not to the repository). Add the environment variable
    `VYBE_PUBLIC_ORIGIN` = `https://<web origin>`. Put the token expiry date in the secret-rotation record.
11. **Schema and grants before the first deploy.** The first API machine is ready only when the API role
    has its grants, and `grants.sql` needs the schema. So run the first migration from a trusted machine
    once. Build the image of the commit you will deploy, and keep the connection string in a file that
    only you can read:
    ```bash
    docker build -f services/api/Dockerfile -t vybe-api:setup .
    umask 077; printf 'ConnectionStrings__Migrations=%s\n' '<owner connection string>' > /tmp/vybe-owner.env
    docker run --rm --env-file /tmp/vybe-owner.env vybe-api:setup migrate
    rm /tmp/vybe-owner.env
    psql "<owner connection string>" -v ON_ERROR_STOP=1 -v api_role=vybe_api -f infra/db/grants.sql
    ```
    The grants also apply to tables of later migrations (default privileges of `vybe_owner`).
12. **Domain and certificates.** Deploy the web app first (below), then:
    ```bash
    fly ips allocate-v4 --shared -a vybe-web    # if the first deploy did not allocate IPs
    fly ips allocate-v6 -a vybe-web
    fly certs add <web domain> -a vybe-web
    ```
    Make the DNS records that `fly certs setup <web domain> -a vybe-web` shows (A/AAAA, or CNAME to
    `vybe-web.fly.dev`). Wait until `fly certs check <web domain> -a vybe-web` says the certificate is issued. Then set
    `Challenges__ShareBaseUrl`, the Supabase Site URL and Redirect URL, and `VYBE_PUBLIC_ORIGIN` to this
    origin. `Security__HstsIncludeSubDomains` is `true` by default: set it to `false` (API secret) and
    change the `Strict-Transport-Security` line in `infra/web/Caddyfile` if another subdomain is not on https.

### First deploy

1. Make sure that steps 1–11 are done and that CI on `master` is green.
2. GitHub → Actions → Deploy → Run workflow on `master`. A reviewer approves the `production` environment.
3. The workflow builds both images (tag = commit SHA) and pushes them to `registry.fly.io`. Then it deploys
   the API: the release machine runs `release` (migrate, then seed). The new API machine gets traffic only
   after `GET /health/ready` passes. Then it deploys the web app and runs a smoke test.
4. Make sure that the API has no public address: `fly ips list -a vybe-api` shows nothing (or only a
   private address). Release any public address: `fly ips release <address> -a vybe-api`.
5. After the first deploy, narrow the proxy network: `fly ips private -a vybe-web` shows the private
   addresses of the web machines (`fdaa:x:y:…`). The first three groups are the organization prefix: set
   `ReverseProxy__KnownNetworks__0` in `api.fly.toml` to `fdaa:x:y::/48`, in a PR.

### Seed and other one-off commands

`release` seeds on every deploy. For a one-off command, run the deployed image on a temporary machine of
the API app. It gets the secrets of the app:

```bash
fly releases -a vybe-api --image          # the image of the current release
fly machine run registry.fly.io/vybe-api:<sha> seed -a vybe-api --region <region> \
  --rm --restart no --vm-size shared-cpu-1x
fly logs -a vybe-api                      # "Seeded …" and the exit; non-zero = failed
```

Use the same form for `migrate` and, after a restore, `replay-deletions` ([backup-restore.md](backup-restore.md)).
The temporary machine does not listen on port 8080; the web proxy skips it.

### Smoke test

The workflow checks `/` (CSP with `frame-ancestors 'none'`, HSTS), `/api/v1/catalog/items` and that
`/health/ready` is not public. After each production deploy, also do these checks by hand:

1. `fly status -a vybe-api` and `fly checks list -a vybe-api`: every machine is started, both checks pass.
2. Open the web origin in a private browser window. The landing page shows with its styles. The browser
   console shows no CSP error.
3. Guest with the age gate: "Start as guest", give a birth year of an adult. The avatar page opens.
   Without stream J on `master`, there is no age gate yet. By command:
   `curl -si -X POST <origin>/api/v1/guests -H 'X-Requested-With: vybe-web' -H 'Content-Type: application/json' -d '{"birthYear":1990}'`
   gives 201 and `Set-Cookie: __Host-vybe_guest`.
4. Profile: save the avatar, open `/hub`; open your own profile; the level and wallet show.
5. Sign-in: create an account with a test email, confirm it, link the guest ("Link this account"). The
   player id stays the same in `/settings`. Delete the test account afterwards in `/settings`.
6. Client address: send 11 `POST /api/v1/guests` (the command of step 3) within one minute from one
   machine, each with another forged `Fly-Client-IP` header. The 11th answer must be 429: the Fly edge
   replaces the header. If all are 201, stop and treat it as a SEV-2 (the guest rate limit does not work).
   The test guests expire like other unused guests.

### Rollback on Fly.io

A rollback never reverts the database (see "Rollback procedure" above). Migrations are forward-only.

1. Find the last good image: `fly releases -a vybe-api --image` (and `-a vybe-web`).
2. Deploy it again without a build. The release command runs again: `migrate` finds nothing to do (the
   schema is newer; that is correct for expand-contract) and `seed` is idempotent:
   ```bash
   fly deploy -c infra/fly/api.fly.toml -a vybe-api --image registry.fly.io/vybe-api:<good sha> --no-public-ips --ha=false
   fly deploy -c infra/fly/web.fly.toml -a vybe-web --image registry.fly.io/vybe-web:<good sha> --ha=false
   ```
   Use the `fly.toml` files of the good commit if they changed (`git show <sha>:infra/fly/api.fly.toml`).
3. If the old image does not start on the new schema, the migration was not an expand step: roll forward
   with a fix (SEV-2 or higher). Never run a down migration against production.
4. If the seed of the old image fails ("ruleset … changed after activation"), the new release published a
   changed ruleset under an existing id. Roll forward: a ruleset change needs a new ruleset version.
5. A web-only problem: roll back `vybe-web` only. The web app and the API are compatible across one release.

### Scaling before the social beta

Decision 7 asks for a second API machine before the social beta (availability target 99.5 %):

```bash
fly scale count 2 -a vybe-api --region <region>
fly scale count 2 -a vybe-web --region <region>
```

- The background workers are safe on many machines (`SKIP LOCKED`, compare-and-swap writes).
- Caddy reads the AAAA records of `vybe-api.internal` every 5 s and spreads requests; a stopped machine is
  skipped for 30 s and a failed connect tries another machine.
- Check that 2 × `Maximum Pool Size` stays below the pooler client limit.
- Bigger machines: `fly scale vm shared-cpu-4x -a vybe-api` or `fly scale memory 2048 -a vybe-api`,
  then change `[[vm]]` in `api.fly.toml` in a PR, so the next deploy keeps the size.
- Run the load test against the beta environment again after a change ([load-test.md](load-test.md)).

### Cost estimate (USD per month, estimates of 2026-11-07)

These are estimates from the vendors' public price pages as remembered on 2026-11-07, not quotes. Check
each price page before the budget review. Ceiling: USD 150 per month (decision 7).

| Component | Size | Estimate |
|---|---|---|
| Supabase Pro plan | base fee, includes Micro compute credit | 25 |
| Supabase compute | Small (if the load test needs more than Micro) | 0–15 |
| Supabase point-in-time recovery (7 days) | add-on; needs Small compute or bigger | 100 (optional, see below) |
| Fly API machine | 1 × `shared-cpu-2x`, 1 GB, always on | 7–12 |
| Fly API, second machine (social beta) | 1 × `shared-cpu-2x`, 1 GB | 7–12 |
| Fly web machine | 1 × `shared-cpu-1x`, 256 MB (2 at the social beta) | 2–4 |
| Fly outbound data | about 100 GB in EU/North America | 2 |
| Fly IPv4 | shared IPv4 for http: free; dedicated IPv4 only if needed | 0–2 |
| Fly certificate | one hostname | 0–1 |
| Domain | annual fee per month | 1–2 |
| Sentry | Developer (free) or Team | 0–26 |
| OTLP backend | a free tier (optional) | 0 |
| **Total without point-in-time recovery** | | **about 45–100** |
| **Total with point-in-time recovery** | | **about 145–200** |

Point-in-time recovery is in the backup runbook as "use before the social beta". With it, the budget stays
under USD 150 only with Sentry Developer and without a bigger compute size. The product owner decides this
before the social beta.

## Local run

```bash
docker compose -f infra/docker-compose.yml up --build
```

The compose file runs `db`, then `migrate`, then `seed`, then the API on `http://localhost:8080` in the
Development environment, and the production web image (`infra/web`) on `http://localhost:8081`, which
serves the app and proxies `/api` to the API like Fly.io.
