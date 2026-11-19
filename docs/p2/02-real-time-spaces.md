# P2 area 2: Real-time spaces

- Spec: [§1 deferred "live multiplayer, open world"](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§11](../spec/VYBE_Technical_Specification_v1.3.md#11-security-and-privacy), [§13](../spec/VYBE_Technical_Specification_v1.3.md#13-deployment-and-operations), [§16 P2](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates)
- Evidence: `infra/oracle/compose.yml`, `infra/oracle/Caddyfile`, [ADR 0009](../adr/0009-oracle-vm.md), [load-test runbook](../runbooks/load-test.md), [decision 7](../decisions/07-hosting.md)
- Signs off: product owner; engineering lead
- Needed before: any real-time code

> Capacity numbers below are **estimates** unless the text names a measurement. Prices change; check vendor pages on the decision date.

## Question

Which shared 3D space does VYBE build first, and where does the real-time traffic run?

## Facts from the repository

| Fact | Source |
|---|---|
| One VM: 2 OCPU / 12 GB ARM (Always Free limit 4 OCPU / 24 GB). API container limit 1.5 CPU, 2 GB | `compose.yml`, `docs/runbooks/oracle-vm.md` |
| Measured: 14.6 HTTP requests/s, read p95 121 ms, write p95 161 ms, API at most 32 % of one core | `load-test.md` (2026-11-11) |
| Caddy proxies `/api` on the same origin, HTTP/1.1 and HTTP/2 only, idle timeout 2 min | `Caddyfile` |
| One API instance, no zero-downtime deploy: a deploy restarts the API | ADR 0009 "Not done" |
| No real-time code today. ASP.NET Core SignalR is part of the shared framework (no new package) | `services/api` |
| Closed-beta peak estimate: 15 concurrent players | decision 7 model |
| Avatar art: code-built glTF, shared rig, emotes (cheer, jump, Swish) | stream N, #70 |
| My Room exists per player, with a profile view | stream Q |

## Scope options

| | Option | Player value | Risk | Effort (streams) |
|---|---|---|---|---|
| **S1** | **Visit a friend's My Room live** (owner + up to 7 friends; presence, walk, emotes) | Medium: social, small | Low: friends only, small rooms | 1.5 |
| **S2** | **Public plaza**, sharded into instances of max. 16 avatars; walk, emotes, presence | High: "a place to meet" | Medium: strangers meet; needs chat level of [01-chat](01-chat.md) | 2 |
| **S3** | S2 + S1 + mini-game lobby (start a challenge from the plaza) | Highest | Medium | 3 |
| **S4** | Open world | — | High (spec §1 non-goal) | not estimated |

## Architecture options

| | Option | What it is | Fit | Cost/month | Effort (streams) | Prerequisites |
|---|---|---|---|---|---|---|
| **A** | **SignalR (WebSockets) in the existing API** | A hub in `Vybe.Api`; room state in memory; auth with the same guest cookie or bearer token | Best: one process, one deploy, same auth, same origin. One instance only (no backplane needed) | USD 0 | 2 (S2) | WebSocket load test; reconnect on deploy |
| **B** | **Separate real-time service on the same VM** (a second container, .NET or Node) | Own process; validates a short-lived ticket from the API | Good: a crash or GC pause does not hit the HTTP API. Second image, second deploy path | USD 0 (VM has 0.5 OCPU free; second VM free) | 3 | Ticket design; compose and deploy changes |
| **C** | **Hosted real-time service** (for example Supabase Realtime, Ably, Pusher) | Vendor relays messages; API signs channel tokens | No server code for fan-out. New processor (DPA, privacy notice). Free tiers have low connection and message caps (Supabase Free: about 200 connections, 2 M messages/month — check) | USD 0 at beta; USD 25–100+ at scale (estimate) | 2 | DPA; privacy notice; vendor limits fit |

## Capacity on the current VM (estimate)

Model: server-authoritative snapshots at **10 Hz**; one snapshot lists every avatar in the instance (about 16 bytes binary or 60 bytes JSON each).

| Item | 16-avatar instance | 1,000 players (63 instances) |
|---|---|---|
| Messages out | 160/s | ~10,000/s |
| Bytes out (binary / JSON) | 41 KB/s / 154 KB/s | 2.6 MB/s / 9.6 MB/s (~21 / ~77 Mbit/s) |
| Messages in (10 Hz input) | 160/s | ~10,000/s |
| Data per player per hour (download, binary) | ~9 MB | — |

- **Estimate:** option A carries 500–1,000 concurrent space players in 1.5 OCPU with binary MessagePack; JSON halves that. This is 30–60 times the beta peak. Memory is not the limit (tens of KB per connection).
- Egress: Oracle Always Free includes 10 TB/month outbound (check). 100 average concurrent players use about 0.7 TB/month.
- The HTTP API shares the CPU: keep run verification p95 inside the [§13](../spec/VYBE_Technical_Specification_v1.3.md#13-deployment-and-operations) SLO during the space test.

### How to load-test

1. Add a k6 WebSocket scenario (`k6/websockets`): SignalR handshake, join an instance, send input at 10 Hz, count snapshots.
2. Run it locally first (runbook rule: local targets only).
3. Step up: 100, 250, 500, 1,000 virtual players, with the existing HTTP scenarios at full rate at the same time.
4. Pass: snapshot interval p95 < 150 ms, HTTP thresholds of the runbook still pass, API CPU < 70 %, no dropped connections.
5. Run on production only with owner approval (runbook), before the first real players in spaces.

## State authority and anti-cheat

| Rule | Reason |
|---|---|
| Server owns membership, instance, position; client sends intent (direction, emote id) | No teleport or speed hacks visible to others |
| Server clamps speed and bounds; drops input above 15 msg/s; disconnects at a repeated excess | Abuse and CPU protection |
| Spaces give **no** coins, XP or items | No reward to farm; game runs stay on the verified HTTP path |
| Blocks apply: a blocked pair never shares an instance (server moves one of them) | Spec invariant 8 intent; block privacy |
| Emotes from the owned set only; emote rate limit (1 per 2 s) | Spam |
| Suspended and `deleting` players cannot join | Same rules as HTTP |

## Avatar sync budget and mobile networks

| Item | Budget |
|---|---|
| Tick | 10 Hz snapshot, client interpolation buffer 100–150 ms |
| Per avatar | position x/z, heading, animation state, emote id: ≤ 16 bytes binary |
| Outfit | sent once on join (item ids); the client loads cached glTF art |
| Mobile | ~9 MB/hour download; automatic reconnect with backoff; resume within 30 s keeps the slot; hidden tab sends no input |
| Deploy | a restart drops all sockets: clients reconnect; announce deploys outside peak |

## Privacy (decision 8)

Presence is new personal data: who is online, where, with whom. Proposal: keep presence in memory only; no position history in the database; logs without positions. The privacy notice needs a new row (to confirm with counsel). Profile privacy ([decision 6](../decisions/06-profile-visibility.md)): a "private" player shows as a generic avatar name in public instances (proposal).

## Recommendation

**Scope S2 (public plaza, 16 per instance) with option A (SignalR in the existing API), binary protocol, no free text** (chat level A of [01-chat](01-chat.md)). Add S1 (friend room visits) in the same stream if time allows.

- Option A costs USD 0, reuses auth, same origin and deploy, and the estimate gives 30–60 times the beta peak.
- Move to option B only when the load test shows that spaces hurt the HTTP SLO.
- Do not use option C now: a new processor and low free caps for no gain at this size.

## After sign-off

| Change | Area | Owner |
|---|---|---|
| ADR 0010: real-time protocol, ticks, authority, limits | `docs/adr/` | new stream |
| Hub, instance manager, contract for messages | `services/api` (new `Features/Spaces*`) | new stream |
| Plaza scene, interpolation, reconnect | `apps/web` | new stream |
| k6 WebSocket scenario and results | `infra/load/`, `docs/runbooks/load-test.md` | new stream |
| Spec new version (§1 deferred list, §5, §11 presence) | spec | orchestrator |
