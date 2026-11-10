#!/usr/bin/env bash
# Stages the Fly.io secrets of VYBE production (runbook deploy-and-rollback.md, one-time setup steps 8-9).
#
#   infra/fly/stage-secrets.sh
#
# The script asks for each secret without echo (paste it from the password manager). The values go to
# `fly secrets import --stage` through stdin only: never on the screen, in shell history, in a file or in
# process arguments. `--stage` keeps them for the first deploy; nothing starts and nothing is billed.
# Non-secret values (project ref, pooler host, origin, bucket) are fixed below.
set -euo pipefail

REF="pkrudxjeihxrkkdlfdmr"                          # Supabase project vybe-prod (eu-central-1)
POOLER="aws-0-eu-central-1.pooler.supabase.com"
API_APP="vybe-prod-api"
WEB_APP="vybe-prod-web"
ORIGIN="https://vybe-prod-web.fly.dev"
BUCKET="vybe-exports"

command -v fly >/dev/null || { echo "flyctl is not installed (brew install flyctl)" >&2; exit 1; }
fly auth whoami >/dev/null 2>&1 || { echo "Log in first: fly auth login" >&2; exit 1; }

# ask <prompt> <variable>: reads one value without echo into <variable>.
ask() {
  local value
  IFS= read -rs -p "$1: " value </dev/tty
  echo >&2
  if [ -z "$value" ]; then echo "Empty value: stopped, nothing was staged." >&2; exit 1; fi
  printf -v "$2" '%s' "$value"
}

# Set by ask (printf -v).
api_password="" anon_key="" service_key="" s3_key_id="" s3_secret=""
echo "Paste each value from the password manager and press Enter. Nothing is shown while you paste." >&2
ask "1/5 vybe_api password (Passwords: VYBE - vybe_api)" api_password
ask "2/5 Supabase anon key (API Keys > Legacy API keys > anon, starts with eyJ)" anon_key
ask "3/5 Supabase service_role key (API Keys > Legacy API keys > service_role, starts with eyJ)" service_key
ask "4/5 S3 access key ID (Passwords: VYBE - Supabase S3, username)" s3_key_id
ask "5/5 S3 secret access key (Passwords: VYBE - Supabase S3, password)" s3_secret

case "$api_password" in *";"*) echo "The vybe_api password contains ';', which breaks the connection string." >&2; exit 1 ;; esac
case "$anon_key" in eyJ*) ;; *) echo "The anon key must start with eyJ (legacy anon key)." >&2; exit 1 ;; esac
case "$service_key" in eyJ*) ;; *) echo "The service_role key must start with eyJ (legacy service_role key)." >&2; exit 1 ;; esac
[ "$anon_key" != "$service_key" ] || { echo "The anon and service_role keys are the same: check what you pasted." >&2; exit 1; }

# 32 random bytes for invite tokens. Nobody needs to know it; a new value only ends open invites.
invite_secret="$(openssl rand -base64 32)"

echo "Staging the API secrets on $API_APP ..." >&2
{
  printf 'ConnectionStrings__Vybe=Host=%s;Port=6543;Database=postgres;Username=vybe_api.%s;Password=%s;SSL Mode=Require;Max Auto Prepare=0;Maximum Pool Size=20;No Reset On Close=true\n' \
    "$POOLER" "$REF" "$api_password"
  printf 'Auth__Supabase__Authority=https://%s.supabase.co/auth/v1\n' "$REF"
  printf 'Auth__Supabase__ApiKey=%s\n' "$anon_key"
  printf 'Auth__Supabase__ServiceRoleKey=%s\n' "$service_key"
  printf 'Challenges__InviteSecret=%s\n' "$invite_secret"
  printf 'Challenges__ShareBaseUrl=%s\n' "$ORIGIN"
  printf 'Storage__Exports__Bucket=%s\n' "$BUCKET"
  printf 'Storage__Exports__Region=eu-central-1\n'
  printf 'Storage__Exports__ServiceUrl=https://%s.storage.supabase.co/storage/v1/s3\n' "$REF"
  printf 'Storage__Exports__AccessKeyId=%s\n' "$s3_key_id"
  printf 'Storage__Exports__SecretAccessKey=%s\n' "$s3_secret"
} | fly secrets import --stage -a "$API_APP" >/dev/null

echo "Staging the web settings on $WEB_APP ..." >&2
{
  printf 'VYBE_SUPABASE_URL=https://%s.supabase.co\n' "$REF"
  printf 'VYBE_SUPABASE_ANON_KEY=%s\n' "$anon_key"
} | fly secrets import --stage -a "$WEB_APP" >/dev/null

unset api_password anon_key service_key s3_key_id s3_secret invite_secret
echo "Done. Names only (never values):" >&2
fly secrets list -a "$API_APP"
fly secrets list -a "$WEB_APP"
