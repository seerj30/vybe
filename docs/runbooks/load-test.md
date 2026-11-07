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

Three scenarios run at the same time, each at a constant arrival rate:

| Scenario | Rate | Requests per iteration | Requests/s |
|---|---|---|---|
| `guests` | 2/s | `POST /guests`, `GET /catalog/items`, `GET /me` | ~6 |
| `runs` | 1/s | `POST /games/street-hoops/sessions`, `POST …/start`, wait for the run (~60 s), `POST …/submit` with about 45–90 random shots | ~3 (about 65 runs open at the same time) |
| `purchases` | 5/s | `POST /purchases` (200, or 409 `insufficient_funds` for a new guest), `GET /me` | ~10 |

Total at steady state: about 19–20 requests/s. The first minute has fewer, because the first runs are still
open. Every VU uses its own guest and replaces it after 40 writes, so the per-player write limit
(120 per minute) does not interfere.

Thresholds (the run fails when one is not met):

- reads: p95 < 300 ms (spec §13 beta SLO);
- writes: p95 < 800 ms (provisional, until a baseline exists);
- failed requests < 1 %; checks > 99 % (a submit must be `verified`).

Challenge acceptance is not in the script yet (it needs two players and an invite link per iteration).

The script imports nothing from the network (only k6 built-in modules). `BASE_URL` must have the form
`http(s)://host[:port][/path]`; a URL with user info (`http://localhost@other.example`), a query or a fragment
is refused.

## Run it locally

1. Start the stack with a high guest limit (the load test creates many guests from one address):
   ```bash
   VYBE_GUESTS_PER_IP_PER_MINUTE=100000 docker compose -f infra/docker-compose.yml up -d --build
   ```
2. Wait until the API is ready:
   ```bash
   until curl -sf http://localhost:8080/health/ready; do sleep 1; done
   ```
3. Run k6 (5 minutes by default; set `DURATION` to change it):
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
