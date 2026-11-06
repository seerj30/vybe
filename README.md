# vybe

Browser-based social arcade with customizable 3D cartoon avatars.

- Specification: [docs/spec/VYBE_Technical_Specification_v1.3.md](docs/spec/VYBE_Technical_Specification_v1.3.md)

## Repository layout

```
packages/game-rules/   Street Hoops simulator (TypeScript reference) and golden fixtures
services/api/          ASP.NET Core solution; Vybe.GameRules is the C# simulator
docs/spec/             Product, game and technical specification
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

Regenerate the golden fixtures after a deliberate rules change, then review the diff:

```bash
pnpm --filter @vybe/game-rules generate:fixtures
```

A changed `expected` value is a rules change. It needs a new ruleset version (spec §4, `game_rulesets`).
