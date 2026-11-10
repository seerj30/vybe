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
| Release step | `docker run --rm -e ConnectionStrings__Migrations <image> release` (migrate, then seed, in one process; on Fly.io the deploy workflow runs it on the runner) |
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
| `Auth__Supabase__ServiceRoleKey` | yes | yes | Supabase service-role (secret) key. The API uses it only for the Auth admin API: J4 deletes the Supabase Auth user (spec §17 decision 8). Give it only to the API process. Never put it in the web app, a log or a ticket. |
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
   The first deploy must run the migrations **before the first player exists**: the `AccountPolicies`
   migration adds the age-gate columns, and players created before it have no gate result (ADR 0007).
5. Run the seed step: `docker run --rm -e ConnectionStrings__Migrations=<secret> <image> seed`.
   Make sure that the exit code is 0. A changed ruleset with an existing id stops the seed: publish a new
   ruleset version instead.
6. Deploy the new image (rolling or blue/green). The new instances receive traffic only after
   `/health/ready` returns 200.
7. Smoke test through the public origin:
   - `POST /api/v1/guests` with `X-Requested-With: vybe-web` and the body `{"birthYear":1990,"birthMonth":6}`
     returns 201.
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

Rules for every step with a secret:

- Never put a password, key or token in a command argument, a connection string on the command line, a
  file in `/tmp`, a ticket or a chat. Command arguments go to the shell history and the process list.
- Paste a secret only at a prompt that reads stdin (`fly secrets import`, `gh secret set`, `read -rs`,
  `\password` in psql), or let one command pipe it to the next.
- For `psql`, use a service file and a password file (mode 600), as in [backup-restore.md](backup-restore.md).
- If a step needs a temporary file, make a private directory first: `d=$(mktemp -d)` (mode 700), and
  delete it afterwards: `rm -rf "$d"`.

### What runs where

| Part | Where | File |
|---|---|---|
| Web app and `/api` proxy (Caddy, non-root) | Fly app `vybe-prod-web`, public, TLS at the Fly edge | [`infra/web/`](../../infra/web/), [`infra/fly/web.fly.toml`](../../infra/fly/web.fly.toml) |
| API (one image: API, `release`, `seed`, `migrate`, `replay-deletions`) | Fly app `vybe-prod-api`, **no public IP**, private network only | [`services/api/Dockerfile`](../../services/api/Dockerfile), [`infra/fly/api.fly.toml`](../../infra/fly/api.fly.toml) |
| Release step (`release` = migrate, then seed, owner role) | The GitHub Actions runner, before `fly deploy` | [`.github/workflows/deploy.yml`](../../.github/workflows/deploy.yml) |
| PostgreSQL and Auth | Supabase Pro, same region | — |
| Export bucket (J4 exports, deletion log) | Supabase Storage, S3 protocol | — |

Request path: browser → Fly edge (TLS, sets `Fly-Client-IP`) → `vybe-web` (Caddy) → `vybe-prod-api.internal:8080`
over the private network (6PN). Caddy sends the client address as the only `X-Forwarded-For` value. The API
reads it only from the organization's private prefix (`ReverseProxy__KnownNetworks__0` in `api.fly.toml`).

The owner credential (`ConnectionStrings__Migrations`) is a secret of the GitHub environment `production`
only. It is **not** a Fly secret: a Fly release machine and every API machine would get it.

### One-time setup

Do these steps in this order. Keep each value in the password manager of the team.

1. **Region.** The product owner and the engineering lead choose the Supabase region. Then choose the Fly
   region nearest to it. Measure the latency with the load test before the beta (decision 7: same region).

   | Supabase region | Fly region |
   |---|---|
   | `eu-central-1` (Frankfurt). Recommended if most players are in the EU | `fra` |
   | `eu-west-2` (London) | `lhr` |
   | `eu-west-3` (Paris) | `cdg` |
   | `us-east-1` (N. Virginia) | `iad` |

   Open a PR that sets `primary_region` in both `infra/fly/*.fly.toml` files. The deploy workflow stops
   while the value is `REGION-NOT-SET`.
2. **Accounts.** Create a **dedicated** Fly.io organization for VYBE production, with no other apps.
   Every machine of the organization shares its private network: a machine there can reach the API and
   can send a `Fly-Client-IP` to the web app. Create a Supabase organization (Pro plan). Use the company
   billing account. Turn on two-factor authentication for every member. Give access to the named on-call
   people only ([incident-response.md](incident-response.md)).
