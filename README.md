# vybe

Browser-based social arcade with customizable 3D cartoon avatars.

- Specification: [docs/spec/VYBE_Technical_Specification_v1.3.md](docs/spec/VYBE_Technical_Specification_v1.3.md)

## Repository layout

```
apps/web/              Web app on the API (Angular + Three.js); /practice keeps the client-only Street Hoops prototype
packages/contracts/    OpenAPI 3.1 contract (source of truth for the API) and generated TS types
packages/game-rules/   Street Hoops simulator (TypeScript reference) and golden fixtures
services/api/          ASP.NET Core solution: Vybe.Api (HTTP API), Vybe.Data (EF Core), Vybe.GameRules (C# simulator)
assets/manifests/      Versioned content: avatar presets and catalog
tests/e2e/             Playwright browser tests for apps/web against the real API and PostgreSQL (Chromium, desktop and mobile touch)
docs/spec/             Product, game and technical specification
docs/adr/              Architecture decision records
```

API (P0 vertical slice: guests, avatar, Street Hoops sessions, catalog, purchases, equipment, wallet):

```bash
export ConnectionStrings__Vybe="Host=localhost;Database=vybe;Username=vybe;Password=<local password>"
cd services/api
VYBE_DB_CONNECTION="$ConnectionStrings__Vybe" dotnet ef database update --project src/Vybe.Data
dotnet run --project src/Vybe.Api            # Development seeds the ruleset and catalog
dotnet test tests/Vybe.Api.Tests             # needs Docker; checks every response against the contract
```

Database (PostgreSQL, EF Core migrations in `services/api/src/Vybe.Data`):

```bash
dotnet tool restore --tool-manifest services/api/dotnet-tools.json
dotnet test services/api/tests/Vybe.Data.Tests      # needs Docker
```

After a model change, add a migration and regenerate the review script:

```bash
cd services/api
dotnet ef migrations add <Name> --project src/Vybe.Data --output-dir Migrations
dotnet ef migrations script --project src/Vybe.Data --output db/schema.sql
```

After a contract change, lint it and regenerate the types:

```bash
pnpm --filter @vybe/contracts lint
pnpm --filter @vybe/contracts generate
```

The C# and TypeScript simulators must produce identical results. Both test suites
run every file in `packages/game-rules/fixtures/`.

## Requirements

- Node.js 22.18 or later (runs TypeScript scripts directly)
- pnpm 12
- .NET SDK 10

## Commands

```bash
pnpm install
pnpm test                    # TypeScript tests
pnpm typecheck
dotnet test services/api     # C# tests
```

Browser tests (start the API on port 5246 and the web dev server on port 4321 for you; need Docker and
the .NET SDK; set `E2E_DATABASE_URL` to use another PostgreSQL with `services/api/db/schema.sql` applied):

```bash
pnpm --filter @vybe/e2e exec playwright install chromium
pnpm --filter @vybe/e2e db:up        # throwaway PostgreSQL on port 55432 with the schema
pnpm --filter @vybe/e2e test:e2e
```

The browser tests also start a fake Supabase Auth on port 5290 (`tests/e2e/fake-auth`, tests only;
`E2E_AUTH_PORT` sets another port). It issues the tokens for sign-in, linking and admin roles.

Regenerate the golden fixtures after a deliberate rules change, then review the diff:

```bash
pnpm --filter @vybe/game-rules generate:fixtures
```

A changed `expected` value is a rules change. It needs a new ruleset version (spec §4, `game_rulesets`).
