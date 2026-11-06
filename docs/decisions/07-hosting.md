# Decision 7: Hosting budget, target concurrency and on-call ownership

- Spec: [§13 Deployment and operations](../spec/VYBE_Technical_Specification_v1.3.md#13-deployment-and-operations), [§12](../spec/VYBE_Technical_Specification_v1.3.md#12-observability-and-analytics), [§16 social beta gate](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates), [§17 item 7](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off); [ADR 0002](../adr/0002-database-schema.md) (schema `vybe`, pooling note), [ADR 0004 #6–7](../adr/0004-challenges-and-jobs.md) (background workers); `docs/roadmap.md` stream D
- Signs off: product owner (budget) and engineering lead (operations); the named on-call people accept the rota
- Needed before: closed beta (stream D waits for it: "hosting-agnostic until §17 #7")

> Prices change often. The numbers below are order-of-magnitude estimates. Check each vendor's
> price page on the decision date.

## Question

1. Where do the database, the API container and the web assets run?
2. What peak load must the beta carry (the load-test target)?
3. What is the monthly budget?
4. Who is on call, and when?

## Why it matters

- Stream D (container image, deployment, OpenTelemetry, Sentry, backups, runbooks, load test) cannot finish without a target platform.
- Spec §16 social beta gate requires a "load/security review" and an "incident runbook". Spec §13: "target concurrency determined by expected beta size, not invented".
- The design already uses **Supabase Auth** (J3, ADR 0003 #2). Supabase-managed Postgres puts the database and the identity provider with one vendor and one data processing agreement.
- The current code has properties that a host can break (next section).

## Effect of the current design on hosting

| Design property | Where | Effect on hosting |
|---|---|---|
| Tables in schema `vybe`, not `public`; privileges revoked from `PUBLIC`, `anon`, `authenticated` | ADR 0002 #2 | On Supabase: do not add `vybe` to the Data API "exposed schemas". Then the Supabase REST and GraphQL APIs cannot reach VYBE tables. |
| The API role must own no tables | ADR 0002 operations notes | Create a login role (for example `vybe_api`) with only the needed grants. Through Supavisor the user name has the form `role.project-ref`. |
| **Transaction pooling** (Supavisor port 6543) | ADR 0002 operations notes | Works if no state lives across transactions. Set `Max Auto Prepare=0` (no named prepared statements) and keep Npgsql multiplexing off. |
| **Advisory locks**: `pg_advisory_xact_lock` for the pair lock | `Infrastructure/PairLock.cs`, invariant 12 | Transaction-scoped, so it works with transaction pooling. **Never** add session-level `pg_advisory_lock`: in transaction mode the next statement can run on another server connection. |
| Retention delete flag `set_config('vybe.retention_delete','on', true)` | ADR 0002 #6, #11 | Transaction-local (`true`), so it works with transaction pooling. |
| `FOR UPDATE SKIP LOCKED` in the outbox and sweeper | ADR 0004 #6–7 | Works on any Postgres host. |
| **Hosted background services** (`OutboxWorker` every 5 s, `SweeperWorker` every 30 s) in every API process | `Jobs/JobsSetup.cs` | Safe with many instances (`SKIP LOCKED`, compare-and-swap writes). But a **scale-to-zero** platform stops the workers when there is no HTTP traffic: winner bonuses, challenge quest progress and expiries then wait for the next request. Use at least 1 always-on instance with CPU outside requests, or a separate worker process (`Jobs:Enabled=false` on the web instances). |
| EF Core migrations | ADR 0002 #1 | Run them through a direct connection or the session-mode pooler, as a deploy step. Do not run DDL through the transaction pooler. |
| Guest cookie `__Host-vybe_guest` | ADR 0003 #2 | Serve `/api` on the **same origin** as the web app (one edge or reverse proxy). Recent Safari versions can cut server-set cookies to 7 days when the API host's IP does not match the site ([decision 2](02-guest-sessions.md)). |
| Forwarded headers for rate limits | ADR 0003 #3, `ServiceSetup.cs` | Configure the proxy's forwarded headers, or every client has the proxy's IP and the guest rate limit (10/min per IP) blocks everyone. |
| Secrets: DB connection string, `Challenges:InviteSecret`, Supabase settings | ADR 0004 consequences | Use the platform's secret store (spec §13). |
| Supabase direct connections | — | Supabase direct connections use IPv6 by default; many container hosts have no IPv6 egress. Use the pooler (IPv4) or buy the IPv4 add-on. Verify on the current Supabase documentation. |
| Region | — | Put the API in the same region as the database. A run verification is about 10–20 SQL round trips in one transaction. Pick an EU region if most players are in the EU ([decision 8](08-legal-wording.md)). |

## Target concurrency model

Assumptions (to replace with beta data): 40 % of registered players are active each day; 15 % of the daily players play in the peak hour; one visit is 15 minutes; one active player sends about 7 API requests per minute (create, start and submit a run, then reads of wallet, quests, catalog).

| Registered players | Daily active | Peak-hour players | Peak concurrent players | Peak API requests/s | Load-test target (10×) |
|---|---|---|---|---|---|
| 1,000 (closed beta) | 400 | 60 | 15 | ~2 | 20 rps |
| 10,000 | 4,000 | 600 | 150 | ~18 | 180 rps |
| 100,000 | 40,000 | 6,000 | 1,500 | ~180 | 1,800 rps |

At beta size, one small API instance and the smallest paid database compute are enough. The 10× target leaves room for spikes (for example a shared challenge link).

Database connections: total Npgsql `Max Pool Size` over all instances must stay below the pooler client limit of the chosen compute size. With transaction pooling, a pool of 20 per instance is enough at beta size.

## Options

| | Option | Fit with the design | Operations work | Monthly cost, beta (rough) |
|---|---|---|---|---|
| **A** | **Supabase Pro (Postgres + Auth) + always-on container host** (for example Fly.io, Render, Railway, or Azure Container Apps / Cloud Run with min. 1 instance and always-allocated CPU) + CDN for web and assets | Best: Auth is already Supabase; workers run in-process; same-origin `/api` through the CDN or edge | Low: managed backups (check plan retention; point-in-time recovery is an add-on), managed upgrades | Supabase plan base fee + compute (about USD 25–60) + 1–2 small instances (about USD 10–50) + CDN (low) + Sentry (free or team tier) → **about USD 50–150** |
| **B** | **Supabase Pro + serverless containers that scale to zero** (Cloud Run, Azure Container Apps at min. 0) | Weak: workers stop when idle; cold starts on the first request | Low | Lowest compute cost (about USD 0–20 for the API) |
| **C** | **Cloud provider Postgres** (Azure Database for PostgreSQL, AWS RDS, Google Cloud SQL) **+ Supabase Auth only** | Good; no Supavisor, so direct connections or a pooler that the team runs | Medium: two vendors, network setup, backups to configure | About USD 50–200 |
| **D** | **Self-managed VM** (Postgres + API on one or two servers) | Works | High: backups, restore drills, patching, monitoring are all team work | About USD 20–60 plus team time |

On-call options:

| | On-call model | Fit |
|---|---|---|
| 1 | One named engineer, best effort | Not enough for the §13 SLO (99.5 %) |
| 2 | **Primary and backup engineer, business hours in the launch time zone, alerts to phone; outside hours: best effort** | Closed beta |
| 3 | 24/7 rota with paid on-call | Public launch at scale |

## Recommendation

**Option A with on-call model 2 for the closed beta.**

- Supabase Pro in one region (EU if most players are in the EU), smallest compute that passes the 20 rps load test.
- One always-on API container in the same region; a second instance before the social beta for the 99.5 % target. Workers in-process for the beta.
- API through the transaction pooler with `Max Auto Prepare=0`; migrations through a direct or session connection.
- `/api` and the web app on one origin behind the CDN.
- **Budget ceiling for the beta: USD 150 per month** (excluding people). Review at 10,000 registered players.
- On-call: a named primary and backup; alerts of §12 (5xx, wallet errors, score validation spikes, auth failures, backup failure) go to both. The runbooks of stream D name them. Decide model 3 before public launch.

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Container image, deploy pipeline, environments (preview, staging, production), secrets | `services/api/Dockerfile`, `infra/`, `.github/workflows/` | stream D |
| Connection strings (pooler for the API, direct/session for migrations), `Max Auto Prepare=0` | `infra/`, deploy secrets; ADR 0002 operations notes | stream D |
| Forwarded headers configuration for the proxy | `ServiceSetup.cs` (hosting hooks) | stream D |
| Same-origin routing for `/api`; Safari cookie check | `infra/`, `tests/e2e` | stream D |
| Seed step at deploy (rulesets, catalog, quests) | `infra/` deploy job (ADR 0003 #6, ADR 0004 consequences) | stream D |
| Load test at the 10× target; backup and restore drill | `docs/runbooks/` | stream D |
| On-call names, alert routing, escalation | `docs/runbooks/incident.md` | engineering lead |
| Spec §13 target concurrency and budget; §17 item 7 resolved | spec (new version and changelog entry) | orchestrator |
