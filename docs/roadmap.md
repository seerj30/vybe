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

## Streams

| Stream | Scope | Owns (may edit) | Gate tests | Status |
|---|---|---|---|---|
| **A. Challenges and quests** | J2 challenges: create, invite preview, accept, recipient session, completion, rematch, `/me/challenges`; outbox worker (`challenge-resolved`); sweepers (sessions, challenges, elapsed suspensions); daily quests: progress in the verification transaction, `/me/quests`, claim | `Features/Challenge*`, `Features/Quest*`, `Features/GameplayEndpoints.cs`, `Features/RewardService.cs`, `Jobs/` | E04, E07, quest claim | merged (review fixes in #17) |
| **B. Accounts and safety** | J3 account linking (Supabase); public profiles; blocks (cancel challenges under `PairLock`) and reports; saved outfits; J4 data export and deletion (anonymization, ADR 0002 #11); admin endpoints | `Features/IdentityEndpoints.cs`, `Features/{Account,Profile,Safety,Outfit,Privacy,Admin}*` | E03, E08, E12 | merged (security review fixes in #23) |
| **C. Web app on the API** | App shell and routes (§3), guest onboarding, avatar creator (2D fallback until 3D art), hub, Street Hoops on API sessions, shop, wardrobe, wallet; typed client from `packages/contracts`; browser E2E against the real API and PostgreSQL in CI | `apps/web`, `tests/e2e`, the e2e job in `.github/workflows/ci.yml` | E01, E02, E05, E06, E09, E10 in a browser | merged |
| **D. Operations** | Container image, deployment (hosting-agnostic until §17 #7), OpenTelemetry, structured logs, Sentry, CORS and security headers, per-player rate limits, idempotency cleanup, backup/restore and incident runbooks, load test | `services/api/Dockerfile`, `infra/`, `docs/runbooks/`, hosting hooks in `ServiceSetup.cs` | load/security review | next: ready to start (A and B merged); also sets `Auth:Supabase:ApiKey`, `Challenges:InviteSecret`, `ShareBaseUrl`, production export storage and content seeding |
| **E. Sign-off pack** | Options and recommendations for every §17 decision (age policy, guest lifetime, art, tuning, economy, profile visibility, hosting, legal wording) | `docs/decisions/` | — | merged; waiting for sign-off (record in `docs/decisions/README.md`) |
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
- Pushing: if `git push` hangs, the macOS keychain has no GitHub credentials; push with
  `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push -u origin HEAD`.
