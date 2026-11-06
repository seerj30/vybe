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

## Streams

| Stream | Scope | Owns (may edit) | Gate tests | Status |
|---|---|---|---|---|
| **A. Challenges and quests** | J2 challenges: create, invite preview, accept, recipient session, completion, rematch, `/me/challenges`; outbox worker (`challenge-resolved`); sweepers (sessions, challenges, elapsed suspensions); daily quests: progress in the verification transaction, `/me/quests`, claim | `Features/Challenge*`, `Features/Quest*`, `Features/GameplayEndpoints.cs`, `Features/RewardService.cs`, `Jobs/` | E04, E07, quest claim | started |
| **B. Accounts and safety** | J3 account linking (Supabase); public profiles; blocks (cancel challenges under `PairLock`) and reports; saved outfits; J4 data export and deletion (anonymization, ADR 0002 #11); admin endpoints | `Features/IdentityEndpoints.cs`, `Features/{Account,Profile,Safety,Outfit,Privacy,Admin}*` | E03, E08, E12 | queued (starts when F is merged) |
| **C. Web app on the API** | App shell and routes (§3), guest onboarding, avatar creator (2D fallback until 3D art), hub, Street Hoops on API sessions, shop, wardrobe, wallet; typed client from `packages/contracts`; browser E2E against the real API and PostgreSQL in CI | `apps/web`, `tests/e2e`, the e2e job in `.github/workflows/ci.yml` | E01, E02, E05, E06, E09, E10 in a browser | started |
| **D. Operations** | Container image, deployment (hosting-agnostic until §17 #7), OpenTelemetry, structured logs, Sentry, CORS and security headers, per-player rate limits, idempotency cleanup, backup/restore and incident runbooks, load test | `services/api/Dockerfile`, `infra/`, `docs/runbooks/`, hosting hooks in `ServiceSetup.cs` | load/security review | queued (after A and B) |
| **E. Sign-off pack** | Options and recommendations for every §17 decision (age policy, guest lifetime, art, tuning, economy, profile visibility, hosting, legal wording) | `docs/decisions/` | — | queued |
| **F. API extras from PR #7** | IPv6 /64 rate-limit grouping; midnight and multi-level reward tests | `ServiceSetup.cs` (rate limiter only), `tests/Vybe.Api.Tests` | — | running |

## Rules for every stream

- Branch from `origin/master`; one PR per coherent change; do not merge your own PR.
- Edit only the files your stream owns. Shared files (`ServiceSetup.cs`, `Vybe.slnx`, `README.md`,
  `pnpm-lock.yaml`): add only your own lines; on conflict, merge `origin/master` and keep both sides.
- Contract or schema change: update `packages/contracts` (lint, regenerate types) or `Vybe.Data`
  (new migration, regenerate `db/schema.sql`) in the same PR, and say so in the PR body.
- Use `PairLock` (`services/api/src/Vybe.Api/Infrastructure/PairLock.cs`) for spec invariant 12.
- Before opening a PR: `dotnet build services/api -c Release`, `dotnet format services/api/Vybe.slnx --verify-no-changes`,
  `dotnet test services/api`, `pnpm -r typecheck`, `pnpm -r test`.
- Pushing: if `git push` hangs, the macOS keychain has no GitHub credentials; push with
  `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push -u origin HEAD`.
