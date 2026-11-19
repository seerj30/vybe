# Runbook: load test

- Owner: stream D (operations). Spec §13: "Load test guest creation, game submissions, wallet transactions,
  and challenge acceptance". Spec §16 social beta gate: "load/security review".
- Script: [`infra/load/street-hoops.js`](../../infra/load/street-hoops.js) (k6). Real-time spaces (P2):
  [`infra/load/spaces.js`](../../infra/load/spaces.js), see [Real-time spaces](#real-time-spaces-p2).
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
| 2026-11-11 | Production at the full rate, release 90628e1 (P1: friends, leaderboard upsert and achievement progress in the run verification, Street Juggle, My Room); k6 on the owner's Mac; `RATE_SCALE=1`, `DURATION=5m` | 5 min of arrivals + drain (6 min 59 s) | 14.6 (ramp-up and drain included) | 121 ms | 161 ms | 0 % (0 of 6,110) | Pass: all thresholds, 6,108 of 6,108 checks, challenges created, accepted and completed. VM during the run: API at most 32 % of one core (150 % only in the first sample, at start-up), at most 205 MiB; load average at most 0.64. 873 test guests, deleted afterwards |

## Real-time spaces (P2)

- Script: [`infra/load/spaces.js`](../../infra/load/spaces.js) (k6, `k6/experimental/websockets`). Each virtual user is
  one avatar: a new guest, a hub ticket, a WebSocket with the SignalR MessagePack protocol (as the web client), `Join`,
  then `Move` at 10 Hz (new direction every 2 s), a quick-chat every 20–25 s and a ping every 15 s. At the end of a
  session it sends `Leave` and closes. Protocol: [p2-spaces-protocol.md](../design/p2-spaces-protocol.md).
- Target ([P2 pack 02](../p2/02-real-time-spaces.md)): step up 100, 250, 500, 1,000 avatars with the HTTP scenario
  above at the same time. Pass: snapshot interval p95 < 150 ms, the HTTP thresholds pass, API CPU < 70 % of its limit,
  no dropped connections.
- Thresholds of the script: `spaces_snapshot_gap` p95 < 150 ms, `spaces_join_time` p95 < 1 s, no join failures,
  no kicks, no dropped connections, checks > 99 %.
- Settings: `STEPS` (avatars per step, default `100,250,500,1000`), `STEP` (hold, `2m`), `RAMP` (`30s`),
  `SESSION_MS` (session length before `Leave` and a new guest, `120000`).
- Local targets only (same rule and `VYBE_LOAD_TARGET_CONFIRMED` as above).

### Run it locally

All sockets come from one address, so raise the guest, per-address hub and read limits of the local stack (the
WebSocket upgrade has no cookie in k6, so it counts as an anonymous read of that address):

```bash
VYBE_GUESTS_PER_IP_PER_MINUTE=100000 VYBE_SPACES_PER_IP=5000 VYBE_READS_PER_MINUTE=100000 docker compose -f infra/docker-compose.yml up -d --build
```

```bash
docker update --cpus 1.5 --memory 2g --memory-swap 2g vybe-api-1
```

```bash
docker run --rm -i --ulimit nofile=65536:65536 -e BASE_URL=http://host.docker.internal:8080 -e STEPS=500 -e SESSION_MS=600000 grafana/k6:1.3.0 run - < infra/load/spaces.js
```

Start the HTTP scenario (`street-hoops.js`, `DURATION=2m`) in a second shell 30 s later, and sample the API with
`docker stats vybe-api-1`. The `docker update` line gives the API the production limits of `infra/oracle/compose.yml`.

### Record (spaces)

Local compose on a developer laptop (Apple silicon, Docker Desktop, 14 CPUs), API container limited to 1.5 CPU and
2 GB, k6 on the same laptop; branch `claude/stream-v-spaces-hub` (PR #75). One run per step: 30 s ramp, 2 min hold,
sessions longer than the run (no churn); HTTP scenario at its full rate (about 10.5 requests/s over 2 min of
arrivals) during the hold. CPU: `docker stats`, % of one core, steady state (after 40 s); the limit is 150 %.
Not a capacity result for the Oracle VM: an A1 core is slower than a laptop core.

| Date | Avatars | Snapshot gap p95 / p99 | Join p95 | Drops / kicks | Server messages/s | HTTP read p95 / write p95 / failures | API CPU median / p90 / max | API memory | Result |
|---|---|---|---|---|---|---|---|---|---|
| 2026-11-19 | 100 | 102 / 104 ms | 5 ms | 0 / 0 | ~930 | 3.8 / 16 ms / 0 % | 11 / 13 / 16 % | 164 MiB | Pass |
| 2026-11-19 | 250 | 104 / 107 ms | 6 ms | 0 / 0 | ~2,300 | 5.7 / 15 ms / 0 % | 21 / 26 / 32 % | 172 MiB | Pass |
| 2026-11-19 | 500 | 105 / 109 ms | 7 ms | 0 / 0 | ~4,600 | 4.9 / 17 ms / 0 % | 36 / 42 / 51 % | 190 MiB | Pass |
| 2026-11-19 | 750 | 112 / 132 ms | 7 ms | 0 / 0 | ~6,900 | 25 / 48 ms / 0 % | 48 / 68 / 131 % | 212 MiB | Pass |
| 2026-11-19 | 1,000 | 115 / 133 ms | 10 ms | 0 / 0 | ~9,300 | 25 / 49 ms / 0 % | 57 / 77 / 91 % | 229 MiB | Pass: 38 % of the CPU limit (median), 51 % (p90) |

Notes from the same day:

- A first single run (all steps in one, 2 min sessions with a new guest each time) failed on the test client: it
  closed sockets without `Leave`, so the 30 s slot reservations of the closed sessions filled the 64 × 16 = 1,024
  slots at 1,000 avatars (`spaces_full`), and it kept sending input after a refused `Join` (kicked `invalid_input`,
  as designed). The script now sends `Leave` and treats a refused `Join` as a failure. The HTTP thresholds passed in
  that run too (read p95 10 ms). With that churn and the retry loop the API reached its 1.5 CPU limit at 1,000.
- At 750 and more, the first per-step runs got 429 on the WebSocket upgrade: the read limit (600 per minute per
  address) counted every upgrade from the one k6 address. Real players connect from their own address or with their
  guest cookie. The local stack now takes `VYBE_READS_PER_MINUTE`.
- Capacity note: under heavy churn, players who drop without `Leave` hold their slot for 30 s, so `spaces_full`
  (1,024 slots) comes before the connection cap (1,100).
- Production run: only with owner approval (rule above), before the first real players in spaces. Expected peak in
  the closed beta: about 15 players.

### Production run (spaces)

Same procedure as [Run it on production](#run-it-on-production-owner-approval-only), with three raised limits in
`/etc/vybe/api.env` (all sockets come from one address): `RateLimits__GuestsPerIpPerMinute=100000`,
`RateLimits__ReadsPerMinute=100000`, `Spaces__MaxConnectionsPerAddress=5000`. Restore the copy afterwards.

```bash
docker run --rm -i --ulimit nofile=65536:65536 --add-host <host>:<ip> -e BASE_URL=https://<host> -e VYBE_LOAD_TARGET_CONFIRMED=<host> -e SESSION_MS=1200000 grafana/k6:1.3.0 run - < infra/load/spaces.js
```

Sample the VM in a second shell (one SSH session): `docker stats --no-stream vybe-api-1 vybe-web-1` every 10 s.

Record of 2026-11-19: release ea1299f, Oracle VM (2 OCPU A1, 12 GB; API limit 1.5 CPU / 2 GB, web 0.5 CPU / 512 MB),
Supabase Free; k6 on the owner's Mac (Romania → Frankfurt), one run 100 → 250 → 500 → 1,000 avatars (30 s ramp,
2 min hold), sessions longer than the run, no HTTP scenario at the same time. CPU in % of one core, steady state of
each hold (10 samples).

| Avatars | API CPU median / p90 / max | API memory | Web (Caddy) CPU median / max | Web memory | VM load max | Result |
|---|---|---|---|---|---|---|
| 100 | 26 / 30 / 39 % | 171 MiB | 15 / 22 % | 30 MiB | 1.3 | Pass: 17 % of the API limit |
| 250 | 74 / 76 / 82 % | 169 MiB | 42 / 50 % | 56 MiB | 1.4 | Pass: 49 % of the API limit; web at its 0.5 CPU limit at peaks |
| 500 | 154 / 168 / 169 % | 258 MiB | 58 / 60 % | 108 MiB | 3.4 | At the limit: API and web both at their CPU limits |
| 1,000 | 170 / 171 / 174 % | 400 MiB | 60 / 60 % | 204 MiB | 5.6 | Fail: overload (below) |

Whole run (k6 does not split the snapshot metrics by step): snapshot gap median 102 ms, p95 488 ms; join p95 5.7 s;
130 join failures, 11 dropped sockets, 1 kick; 503 of 1,499 guest creations failed (about 30 s each: the proxy stopped
waiting for the overloaded API; 2 connect timeouts in the 250 ramp, 1 in the 1,000 step); 996 sockets opened, 852 still open at the end. Thresholds fail. VM CPU per avatar
is about 3.5 times the laptop's (an A1 core is slower, and TLS through Caddy is added).

Conclusion: the VM carries about 250 avatars with headroom and about 500 at its CPU limit. The closed beta (about
15 players, 1 instance) uses about 4 % of one core. Before more than about 250 players at the same time in the
plaza: raise the web limit (Caddy terminates TLS for every socket), give the API more CPU, or move to a bigger VM.
1,001 test guests (created 13:23:40–13:35 UTC) were queued for deletion after the run.