3. **Supabase project** in the chosen region:
   - Settings → JWT keys: turn on the asymmetric signing keys (ES256 or RS256). The API does not accept HS256.
   - Authentication → URL configuration: Site URL = the web origin; Redirect URLs = `<web origin>/`.
   - Data API: do **not** add schema `vybe` to the exposed schemas.
   - Compute: start with the smallest size; the load test decides ([load-test.md](load-test.md)).
   - Network restrictions: if you turn them on, the release step runs on GitHub-hosted runners, which have
     no fixed address. Keep the pooler open to them, or use a self-hosted runner with a fixed address.
4. **Database roles.** Connect with `psql` as `postgres` through a service file and a password file. Make
   the roles without a password, then set each password at the `\password` prompt:
   ```sql
   CREATE ROLE vybe_owner LOGIN;            -- owns schema vybe; migrations and seed only
   GRANT CREATE ON DATABASE postgres TO vybe_owner;
   CREATE ROLE vybe_api LOGIN;              -- the API: data access only (infra/db/grants.sql)
   \password vybe_owner
   \password vybe_api
   ```
5. **Connection strings** (Supavisor, IPv4; the user name has the form `role.<project ref>`). Make them in
   the password manager:
   - API, transaction pooler, port 6543:
     `Host=<pooler host>;Port=6543;Database=postgres;Username=vybe_api.<ref>;Password=<password>;SSL Mode=Require;Max Auto Prepare=0;Maximum Pool Size=20`
   - Owner, session pooler, port 5432, never 6543:
     `Host=<pooler host>;Port=5432;Database=postgres;Username=vybe_owner.<ref>;Password=<password>;SSL Mode=Require`
   - With two API machines, the total pool (2 × 20) must stay below the pooler client limit of the compute size.
6. **Export bucket.** Supabase Storage → new **private** bucket (for example `vybe-exports`). Storage →
   S3 connection: write down the endpoint (`https://<ref>.storage.supabase.co/storage/v1/s3`) and the
   region, and create an S3 access key. Lifecycle rules: see [backup-restore.md](backup-restore.md)
   (`exports/` only; `deletion-log/` 90 days or more).
7. **Fly apps.** On a trusted machine with `flyctl` (`fly auth login`):
   ```bash
   fly apps create vybe-prod-api --org vybe-prod
   fly apps create vybe-prod-web --org vybe-prod
   ```
   If a name is taken, use another name and change `app` (and `VYBE_API_HOST` in `web.fly.toml`) in a PR.
8. **API secrets.** Run the command, paste the `NAME=VALUE` lines from the password manager, then press
   Ctrl-D. The values go through stdin only. `--stage` keeps them for the first deploy.
   ```bash
   fly secrets import --stage -a vybe-prod-api
   ```
   | Name | Value |
   |---|---|
   | `ConnectionStrings__Vybe` | step 5, API (transaction pooler, `vybe_api`) |
   | `Auth__Supabase__Authority` | `https://<ref>.supabase.co/auth/v1` |
   | `Auth__Supabase__ApiKey` | the publishable (anon) key |
   | `Auth__Supabase__ServiceRoleKey` | the secret (service role) key. Needed when stream J (J4 deletes the Supabase Auth user) is on `master` |
   | `Challenges__InviteSecret` | 32 random bytes in base64 (`openssl rand -base64 32`, pasted, not typed into an argument) |
   | `Challenges__ShareBaseUrl` | `https://<web origin>` |
   | `Storage__Exports__Bucket`, `Storage__Exports__Region`, `Storage__Exports__ServiceUrl` | step 6 |
   | `Storage__Exports__AccessKeyId`, `Storage__Exports__SecretAccessKey` | step 6 |
   | `Sentry__Dsn`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` | optional |

   Do **not** set `ConnectionStrings__Migrations` on Fly (step 10). Check the names with
   `fly secrets list -a vybe-prod-api` (names and digests only, never values). The settings table above describes
   every setting. `api.fly.toml` sets `ASPNETCORE_ENVIRONMENT`, `ReverseProxy__KnownNetworks__0` and the
   Kestrel limit. The workflow sets `Sentry__Release`.
9. **Web settings** (public values, but per environment, so not in the repository). Same way:
   `fly secrets import --stage -a vybe-prod-web` with `VYBE_SUPABASE_URL=https://<ref>.supabase.co` and
   `VYBE_SUPABASE_ANON_KEY=<publishable key>`. The container writes `/runtime-config.json` from them and adds
   the origin to `connect-src`. A wrong format stops the container with a message. Without both, sign-in
   is off (guest-only).
