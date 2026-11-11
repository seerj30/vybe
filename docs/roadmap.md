# Roadmap and work streams

Status of the spec §16 delivery plan. Each stream is one Claude Code session that works on its own
branch and opens PRs. The orchestrator session reviews, tests and merges them.

## Done

| Item | Where |
|---|---|
| Spec v1.3 (+ v1.3.1–v1.3.4) | `docs/spec/` |
| Simulator TS + C#, golden fixtures | `packages/game-rules`, `services/api/src/Vybe.GameRules` |
| OpenAPI contract, generated types | `packages/contracts` (ADR 0001) |
| PostgreSQL schema, migrations | `services/api/src/Vybe.Data` (ADR 0002) |
| CI | `.github/workflows/ci.yml` |
| API P0 slice: guests, avatar, sessions, catalog, purchase, equip, wallet (E01, E02, E05, E06) | `services/api/src/Vybe.Api` (ADR 0003) |
| Playable Street Hoops prototype, Playwright (E11) | `apps/web`, `tests/e2e` (PRs #1–#6) |
| Stream F: IPv6 /64 rate-limit grouping, reward edge tests | #8 |
| Stream A: J2 challenges, outbox worker, sweepers, daily quests (E04, E07) | #10, #16 (ADR 0004) |
| Stream C: web app on the API — client, shell, onboarding, avatar, gameplay, shop, wardrobe, wallet; browser E2E against the real API (E01, E02, E05, E06, E09, E10) | #9, #11, #14 |
| Stream A review fixes: jobs, start-up checks, lock order | #17 |
| Stream E: sign-off pack for the §17 decisions (not signed yet) | #20 (`docs/decisions/`) |
| Stream B: J3 account linking, profiles, blocks, reports, saved outfits, J4 export and deletion, admin (E03, E08, E12) | #18, #19, #21 (ADR 0005) |
| Stream B security review fixes | #23 |
| Configurable E2E ports and database for parallel worktrees | #25 |
| Stream D: container image, migrate/seed commands, production configuration check, S3 export storage, health checks, OpenTelemetry, logs, Sentry, CORS, security headers, per-player limits, idempotency cleanup, runbooks, k6 load test | #24, #26, #27 (ADR 0006) |
| Stream D security review fixes: deletion log and `replay-deletions` after a restore, telemetry scrubbing, proxy trust, read limits, restore and grants | #31 |
| Stream G: web challenges, invites, daily quests, profiles (report, block), saved outfits; E04, E07 in a browser | #29 |
| Stream H: web sign-in (Supabase, PKCE), J3 linking, settings (blocks, export, deletion), deletion status, admin; fake Auth for E2E; E03, E08, E12 in a browser | #30 |
| Flaky retry test fix | #33 |
| Stream I: full-length run and score floor for rewards, review flags, catalog price bands (decisions 4, 5) | #34 |
| Stream J: age gate (birth month and year), sliding guest lifetime and cleanup, profile visibility, J4 Supabase Auth user deletion, legal drafts (decisions 1, 2, 6, 8) | #35 |
| Stream K: Fly.io deployment — API and web (Caddy) configs, same-origin proxy, deploy workflow with the release step outside Fly, runbooks (decision 7) | #36 (ADR 0008) |
| Stream N: code-built 3D avatar and catalog art, 3D viewport, art cache | #51, #52, #53; previewUrl from thumbnails #54, #61; viewport layout fix #60 |
| Stream O (P1): friends and weekly leaderboard per game | #56 (orchestrator fix: friend requests wait for a running deletion) |
| Stream P (P1): second mini-game Street Juggle, game switch on the leaderboard | #59 (orchestrator: two-thumb input fix, leaderboard hook, hub baselines) |
| Stream Q (P1): My Room 3D, 31 room items and 13 avatar items (catalog version 3) | #57, #58, #62 (merge commit dbada2f) |
| Stream R (P1): advanced achievements, badges on profiles, one-time backfill per rules version | #65 |
| Phone navigation fits 375 px (five items; Wardrobe, My Room, Wallet on the hub), flaky test fix | #64, #63 |
| Street Juggle `timing_spread_low` review flag (anti-bot), Npgsql GSS off (no libgssapi log noise), body B art pass (stream S), flaky room touch test | #66, #67, #68, #69 |

## P0 social beta gate (spec §16)

| Gate item | Status |
|---|---|
| Critical browser E2E (E01–E12) | In CI on every PR and on master (`tests/e2e/specs`) |
| Load review | Local 5-minute k6 run passed; production runs on the Oracle VM + Supabase Free passed on 2026-11-11: half rate, then the full rate on the P1 release 90628e1 (read p95 121 ms, write p95 161 ms, 0 % failures, API at most 32 % of one core; `docs/runbooks/load-test.md`). Repeat after large changes to the run verification path or before a public launch |
| Security review | Independent reviews of streams A, B, D, G, H, J, K; all findings fixed (#17, #23, #29, #30, #31, #35, #36) |
| Telemetry, backup/restore, incident runbook | Merged (stream D, `docs/runbooks/`, ADR 0006) |
| Privacy policy and age policy | Signed 2026-11-07 (decisions 1, 6, 8; spec v1.4). Code done (#35); drafts in `docs/legal/`. Open: counsel country list and final notice |
| Hosting, deployment, production secrets | **Changed 2026-11-10: Oracle Cloud Always Free ARM VM (no hosting cost), stream M.** Oracle (Free Tier, not upgraded; idle stop possible, restart in the console): tenancy `molnarsergiuionut`, home region Frankfurt; network `vybe-vcn` (ingress 22, 80, 443 only); VM `vybe-prod` VM.Standard.A1.Flex 2 OCPU / 12 GB, Ubuntu 24.04 aarch64, public IPv4 `141.144.232.12` (ephemeral), host key `SHA256:jXELn+UFxVc1RVG/M4xrcylDjvh/49NY64D9M+LLXEM`; SSH keys `vybe-admin` and `vybe-deploy` on the product owner's Mac. #43 merged; bootstrap done on 2026-11-10 (Docker 29.9, ufw 22/80/443, fail2ban, SSH keys only, restricted `vybe-deploy` user, swap, monitor timer; rebooted and checked). **Live since 2026-11-10 at https://141-144-232-12.sslip.io**; latest release 90628e1 on 2026-11-11 (deploy run 43008936717: anti-bot flag, body B, log cleanup; before it 3634733: My Room, achievements, phone nav; before it 7fee72c: friends, leaderboard, Street Juggle). First release 754c42f, deploy run 42796440962; smoke test passed: TLS Let's Encrypt, security headers, catalog, guest with age gate, under-18 refused). Ephemeral IP kept (stays through stops). Two first-deploy problems fixed on the VM: an automatic kernel update broke `veth` until a reboot, and the transaction pooler needs `No Reset On Close=true` in `ConnectionStrings__Vybe` (fixed in the repository: #45 makes the API refuse a port 6543 string without it, and `vybe-deploy` stops on a pending kernel reboot). The VM runs Ubuntu 26.04 LTS since an unplanned `do-release-upgrade` on 2026-11-10; kept on purpose, `bootstrap.sh` supports 24.04 and 26.04 and sets `Prompt=never` (#47). Uptime check from GitHub every 15 minutes with a `production-down` issue (#46). Weekly encrypted backup on since 2026-11-10 (#49; read-only role `vybe_backup`, age key with the product owner, first dump `backups/vybe-20261110T105316Z.dump.age`, Sundays 03:17 UTC). Restore drill passed on 2026-11-10 (decrypt and `pg_restore`, 26 tables). Decided 2026-11-10: no own domain yet (email through Resend comes with the domain), no paid tiers yet, Code Owners rule off until a second person. Still open: VM heartbeat and backup heartbeat (healthchecks.io account) Code Owners branch rule, own domain before real players (some ISP resolvers rewrite sslip.io). Fly.io config (#36, ADR 0008) removed on 2026-11-10 (in the git history); the never-used Fly.io apps `vybe-prod-api` and `vybe-prod-web` and the organization `vybe-prod` were deleted the same day. Supabase (Free, 2026-11-10): organization `vybe`, project `vybe-prod` (ref `pkrudxjeihxrkkdlfdmr`, `eu-central-1`, Nano), ES256 signing key active, Data API off, Auth Site URL `https://141-144-232-12.sslip.io` and redirect `https://141-144-232-12.sslip.io/` (checked 2026-11-10), roles `vybe_owner` and `vybe_api` (passwords set by the product owner, checked 2026-11-10), private bucket `vybe-exports`, S3 endpoint `https://pkrudxjeihxrkkdlfdmr.storage.supabase.co/storage/v1/s3`, pooler host `aws-0-eu-central-1.pooler.supabase.com`. Fly.io setup state when it was removed: two-factor authentication on the Fly.io account (check), steps 2–6 are done (role passwords and the S3 access key `vybe-api` are in the product owner's password manager); custom SMTP for Auth emails before real sign-ups (upgrade to Pro before real beta players, roles, connection strings, export bucket), 8–12 (secrets, GitHub `production` environment, release dry run, grants), then the first deploy. The owner is the product owner's personal Fly.io account (no company): keep two-factor authentication and recovery codes, and add a second member for on-call before the beta |
| 3D avatar art | Decision 3 amended 2026-11-10: code-built art now (stream N: Blender scripts → glTF, rig, 2 bodies, slots, ~30 items, emotes, Three.js viewport); contractor later on the same rig. Merged 2026-11-10 (#51 pipeline and 43-item catalog v2, #52 viewport in creator, hub, wardrobe and shop with 2D fallback, #53 immutable art cache). previewUrl fixed (#54). Body B redone as a sturdy build (#68, stream S). Open: at the peak of the "cheer" and "jump" emotes the raised hands cover the face (both bodies) |
| Low-effort run fix, review flags, catalog prices (decisions 4, 5) | Signed 2026-11-07. Code done (#34). Catalog version 2 (#51, 2026-11-10) has 40 priced items (26 common, 9 rare, 5 epic) |
| Guest lifetime, age gate, profile visibility, Auth user deletion (decisions 1, 2, 6, 8) | Signed 2026-11-07. Code done (#35) |

## Streams

| Stream | Scope | Owns (may edit) | Gate tests | Status |
|---|---|---|---|---|
| **A. Challenges and quests** | J2 challenges: create, invite preview, accept, recipient session, completion, rematch, `/me/challenges`; outbox worker (`challenge-resolved`); sweepers (sessions, challenges, elapsed suspensions); daily quests: progress in the verification transaction, `/me/quests`, claim | `Features/Challenge*`, `Features/Quest*`, `Features/GameplayEndpoints.cs`, `Features/RewardService.cs`, `Jobs/` | E04, E07, quest claim | merged (review fixes in #17) |
| **B. Accounts and safety** | J3 account linking (Supabase); public profiles; blocks (cancel challenges under `PairLock`) and reports; saved outfits; J4 data export and deletion (anonymization, ADR 0002 #11); admin endpoints | `Features/IdentityEndpoints.cs`, `Features/{Account,Profile,Safety,Outfit,Privacy,Admin}*` | E03, E08, E12 | merged (security review fixes in #23) |
| **C. Web app on the API** | App shell and routes (§3), guest onboarding, avatar creator (2D fallback until 3D art), hub, Street Hoops on API sessions, shop, wardrobe, wallet; typed client from `packages/contracts`; browser E2E against the real API and PostgreSQL in CI | `apps/web`, `tests/e2e`, the e2e job in `.github/workflows/ci.yml` | E01, E02, E05, E06, E09, E10 in a browser | merged |
| **D. Operations** | Container image, deployment (hosting-agnostic until §17 #7), OpenTelemetry, structured logs, Sentry, CORS and security headers, per-player rate limits, idempotency cleanup, backup/restore and incident runbooks, load test | `services/api/Dockerfile`, `infra/`, `docs/runbooks/`, hosting hooks in `ServiceSetup.cs` | load/security review | merged (security review fixes in #31) |
| **E. Sign-off pack** | Options and recommendations for every §17 decision (age policy, guest lifetime, art, tuning, economy, profile visibility, hosting, legal wording) | `docs/decisions/` | — | merged; waiting for sign-off (record in `docs/decisions/README.md`) |
| **G. Web social** | `/challenges`, `/challenge/:token`, daily quests on `/hub`, `/profile/:handle` (challenge, report, block), saved outfits in `/wardrobe`; browser E2E | `apps/web/src/app/{challenges,quests,profiles,outfits}/`, `pages/{challenges,invite,profile,hub,wardrobe}.ts`, `api/social-api.ts`, small hooks in `street-hoops/`, its specs | E04, E07, quest claim in a browser | merged (review fixes in #29) |
| **H. Web account** | Supabase sign-in, J3 linking, `/settings` (blocks, export, deletion), deletion status, `/admin`; fake Supabase Auth for E2E | `apps/web/src/app/{auth,settings,admin}/`, `pages/{landing,settings,admin,deletion-status}.ts`, `api/account-api.ts`, bearer hook in `api-client.ts`, `tests/e2e/fake-auth/`, its specs and config lines | E03, E08, E12 in a browser | merged (two security reviews, fixes in #30) |
| **I. Rewards and economy (decisions 4, 5)** | `minRunMsForRewards` and score floor for coins (TS + C# simulator config, fixtures, checksum, contract, API check), `review_flags` writer, catalog repricing, web messages | `packages/game-rules`, `services/api/src/Vybe.GameRules`, `Features/{Rewards,RewardService,GameplayEndpoints}*`, `assets/manifests/catalog.v1.json`, `apps/web/src/app/street-hoops/` | reward and parity tests, E2E gameplay | merged (review fixes in the PR) |
| **J. Account policies (decisions 1, 2, 6, 8)** | Age gate before guest creation, sliding guest lifetime and cleanup, `guestExpiresAt`, profile visibility and private toggle, J4 deletes the Supabase Auth user | `Infrastructure/Authentication.cs`, `Features/{IdentityEndpoints,Profile,Privacy}*`, `Jobs/` (guest cleanup), `Vybe.Data` (migration), web landing/onboarding/hub/settings | E01, E03, E12, profile tests | merged (review fixes in the PR) |
| **K. Fly.io deployment (decision 7)** | API and web (Caddy) Fly configs, same-origin proxy, deploy workflow, Fly runbook sections | `infra/fly/**`, `infra/web/**`, `.github/workflows/deploy.yml`, Fly sections of `docs/runbooks/*` | deploy-config CI job | merged (security review fixes in the PR) |
| **F. API extras from PR #7** | IPv6 /64 rate-limit grouping; midnight and multi-level reward tests | `ServiceSetup.cs` (rate limiter only), `tests/Vybe.Api.Tests` | — | merged |
| **O. Friends and leaderboard (P1)** | Friends, weekly leaderboard per `gameId` | `Features/{Friend,Leaderboard}Endpoints.cs`, `pages/{friends,leaderboard}.ts` | E13–E15 | merged #56, live 2026-11-11 |
| **P. Second mini-game (P1)** | Street Juggle: TS + C# simulators, verification, web game | `packages/game-rules` (juggle), `Vybe.GameRules` (juggle), `street-juggle/` | parity, E2E run | merged #59, live 2026-11-11. Accepted risk: timing bot on the board (P1 decisions) |
| **Q. My Room 3D (P1)** | Room art and catalog v3, room API, editor and profile room | `assets/source` (room), room features and pages | room E2E | merged (dbada2f), live 2026-11-11 |
| **R. Achievements (P1)** | Achievements, badges on profiles | achievements features and pages | achievement E2E | merged #65, live 2026-11-11. Achievement XP kept (P1 decisions) |

## Rules for every stream

- Branch from `origin/master`; one PR per coherent change; do not merge your own PR.
- Stacked PRs: the orchestrator merges the top of the stack with a merge commit (it contains the
  lower PRs unchanged), so never delete a base branch while PRs still target it.
- Edit only the files your stream owns. Shared files (`ServiceSetup.cs`, `Vybe.slnx`, `README.md`,
  `pnpm-lock.yaml`): add only your own lines; on conflict, merge `origin/master` and keep both sides.
- Contract or schema change: update `packages/contracts` (lint, regenerate types) or `Vybe.Data`
  (new migration, regenerate `db/schema.sql`) in the same PR, and say so in the PR body.
- Use `PairLock` (`services/api/src/Vybe.Api/Infrastructure/PairLock.cs`) for spec invariant 12.
- Before opening a PR: `dotnet build services/api -c Release`, `dotnet format services/api/Vybe.slnx --verify-no-changes`,
  `dotnet test services/api`, `pnpm -r typecheck`, `pnpm -r test`.
- Browser E2E in parallel worktrees: use your own `E2E_WEB_PORT`, `E2E_API_PORT`, `E2E_PG_PORT` and
  `E2E_PG_NAME` (G: 4331/5256/55433/vybe-e2e-g, H: 4341/5266/55434/vybe-e2e-h, D: 4351/5276/55435/vybe-e2e-d).
  Never stop or remove another stream's containers.
- Pushing: if `git push` hangs, the macOS keychain has no GitHub credentials; push with
  `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push -u origin HEAD`.
