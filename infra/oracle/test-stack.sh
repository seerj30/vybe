#!/usr/bin/env bash
# Runs infra/oracle/compose.yml with given images against a throwaway PostgreSQL, with dummy settings and Caddy's
# internal CA for "localhost" (no Let's Encrypt), then checks the proxy, the headers and the client address.
# CI (job "oracle-config") and local runs use it; it never touches /etc/vybe or /var/lib/vybe.
#
#   infra/oracle/test-stack.sh <api image> <web image>     for example: vybe-api:ci vybe-web:ci
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: test-stack.sh <api image> <web image>" >&2; exit 2; }
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PG_IMAGE="postgres:17.6-alpine@sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94"
readonly HTTP_PORT="${VYBE_TEST_HTTP_PORT:-8480}" HTTPS_PORT="${VYBE_TEST_HTTPS_PORT:-8443}"
# A '$' in the password: compose must read the env file values literally (single quotes).
# shellcheck disable=SC2016 # a literal '$', on purpose
readonly DB_PASSWORD='test$only-not-a-secret'

work=$(mktemp -d)
chmod 755 "$work"
export VYBE_ENV_DIR="$work/env" VYBE_CADDY_DATA="$work/caddy" VYBE_HTTP_PORT="$HTTP_PORT" VYBE_HTTPS_PORT="$HTTPS_PORT"
mkdir -p "$VYBE_ENV_DIR" "$VYBE_CADDY_DATA"
# The web image runs as UID 10001 (bootstrap.sh gives the directory to that user on the VM).
chmod 777 "$VYBE_CADDY_DATA"
printf 'VYBE_API_IMAGE=%s\nVYBE_WEB_IMAGE=%s\nVYBE_RELEASE=%s\n' "$1" "$2" "$(printf '0%.0s' {1..40})" > "$work/release.env"

compose() { docker compose -p vybe -f "$HERE/compose.yml" -f "$work/compose.test.yml" --env-file "$work/release.env" "$@"; }
cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then compose ps -a || true; compose logs --tail 60 || true; fi
  compose down --volumes --timeout 5 >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

cat > "$work/compose.test.yml" <<EOF
services:
  db:
    image: $PG_IMAGE
    environment:
      POSTGRES_USER: vybe
      POSTGRES_PASSWORD: '${DB_PASSWORD//\$/\$\$}'
      POSTGRES_DB: vybe
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U vybe -d vybe"]
      interval: 2s
      retries: 30
    networks: [vybe]
EOF

cat > "$VYBE_ENV_DIR/api.env" <<EOF
ConnectionStrings__Vybe='Host=db;Database=vybe;Username=vybe;Password=$DB_PASSWORD;Max Auto Prepare=0'
Auth__Supabase__Authority='https://auth.example.invalid/auth/v1'
Auth__Supabase__ApiKey='dummy-anon-key-for-tests'
Auth__Supabase__ServiceRoleKey='dummy-service-role-key-for-tests'
Challenges__InviteSecret='$(openssl rand -base64 32)'
Challenges__ShareBaseUrl='https://localhost'
Storage__Exports__Bucket='vybe-exports'
Storage__Exports__Region='eu-central-1'
Storage__Exports__ServiceUrl='https://storage.example.invalid/storage/v1/s3'
Storage__Exports__AccessKeyId='dummy'
Storage__Exports__SecretAccessKey='dummy'
EOF
cat > "$VYBE_ENV_DIR/web.env" <<'EOF'
VYBE_PUBLIC_HOST='localhost'
VYBE_SUPABASE_URL='https://example.supabase.co'
VYBE_SUPABASE_ANON_KEY='dummy-anon-key-for-tests'
EOF
chmod 600 "$VYBE_ENV_DIR"/*.env

compose config --quiet
compose up -d --wait db
# The release step, as the deploy workflow runs it on the runner (owner role; here the local superuser).
compose run --rm --no-deps -e "ConnectionStrings__Migrations=Host=db;Database=vybe;Username=vybe;Password=$DB_PASSWORD" api release
compose up -d --wait --wait-timeout 180 api web

origin="https://localhost:$HTTPS_PORT"
ca="$work/root.crt"
docker cp vybe-web-1:/data/caddy/pki/authorities/local/root.crt "$ca"
get() { curl -sS --cacert "$ca" "$@"; }
status() { get -o /dev/null -w '%{http_code}' "$@"; }
check() { if "$@"; then echo "ok: ${*: -1}"; else echo "FAILED: $*" >&2; exit 1; fi; }
absent() { ! grep -qi "$1" "$2"; }

get -D "$work/h" -o "$work/index.html" "$origin/hub"
check grep -q '<vybe-root>' "$work/index.html"
check grep -qi "^content-security-policy:.*connect-src 'self' https://example.supabase.co;.*frame-ancestors 'none'" "$work/h"
check grep -qi '^strict-transport-security: max-age=31536000' "$work/h"
check grep -qi '^referrer-policy: no-referrer' "$work/h"
check grep -qi '^x-content-type-options: nosniff' "$work/h"
check absent '^server:' "$work/h"
check test "$(get "$origin/runtime-config.json")" = '{"supabaseUrl":"https://example.supabase.co","supabaseAnonKey":"dummy-anon-key-for-tests"}'

# /api goes to the API container; the API's own (stricter) CSP and HSTS stay.
get -D "$work/a" -o /dev/null "$origin/api/v1/catalog/items"
check grep -q '^HTTP/[0-9.]* 200' "$work/a"
check grep -qi "^content-security-policy: default-src 'none'" "$work/a"
check grep -qi '^strict-transport-security: max-age=' "$work/a"
check test "$(status "$origin/health/ready")" = 404
check test "$(status "$origin/health/live")" = 404
check test "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$HTTP_PORT/")" = 308

# The client address: a forged X-Forwarded-For never gives a new rate-limit bucket (10 guests per address per minute).
codes=""
for i in $(seq 1 11); do
  codes="$codes $(status -X POST -H 'Content-Type: application/json' -H 'X-Requested-With: vybe-web' \
    -H "X-Forwarded-For: 198.51.100.$i" -d '{"birthYear":1990,"birthMonth":6}' "$origin/api/v1/guests")"
done
echo "guest creation codes:$codes"
check test "$codes" = " 201 201 201 201 201 201 201 201 201 201 429"

# Container hardening as compose.yml declares it.
for c in vybe-api-1 vybe-web-1; do
  check test "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.State.Health.Status}}' "$c")" = "true [ALL] healthy"
done
check test "$(docker port vybe-api-1 | wc -l | tr -d ' ')" = 0
echo "test-stack: all checks passed"