10. **GitHub environment `production`** (Settings → Environments):
    - Required reviewers: the engineering lead and the on-call primary; "Prevent self-review" on.
    - Deployment branches and tags: `master` and `v*` only.
    - Variable `VYBE_PUBLIC_ORIGIN` = `https://<web origin>`.
    - Secrets (environment only, not repository secrets). The pipes keep the values off the screen:
      ```bash
      fly tokens create deploy -a vybe-prod-api -x 8760h | gh secret set FLY_API_TOKEN_API --env production -R seerj30/vybe
      fly tokens create deploy -a vybe-prod-web -x 8760h | gh secret set FLY_API_TOKEN_WEB --env production -R seerj30/vybe
      gh secret set VYBE_MIGRATIONS_CONNECTION_STRING --env production -R seerj30/vybe   # paste the owner string (step 5)
      ```
    - Write the token expiry date in the record of [secret-rotation.md](secret-rotation.md).
11. **Dry run: release step only.** GitHub → Actions → Deploy → Run workflow on `master`, mode
    `release-only`. A reviewer approves. The run builds the API image and runs `release` on the runner. This
    proves that the chiseled ENTRYPOINT passes the argument (`dotnet Vybe.Api.dll release`), that the owner
    string works, and it creates the schema. The log must show "The database schema is up to date" and
    "Seeded … rulesets, … catalog items".
12. **Grants.** The API machine is ready only when the API role has its grants. As the owner role, with
    the service `vybe-owner` in `~/.pg_service.conf` and the password in `~/.pgpass` (both mode 600):
    ```bash
    PGSERVICE=vybe-owner psql -v ON_ERROR_STOP=1 -v api_role=vybe_api -f infra/db/grants.sql
    ```
    The grants also apply to tables of later migrations (default privileges of `vybe_owner`).

### First deploy

1. Make sure that steps 1–12 are done and that CI on `master` is green.
2. Deploy → Run workflow on `master`, mode `deploy`, **`first_deploy` on**. A reviewer approves.
3. The workflow:
   1. checks that the commit is on `master` and has a green CI run;
   2. builds both images and runs `release` on the runner (owner role from the environment secret);
   3. pushes both images and deploys **by digest** (the run summary lists both digests: keep them for a
      rollback);
   4. deploys the API with `--no-public-ips`. The new machine gets traffic only after `GET /health/ready`
      passes (bluegreen);
   5. fails if the API app has a public IP or if `https://vybe-prod-api.fly.dev` answers;
   6. deploys the web app, then does the smoke test.
4. Domain and certificates:
   ```bash
   fly ips allocate-v4 --shared -a vybe-prod-web    # if the first deploy did not allocate IPs
   fly ips allocate-v6 -a vybe-prod-web
   fly certs add <web domain> -a vybe-prod-web
   fly certs setup <web domain> -a vybe-prod-web    # the DNS records to make
   fly certs check <web domain> -a vybe-prod-web    # repeat until the certificate is issued
   ```
   Then set `Challenges__ShareBaseUrl` (API secret), the Supabase Site URL and Redirect URL, and
   `VYBE_PUBLIC_ORIGIN` to this origin. `Security__HstsIncludeSubDomains` is `true` by default: set it to
   `false` (API secret) and change the `Strict-Transport-Security` line in `infra/web/Caddyfile` if another
   subdomain is not on https.
5. **Narrow the proxy network (required).** `fly ips private -a vybe-prod-web` shows the private addresses of
   the web machines (`fdaa:x:y:…`). The first three groups are the organization prefix. Open a PR that sets
   `ReverseProxy__KnownNetworks__0` in `api.fly.toml` to `fdaa:x:y::/48`. Until then, every deploy without
   `first_deploy` fails (`check-config.py --require-region` refuses a prefix shorter than /48). Deploy the
   PR normally (without `first_deploy`).

### One-off commands

| Command | How |
|---|---|
| `migrate`, `seed`, `release` | Deploy workflow, mode `release-only` (owner role from the GitHub environment). Never on Fly: the API app has no owner credential. |
| `replay-deletions` (after a restore) | It uses the API role and the bucket, which are Fly secrets, so a temporary machine of the API app can run it (below). |

```bash
fly releases -a vybe-prod-api --image          # the image digest of the current release
fly machine run registry.fly.io/vybe-prod-api@sha256:<digest> replay-deletions -a vybe-prod-api --region <region> \
  --rm --restart no --vm-size shared-cpu-1x
fly logs -a vybe-prod-api                      # "Deletion replay: …" and the exit; non-zero = failed
```

The temporary machine runs the same image with the same secrets as the API, in the same dedicated
organization. It does not listen on port 8080, so the web proxy skips it.

### Smoke test

The workflow checks `/` (CSP with `frame-ancestors 'none'`, HSTS), `/api/v1/catalog/items`, that
`/health/ready` is not public and that the API has no public address. After each production deploy, also do
these checks by hand:

