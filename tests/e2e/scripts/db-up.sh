#!/usr/bin/env bash
# Starts a throwaway PostgreSQL for the E2E tests and applies the API schema.
# Matches the default E2E_DATABASE_URL in playwright.config.ts.
set -euo pipefail
cd "$(dirname "$0")/../../.."

# E2E_PG_NAME and E2E_PG_PORT let several worktrees each have their own database.
name=${E2E_PG_NAME:-vybe-e2e-pg}
port=${E2E_PG_PORT:-55432}
docker rm -f "$name" >/dev/null 2>&1 || true
docker run -d --name "$name" -e POSTGRES_USER=vybe -e POSTGRES_PASSWORD=vybe -e POSTGRES_DB=vybe -p "$port":5432 postgres:17.6-alpine >/dev/null
until docker exec "$name" pg_isready -U vybe -d vybe >/dev/null 2>&1; do sleep 1; done
docker exec -i "$name" psql -q -v ON_ERROR_STOP=1 -U vybe -d vybe < services/api/db/schema.sql
echo "PostgreSQL ready: postgres://vybe:vybe@localhost:$port/vybe"
