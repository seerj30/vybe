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

## P0 social beta gate (spec §16)

| Gate item | Status |
|---|---|
| Critical browser E2E (E01–E12) | In CI on every PR and on master (`tests/e2e/specs`) |
| Load review | Local 5-minute k6 run passed (`docs/runbooks/load-test.md`); repeat on the chosen hosting |
| Security review | Independent reviews of streams A, B, D, G, H, J, K; all findings fixed (#17, #23, #29, #30, #31, #35, #36) |
| Telemetry, backup/restore, incident runbook | Merged (stream D, `docs/runbooks/`, ADR 0006) |
| Privacy policy and age policy | Signed 2026-11-07 (decisions 1, 6, 8; spec v1.4). Code done (#35); drafts in `docs/legal/`. Open: counsel country list and final notice |
| Hosting, deployment, production secrets | **Changed 2026-11-10: Oracle Cloud Always Free ARM VM (no hosting cost), stream M.** Oracle (Free Tier, not upgraded; idle stop possible, restart in the console): tenancy `molnarsergiuionut`, home region Frankfurt; network `vybe-vcn` (ingress 22, 80, 443 only); VM `vybe-prod` VM.Standard.A1.Flex 2 OCPU / 12 GB, Ubuntu 24.04 aarch64, public IPv4 `141.144.232.12` (ephemeral), host key `SHA256:jXELn+UFxVc1RVG/M4xrcylDjvh/49NY64D9M+LLXEM`; SSH keys `vybe-admin` and `vybe-deploy` on the product owner's Mac. #43 merged; bootstrap done on 2026-11-10 (Docker 29.9, ufw 22/80/443, fail2ban, SSH keys only, restricted `vybe-deploy` user, swap, monitor timer; rebooted and checked). **Live since 2026-11-10 at https://141-144-232-12.sslip.io** (release 754c42f, deploy run 42796440962; smoke test passed: TLS Let's Encrypt, security headers, catalog, guest with age gate, under-18 refused). Ephemeral IP kept (stays through stops). Two first-deploy problems fixed on the VM: an automatic kernel update broke `veth` until a reboot, and the transaction pooler needs `No Reset On Close=true` in `ConnectionStrings__Vybe` (scripts and runbooks still to update). Still open: monitoring (heartbeat, uptime), weekly backup (`vybe_backup` role, age key), Code Owners branch rule, own domain before real players (some ISP resolvers rewrite sslip.io). Fly.io config (#36, ADR 0008) removed on 2026-11-10 (in the git history); its apps have no machines (USD 0). Done: Fly.io organization `vybe-prod` with billing; region `fra` (#38); apps `vybe-prod-api` and `vybe-prod-web` with no machines and no IPs (no cost). Supabase (Free, 2026-11-10): organization `vybe`, project `vybe-prod` (ref `pkrudxjeihxrkkdlfdmr`, `eu-central-1`, Nano), ES256 signing key active, Data API off, Auth Site URL and redirect `https://vybe-prod-web.fly.dev`, roles `vybe_owner` and `vybe_api` (passwords set by the product owner, checked 2026-11-10), private bucket `vybe-exports`, S3 endpoint `https://pkrudxjeihxrkkdlfdmr.storage.supabase.co/storage/v1/s3`, pooler host `aws-0-eu-central-1.pooler.supabase.com`. Fly.io setup state when it was removed: two-factor authentication on the Fly.io account (check), steps 2–6 are done (role passwords and the S3 access key `vybe-api` are in the product owner's password manager); custom SMTP for Auth emails before real sign-ups (upgrade to Pro before real beta players, roles, connection strings, export bucket), 8–12 (secrets, GitHub `production` environment, release dry run, grants), then the first deploy. The owner is the product owner's personal Fly.io account (no company): keep two-factor authentication and recovery codes, and add a second member for on-call before the beta |
| 3D avatar art | Signed 2026-11-07 (decision 3: contractor work-for-hire). Open: contract and production (people); the 2D fallback is in use |
| Low-effort run fix, review flags, catalog prices (decisions 4, 5) | Signed 2026-11-07. Code done (#34). Open: the catalog has 7 priced items (5 common, 2 rare, 0 epic); 23 more priced items (with epics) are necessary to reach 30 at beta. They need art (decision 3) |
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