1. `fly status -a vybe-prod-api` and `fly checks list -a vybe-prod-api`: every machine is started, both checks pass.
2. Open the web origin in a private browser window. The landing page shows with its styles. The browser
   console shows no CSP error.
3. Guest with the age gate: "Start as guest", give the birth month and year of an adult. The avatar page
   opens. By command: `POST <origin>/api/v1/guests` with `X-Requested-With: vybe-web` and the age-gate body
   of the contract (`packages/contracts`) gives 201 and `Set-Cookie: __Host-vybe_guest`.
4. Profile: save the avatar, open `/hub`, open your own profile; the level and wallet show.
5. Sign-in: create an account with a test email, confirm it, link the guest ("Link this account"). The
   player id stays the same in `/settings`. Delete the test account afterwards in `/settings`.
6. Client address: send 11 guest creations (step 3) within one minute from one machine, each with another
   forged `Fly-Client-IP` header (with two API machines: 21, see "Scaling before the social beta"). At least
   one answer must be 429: the Fly edge replaces the header. If all are 201, stop and treat it as a SEV-2 (the guest rate limit does not work). The test guests expire like
   other unused guests.

### Rollback on Fly.io

A rollback never reverts the database (see "Rollback procedure" above). Migrations are forward-only, and a
rollback does **not** run the release step: the seed of an older image can fail on content that a newer
release published ("ruleset … changed after activation"), and its migrate has nothing to do.

1. Find the digests of the last good release: the summary of its Deploy run, or
   `fly releases -a vybe-prod-api --image` and `fly releases -a vybe-prod-web --image`.
2. Deploy → Run workflow on `master`, mode `rollback`, with `api_image` and `web_image` =
   `registry.fly.io/<app>@sha256:<digest>`. The run deploys these images without a build and without the
   release step, with the same checks as a deploy.
3. Without GitHub (an outage of GitHub Actions): a person with Fly access runs the same deploy:
   ```bash
   fly deploy -c infra/fly/api.fly.toml -a vybe-prod-api --image registry.fly.io/vybe-prod-api@sha256:<digest> --no-public-ips --ha=false
   fly deploy -c infra/fly/web.fly.toml -a vybe-prod-web --image registry.fly.io/vybe-web@sha256:<digest> --ha=false
   ```
   Use the `fly.toml` files of the good commit if they changed (`git show <sha>:infra/fly/api.fly.toml`).
4. Roll forward instead (fix and deploy a new release) when:
   - the old image does not become ready on the new schema: the migration was not an expand step
     (SEV-2 or higher; never run a down migration against production);
   - the old image needs content (rulesets, catalog, quests) that the new seed changed: publish the fix as
     new content versions;
   - the bad release changed data, not code: use [backup-restore.md](backup-restore.md) with the incident lead.
5. A web-only problem: roll back `vybe-web` only (the API digest of the current release stays).

Tested on 2026-11-07 with the compose stack: the database was migrated and seeded by the current image;
the image of b1487eb (before stream I) then started without a release step, `/health/ready` gave 200,
`GET /api/v1/catalog/items` 200 and `POST /api/v1/guests` 201. The `seed` of that older image failed, as
expected.

### Scaling before the social beta

Spec §16 and decision 7 ask for a second API machine before the social beta (availability target 99.5 %).
`infra/fly/api.fly.toml` does not set the machine count: Fly.io keeps the count of the app, and the deploy
workflow (`--ha=false`) and a rollback keep it too. `min_machines_running` only stops the auto-stop of
machines, which is off. Do this procedure once, before the social beta, after a deploy that passed the smoke
test. Two people: one runs the commands, the second watches `fly logs`.

1. **Pool.** The pooler client limit of the Supabase compute size must be more than
   2 × `Maximum Pool Size` (2 × 20 = 40) plus the owner connections of the release step. If it is not, make
   the compute size bigger first, or set `Maximum Pool Size=15` in `ConnectionStrings__Vybe`.
2. **Rate limits.** The rate limits are in the memory of each machine, and Caddy sends each request to a
   random machine. With two machines a client can get up to two times each limit (guest creation, account
   linking, writes and reads per player). This is accepted for the closed beta. To keep the old totals, set
   the `RateLimits__*` secrets to half of their defaults before you scale.
3. **Scale.**
   ```bash
   fly scale count 2 -a vybe-prod-api --region <region>
   fly scale count 2 -a vybe-prod-web --region <region>
   ```
4. **Check the machines.** `fly machines list -a vybe-prod-api` shows two machines in `<region>`, both `started`.
   `fly checks list -a vybe-prod-api` shows that both pass the `/health/ready` service check and the `live` check.
