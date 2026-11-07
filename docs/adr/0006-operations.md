# ADR 0006: Operations — container, deploy steps, observability and edge security

- Status: accepted
- Date: 2026-11-06 (review fixes 2026-11-07)
- Spec: [v1.3 §11, §12, §13, §16](../spec/VYBE_Technical_Specification_v1.3.md); hosting:
  [decision 7](../decisions/07-hosting.md) (not signed); schema: [ADR 0002](0002-database-schema.md);
  slice: [ADR 0003](0003-api-vertical-slice.md); jobs: [ADR 0004](0004-challenges-and-jobs.md);
  privacy: [ADR 0005](0005-accounts-and-safety.md)

## Context

Stream D of `docs/roadmap.md` makes the API deployable and observable. Hosting is not signed. The
recommendation is Supabase Pro + one always-on container, with `/api` on the same origin as the web app.
So every decision below must work on any container host and any OpenTelemetry backend.

## Decision

1. **One image, three commands.** `services/api/Dockerfile` builds from the repository root. The runtime is
   `aspnet:10.0-noble-chiseled-extra`: no shell, no package manager, non-root user (UID 1654), ICU and
   tzdata included. Content files (rulesets, catalog, quests) are in `/app/content`. The same image runs
   the API (no argument), `migrate` and `seed`. `DOTNET_EnableDiagnostics=0` removes the diagnostics socket,
   so the root filesystem can be read-only (a `tmpfs` at `/tmp` is enough).
2. **Migrations and seeding are deploy steps, never start-up work.** `migrate` applies the EF Core
   migrations; `seed` runs `ContentSeeder` and `QuestSeeder` (idempotent). Both use
   `ConnectionStrings:Migrations` (owner role, direct or session connection), else `ConnectionStrings:Vybe`.
   The API process does not need DDL rights. Rollback redeploys the previous image and never reverts the
   database (expand-contract, spec §13).
3. **Probes.** `/health/live` has no dependencies, so a database outage does not restart every instance.
   `/health/ready` checks that the database answers and that every migration of the image is applied, so a
   new image gets traffic only after the migration step. Both answer `Healthy`/`Unhealthy` without details
   and are outside `/api/v1`.
4. **Fail fast on configuration.** In every environment except Development and Testing, start-up stops when a
   required setting is missing or invalid, and one error names every problem (never a value). A misspelled
   environment name cannot skip the check. Start-up exceptions exit with code 1: as PID 1 in a container, an
   unhandled exception otherwise hangs instead of exiting.
5. **Export storage.** `S3ExportStorage` implements `IExportStorage` (ADR 0005) on any S3-compatible store
   (Supabase Storage S3 endpoint, AWS S3, MinIO) with the AWS SDK. Objects are private (`exports/{id}.json`);
   download URLs are SigV4 presigned GET URLs valid for min(10 min, export expiry), as an attachment. The
   storage endpoint must be https outside Development. Supabase S3 access keys give storage access only,
   so the API does not need the Supabase service role key.
