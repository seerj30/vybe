# Runbook: deploy and rollback

- Owner: stream D (operations). On-call names: see [incident-response.md](incident-response.md).
- Applies to: the API container (`services/api/Dockerfile`) on any container host.
  Hosting is not signed yet ([decision 7](../decisions/07-hosting.md)). The recommendation is
  Supabase Pro + one always-on container, with `/api` on the same origin as the web app.
- Related: [migrations.md](migrations.md), [secret-rotation.md](secret-rotation.md), [ADR 0006](../adr/0006-operations.md).

## What a release contains

| Part | Command or artifact |
|---|---|
| Image | `docker build -f services/api/Dockerfile -t <registry>/vybe-api:<git-sha> .` (from the repository root) |
| Migration step | `docker run --rm <env> <image> migrate` |
| Seed step | `docker run --rm <env> <image> seed` (rulesets, catalog, quests; idempotent) |
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

## Local run

```bash
docker compose -f infra/docker-compose.yml up --build
```

The compose file runs `db`, then `migrate`, then `seed`, then the API on `http://localhost:8080` in the
Development environment.
