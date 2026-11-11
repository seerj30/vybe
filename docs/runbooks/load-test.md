# Runbook: load test

- Owner: stream D (operations). Spec §13: "Load test guest creation, game submissions, wallet transactions,
  and challenge acceptance". Spec §16 social beta gate: "load/security review".
- Script: [`infra/load/street-hoops.js`](../../infra/load/street-hoops.js) (k6).
- Target: about 20 requests/s, the closed-beta target (10× the expected peak) in
  [decision 7](../decisions/07-hosting.md).

## Rule: local targets only

Run the load test only against a local stack or an environment that you own and that is isolated from
players. Never run it against production or a shared host without written approval of the engineering lead
and the host provider's rules for load tests. The script stops when `BASE_URL` is not a local host. To test a
dedicated load-test environment, set `VYBE_LOAD_TARGET_CONFIRMED` to its host name.

## What the script does

Four scenarios run at the same time, each at a constant arrival rate:

| Scenario | Rate | Requests per iteration | Requests/s |
|---|---|---|---|
| `guests` | 2/s | `POST /guests`, `GET /catalog/items`, `GET /me` | ~6 |
| `runs` | 1/s | `POST /games/street-hoops/sessions`, `POST …/start`, wait for the run (~60 s), `POST …/submit` with about 45–90 random shots | ~3 (about 65 runs open at the same time) |
| `purchases` | 4/s | `POST /purchases` (200, or 409 `insufficient_funds` for a new guest), `GET /me` | ~8 |
| `challenges` | 1 per 4 s | Creator: `POST /guests`, a full-length run (create, start, wait 58.5 s, submit), `POST /challenges`. Recipient: `POST /guests`, `GET /invites/{token}`, `POST /invites/{token}/accept`, a full-length recipient run, `GET /challenges/{id}` (must be `completed`) | ~3 (12 requests in about 2 minutes; about 30 iterations open at the same time) |

Total at steady state: about 20 requests/s. The first two minutes have fewer, because the first runs and
challenges are still open. Every VU uses its own guest and replaces it after 40 writes, so the per-player
write limit (120 per minute) does not interfere.

Challenge acceptance (spec §13, J2) needs reward-eligible runs (spec v1.4 §7): the creator run and the
recipient run each wait `minRunMsForRewards` (58 s) of real time before the submit. So one iteration takes
about 2 minutes, and its rate is low (one every 4 s) to keep the total near 20 requests/s. The `challenges`
scenario has `gracefulStop: 150s`: after the 5 minutes, the open iterations complete their runs (the test
takes about 7.5 minutes in total, with a lower rate at the end).

Thresholds (the run fails when one is not met):

- reads: p95 < 300 ms (spec §13 beta SLO);
- writes: p95 < 800 ms (provisional, until a baseline exists);
- failed requests < 1 %; checks > 99 % (a submit must be `verified`).

The script imports nothing from the network (only k6 built-in modules). `BASE_URL` must have the form
`http(s)://host[:port][/path]`; a URL with user info (`http://localhost@other.example`), a query or a fragment
is refused.

## Run it on production (owner approval only)

The product owner approved one small production run on 2026-11-11 (before real players). For a new run, get the
same approval first. Steps:

1. On the VM, add `RateLimits__GuestsPerIpPerMinute=1000` to `/etc/vybe/api.env` (keep a copy of the file first)
   and recreate the API: `cd /opt/vybe && sudo docker compose -p vybe -f compose.yml --env-file release.env up -d api`.
2. Note the start time (UTC). Run k6 with `BASE_URL=https://<origin>`, `VYBE_LOAD_TARGET_CONFIRMED=<host>`,
   `RATE_SCALE` (0.5 = about 10 requests/s) and `DURATION`. If your DNS rewrites sslip.io, add
   `--add-host <host>:<ip>` to `docker run`.
3. Restore the copy of `/etc/vybe/api.env` and recreate the API again.
4. Queue the deletion of the test guests: the `GuestCleanup` SQL (`services/api/src/Vybe.Api/Jobs/GuestCleanup.cs`)
   with `p.created_at` limited to the test window instead of the 30-day rule. The privacy processor deletes them.
   First count the guests in the window and in the hour before it, to be sure that no real guest is in the window.

## Run it locally

1. Start the stack with a high guest limit (the load test creates many guests from one address):
   ```bash
   VYBE_GUESTS_PER_IP_PER_MINUTE=100000 docker compose -f infra/docker-compose.yml up -d --build
   ```
2. Wait until the API is ready:
   ```bash
   until curl -sf http://localhost:8080/health/ready; do sleep 1; done
   ```
3. Run k6 (5 minutes of arrivals by default, about 7.5 minutes in total; set `DURATION` to change it):
   ```bash
   docker run --rm -i -e BASE_URL=http://host.docker.internal:8080 grafana/k6:1.3.0 run - < infra/load/street-hoops.js
   ```
   With k6 installed locally: `k6 run infra/load/street-hoops.js` (default `BASE_URL` is `http://localhost:8080`).
4. Stop the stack and delete its data:
   ```bash
   docker compose -f infra/docker-compose.yml down -v
   ```

The local stack runs in Development (no Supabase, local export storage). It measures the API and the
database on your machine, not the target hosting. Repeat the test on the chosen hosting before the social
beta gate, in an isolated environment, with production-like compute.

## Record

| Date | Target (where) | Duration | Requests/s | Read p95 | Write p95 | Failures | Result |
|---|---|---|---|---|---|---|---|
| 2026-11-06 | Local compose on a developer laptop (Apple silicon, Docker Desktop) | 90 s trial | 14.9 (ramp-up included) | 5 ms | 16 ms | 0 % | Pass (script check, not a capacity result) |
| 2026-11-07 | Local compose on a developer laptop (Apple silicon, Docker Desktop), master 4cfd254 (streams A–H) | 5 min | 17.6 (ramp-up included) | 4.2 ms | 16.3 ms | 0 % (0 of 5,798) | Pass: all thresholds, 5,796 of 5,796 checks (not a capacity result) |
| 2026-11-10 | Local compose on a developer laptop (Apple silicon, Docker Desktop), master 878cf5a + challenge scenario (stream L) | 5 min of arrivals + 2 min drain (6 min 57 s) | 14.6 (ramp-up and drain included) | 5.0 ms | 16.2 ms | 0 % (0 of 6,107) | Pass: all thresholds, 6,105 of 6,105 checks; 76 challenges created, accepted and completed (not a capacity result) |
| 2026-11-11 | Production, Oracle VM (2 OCPU A1, 12 GB) + Supabase Free (Nano, transaction pooler), release 963ac1f; k6 on the owner's Mac (Romania → Frankfurt); `RATE_SCALE=0.5`, `DURATION=3m` | 3 min of arrivals + drain (3 min 56 s) | 7.5 | 120 ms | 146 ms | 0 % (0 of 1,761) | Fail on checks only (98.7 %): 23 challenge recipients not created (k6 cookie jar bug over HTTPS, fixed in the script). Latency thresholds pass |
| 2026-11-11 | Same, with the cookie fix; `RATE_SCALE=0.5`, `DURATION=2m` | 2 min of arrivals + drain (3 min 51 s) | 5.5 | 126 ms | 146 ms | 0 % (0 of 1,275) | Pass: all thresholds, 1,273 of 1,273 checks, challenges created, accepted and completed. VM load average after the run 0.35, API 102 MiB. Half the target rate: repeat at `RATE_SCALE=1` before more than a few hundred players |