6. **Telemetry is opt-in by configuration and vendor-neutral.** OpenTelemetry traces and metrics (ASP.NET Core,
   HttpClient, Npgsql, runtime, rate limiting) are exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT`
   is set; outside Development the endpoint must be https, or http to a collector on localhost (a sidecar on the
   same host does not cross a network). Sentry runs only when `Sentry:Dsn` is set, for errors only (traces go to
   OpenTelemetry).
7. **No secrets or personal data leave the process.** Logs outside Development are one JSON object per line
   (`JsonLogFormatter`) with `traceId`, `spanId` and `requestId`. Scopes are allowlisted, so the framework's
   `RequestPath` scope is not written. Only VYBE log categories write their structured properties. Invite
   tokens and deletion-request ids (capabilities in URLs) are redacted, and query strings after a path are
   removed, in messages, properties and exceptions. Server spans keep the route template, not the raw path
   (error responses use the route saved before the exception handler cleared the endpoint), and drop client
   address, port and user agent. Sentry events keep only the redacted URL: no query, cookies, headers, body or user address;
   breadcrumbs, exception values, tags and extra data are redacted.
8. **Edge security** (`EdgeSecurity`, the first middleware):
   - Forwarded headers only from `ReverseProxy:KnownNetworks` (required in deployed environments), with a
     forward limit of 1. Otherwise, behind a proxy every client would share the proxy's rate-limit bucket.
     A `/0` network needs `ReverseProxy:TrustAnyHop=true`, because it lets any client forge its address.
   - Every response: `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; …`, `nosniff`,
     `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cross-Origin-Resource-Policy: same-site`,
     HSTS outside Development on https requests only (`Security:HstsIncludeSubDomains`, default true), and
     `Cache-Control: no-store` unless the endpoint sets its own value. The
     headers are added in `OnStarting`, so error responses also have them.
   - CORS only when `Cors:AllowedOrigins` lists https origins (no path, no wildcard), with credentials and
     the CSRF header `X-Requested-With`. The recommended same-origin setup needs no CORS.
   - Global fixed-window limits per player when authenticated (guest player id or token subject), else per client
     address (IPv6 per /64): writes (`RateLimits:WritesPerPlayerPerMinute`, default 120) and reads
     (`RateLimits:ReadsPerMinute`, default 600; `/health` excluded). Endpoints with their own policy (guest
     creation, 10 per address per minute) are not counted twice. The windows follow the application
     `TimeProvider` (`ClockRateLimiter`), so tests move them with a fake clock.
9. **Idempotency cleanup.** `IdempotencyCleanup` deletes `idempotency_records` after `expires_at`
   (created + 72 h) in `SKIP LOCKED` batches of 1,000, and keeps records whose lease is still held. It uses
   the database clock, like `expires_at` and `locked_until`. A hosted worker runs it every
   `Jobs:IdempotencyCleanupSeconds` (default 3,600) unless `Jobs:Enabled=false`. It lives in
   `Infrastructure/`, with the same pattern as `Jobs/`, so stream A files did not change.
10. **Load test** with k6 (`infra/load/street-hoops.js`): guest creation, full runs (create, start, submit after
    the run time) and purchases at about 20 requests/s. The script has no remote imports, and refuses non-local
    targets (and any URL with user info) unless the host is confirmed explicitly.
11. **Deletion log** (spec §2 J4). A restore must not bring back the personal data of a player who asked for
    deletion after the backup. `DeletionLogExporter` copies every deletion request of the last 40 days that is
    not in the log to `deletion-log/{requestId}.json` in the export bucket (ids and time only), every
    `Jobs:DeletionLogSeconds` (default 300). After a restore, the `replay-deletions` command queues every logged
    deletion whose player is in the restored database and not deleted (player → `deleting`), then runs the
    existing privacy processor until nothing is queued. Stream B files did not change.
12. **Database privileges** are a script, [`infra/db/grants.sql`](../../infra/db/grants.sql), run by the owner role
    after the first migration and after every restore: the API role gets data access only; `PUBLIC`, `anon` and
    `authenticated` get nothing.

## Not done

- Deploy pipeline, environments and secret store: they wait for the hosting sign-off (decision 7).
- A CI job that builds the image (`.github/workflows/ci.yml` is not a stream D file).
- Product events and the business metrics of spec §12 (purchase failures, duplicate idempotency attempts,
  invalid score rate): they need counters in feature files of streams A and B.
- Challenge acceptance in the load test.
- Host filtering (`AllowedHosts` is `*`): the proxy routes by host.
- A second copy of the ledger: ledger rows after a restore point are lost (see the backup-restore runbook);
  point-in-time recovery keeps the gap small.

## Consequences

- Each deployed environment needs the settings listed in `docs/runbooks/deploy-and-rollback.md`; a missing
  one stops the start-up with a full list.
- A new image is not ready until its migrations are applied, so the deploy order is migrate → seed → rollout.
- Behind a proxy, `ReverseProxy:KnownNetworks` must match the proxy. `0.0.0.0/0` and `::/0` need
  `ReverseProxy:TrustAnyHop=true`; then the container must not be reachable directly.
- A restore runs `grants.sql`, `migrate` and `replay-deletions` before the API takes traffic.
- A client sees `Cache-Control: no-store` on responses without their own value; endpoints that allow caching
  (rulesets) keep theirs.
- Tests: `Ops*` in `Vybe.Api.Tests`; `OpsExportStorageTests` needs Docker (MinIO, `pgsty/minio`, because
  the official `minio/minio` image is no longer on Docker Hub).
