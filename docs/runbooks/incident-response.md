# Runbook: incident response

- Owner: engineering lead. Spec §16 social beta gate: "incident runbook".
- On-call model (recommended in [decision 7](../decisions/07-hosting.md), not signed yet): a primary and a
  backup engineer, business hours in the launch time zone, alerts to phone; outside hours best effort.

## On-call

| Role | Name | Contact |
|---|---|---|
| Primary | TBD (engineering lead names the person at sign-off) | TBD |
| Backup | TBD | TBD |
| Incident lead for SEV-1 | Engineering lead | TBD |
| Privacy contact (personal data breach) | TBD (see [decision 8](../decisions/08-legal-wording.md)) | TBD |

## Severity

| Level | Examples | First answer |
|---|---|---|
| SEV-1 | API down for all players; wallet or ledger errors; data loss; personal data breach; leaked secret in use | 15 min, business hours; best effort outside |
| SEV-2 | One feature down (challenges, purchases, exports); 5xx rate above 5 % for 10 min; p95 above 1 s for 15 min; background workers stopped | 1 h |
| SEV-3 | Degraded but usable; one player affected; noisy alert | Next business day |

## Alerts (spec §12)

Configure these on the telemetry backend (OTLP metrics and Sentry). Send them to the primary and the backup.

| Alert | Signal | Threshold (start values; tune with beta data) |
|---|---|---|
| API 5xx | `http.server.request.duration` count with `http.response.status_code` ≥ 500 | > 2 % of requests for 5 min |
| Latency | `http.server.request.duration` p95, reads | > 300 ms for 15 min (spec §13 SLO) |
| Readiness | `/health/ready` from the platform probe or an uptime check | failing for 2 min |
| Wallet errors | Sentry errors in purchase, reward and admin adjustment paths; 500 on `/api/v1/purchases` | any |
| Score validation spike | rejected submits (`status = rejected`) | > 3× the 7-day baseline for 30 min |
| Auth failures | 401 rate; 503 `auth_unavailable` on linking | > 5× baseline for 10 min; any 503 for 5 min |
| Rate limit | 429 count (`aspnetcore.rate_limiting.requests` with `rejected`) | > 10× baseline (possible attack or a bad client release) |
| Background jobs | log lines "Outbox pass failed", "Sweep failed", "Privacy pass failed", "Idempotency cleanup failed"; outbox job `failed` | any, repeated for 10 min |
| Backup | backup job result | any failure |

## Procedure

1. **Acknowledge** the alert. Open an incident record (date, time, alert, who).
2. **Assess** the severity with the table above. For SEV-1, call the incident lead.
3. **Look**:
   - `GET /health/ready` on each instance (inside the platform network).
   - Recent deploys: was there a release in the last 2 hours? If yes, roll back first
     ([deploy-and-rollback.md](deploy-and-rollback.md)). Rollback is safe: the database stays.
   - Logs: filter JSON logs by `level` = `Error` or `Critical`. Use `traceId` to find the trace and the
     Sentry event. A problem response to a client has the same `traceId`.
   - Database: connection count against the pooler limit; long locks:
     ```sql
     SELECT pid, now() - xact_start AS age, wait_event_type, state, left(query, 120)
     FROM pg_stat_activity WHERE datname = current_database() ORDER BY xact_start NULLS LAST LIMIT 20;
     ```
   - Provider status pages: database host, container host, Supabase Auth, Sentry.
4. **Mitigate** (stop the damage before you find the root cause):
   - Bad release: roll back.
   - Overload: add an instance; lower `RateLimits:WritesPerPlayerPerMinute` or `RateLimits:GuestsPerIpPerMinute`.
   - Abuse by one player: suspend the player through the admin API (audited).
   - A background job breaks data: set `Jobs__Enabled=false` on all instances, then fix.
   - Leaked secret: [secret-rotation.md](secret-rotation.md) now.
   - Data loss: [backup-restore.md](backup-restore.md), with the incident lead.
5. **Communicate**: for SEV-1 and SEV-2, post a status update every 30 min to the team channel and, if
   players are affected, a short player-facing notice. Do not share personal data in updates.
6. **Close**: when the signals are normal for 30 min. Write down the end time.
7. **Review**: for SEV-1 and SEV-2, write a blameless review within 5 business days: timeline, impact,
   root cause, actions with owners.

## Personal data breach

A breach of personal data (for example: a leaked database dump, exports readable by others, logs with
personal data sent to a wrong place) has legal deadlines (GDPR: report to the supervisory authority within
72 hours of awareness, if the breach is likely to be a risk to people).

1. Treat it as SEV-1. Call the incident lead and the privacy contact at once.
2. Contain: rotate the exposed credentials, remove public access, delete the exposed copies.
3. Record: what data, which players, when it started, when it was found, who had access.
4. The privacy contact decides about the report to the authority and the notice to players.
   Final legal wording: [decision 8](../decisions/08-legal-wording.md).

## Useful facts

- Problem responses have `traceId`; JSON logs have `traceId`, `spanId` and `requestId`.
- Logs and traces never contain invite tokens (redacted), request bodies, cookies or client addresses.
- Background workers run in every API instance; they are safe with many instances (`SKIP LOCKED`).
- The guest cookie is `__Host-vybe_guest`; never ask a player for it.