5. **Check the traffic.** Do smoke-test steps 2–4. In `fly logs -a vybe-prod-api`, request lines come from both
   machine ids. Caddy reads the AAAA records of `vybe-prod-api.internal` every 5 s, so wait 10 s after step 4.
6. **Check the workers.** Each machine runs all background workers. They are safe on many machines (table
   below). In `fly logs -a vybe-prod-api`, no worker logs `failed` in the 10 minutes after the scale.
7. **Smoke-test step 6 changes.** With two machines, send `10 × 2 + 1` = 21 guest creations (each with a
   forged `Fly-Client-IP`). One machine gets at least 11 of them, so at least one answer must be 429. If step 2
   halved the limit, 11 are enough.
8. **Load test.** Repeat the load test against an isolated beta-like environment with two machines
   ([load-test.md](load-test.md)) and record the result.
9. **Back to one machine** (for example to save cost after the beta): `fly scale count 1 -a vybe-prod-api`. Fly stops
   one machine with `SIGTERM`; running requests complete (`kill_timeout`), and a worker pass that stops rolls back
   its transaction, so the other machine runs it again.

Background workers with two or more machines:

| Worker | Why it is safe on many machines | Test |
|---|---|---|
| Outbox (`challenge-resolved` bonus) | One job per transaction, claimed with `FOR UPDATE SKIP LOCKED`; the job and its status change commit together. The ledger key `challenge:{id}` is unique, so even a repeated job never pays twice. | `MultiInstanceWorkerTests.Outbox_TwoInstances_RunEveryJobOnce_AndPayEveryBonusOnce`, `ChallengeTests.OutboxRetries_NeverPayTwice_AndRespectTheDailyCap` |
| Sweeper (sessions, challenges, elapsed suspensions) | Each batch claims rows with `SKIP LOCKED` and the update checks the status again (compare-and-swap). | `MultiInstanceWorkerTests.Sweeper_TwoInstances_ExpireEverySessionOnce`, `SweeperTests.LateSubmit_RacingTheSweeper_GivesOneExpiredState` |
| Privacy: exports and J4 anonymization | Each request is claimed with `FOR UPDATE SKIP LOCKED` and finished in one transaction; lock order player, then export. | `MultiInstanceWorkerTests.Privacy_TwoInstances_AnonymizeEveryPlayerOnce_AndDeleteEveryAuthUserOnce`, `PrivacyTests.ExportAndDeletionOfOnePlayer_InParallelWorkers_FinishWithoutAFile` |
| Privacy: Supabase Auth user deletion | A claim moves `next_attempt_at` forward by a 5-minute lease (`SKIP LOCKED`) and commits before the HTTP call, so another machine does not take the row. A `DELETE` of a deleted user is harmless. | `MultiInstanceWorkerTests.Privacy_TwoInstances_AnonymizeEveryPlayerOnce_AndDeleteEveryAuthUserOnce` |
| Privacy: export file removal and orphan sweep | The file removal claims the export row with `SKIP LOCKED`. The orphan sweep (once per hour per machine) removes only files of unknown or failed exports without a key; a running export has a `queued` row, so its file stays. | `PrivacyTests` |
| Guest cleanup | Players claimed with `FOR NO KEY UPDATE SKIP LOCKED`; the update checks kind and status again, so a player gets one deletion request. | `MultiInstanceWorkerTests.GuestCleanup_TwoInstances_QueueOneDeletionPerGuest` |
| Idempotency cleanup | Rows claimed with `SKIP LOCKED` in batches, then deleted. | `OpsIdempotencyCleanupTests.ConcurrentRuns_DeleteMoreThanOneBatch_EachRowOnce` |
| Deletion log copy | An entry is keyed by the request id and never changes, so two machines that write the same entry write the same object. A missed entry is written by the next pass. | `MultiInstanceWorkerTests.DeletionLog_TwoInstances_WriteEveryRequest_AndRewritesAreHarmless` |

The `replay-deletions` command is not a worker: run it once, on one temporary machine, before the API takes
traffic. Other process state: the API has no in-memory session, cache or Data Protection key ring. Guest
cookies and invite tokens are checked against the database and the shared `Challenges__InviteSecret`, so any
machine can answer any request. Only the rate limits are per machine (step 2).

Bigger machines: `fly scale vm shared-cpu-4x -a vybe-prod-api` or `fly scale memory 2048 -a vybe-prod-api`, then change
`[[vm]]` in `api.fly.toml` in a PR, so the next deploy keeps the size. Run the load test again after a change.

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
