#!/usr/bin/env bash
# Writes the VYBE production settings on the VM: /etc/vybe/api.env and /etc/vybe/web.env (root:root, mode 600).
# Runbook: docs/runbooks/oracle-vm.md, "Stage the settings". Run it on the VM as the admin user (it uses sudo):
#
#   bash ~/vybe-oracle/stage-env.sh
#
# The script asks for each secret without echo (paste it from the password manager). The values go to the files
# through stdin only: never on the screen, in shell history, in a file outside /etc/vybe or in process arguments.
# Run it again to change a value: it writes both files again.
# The invite secret stays the same unless you add --new-invite-secret (a new value ends all open invites).
set -euo pipefail

REF="pkrudxjeihxrkkdlfdmr"                          # Supabase project vybe-prod (eu-central-1)
POOLER="aws-0-eu-central-1.pooler.supabase.com"
BUCKET="vybe-exports"
ENV_DIR=/etc/vybe

new_invite_secret=false
case "${1:-}" in
  "") ;;
  --new-invite-secret) new_invite_secret=true ;;
  *) echo "usage: stage-env.sh [--new-invite-secret]" >&2; exit 2 ;;
esac

# ask <prompt> <variable>: reads one value without echo into <variable>.
ask() {
  local value
  IFS= read -rs -p "$1: " value </dev/tty
  echo >&2
  if [ -z "$value" ]; then echo "Empty value: stopped, nothing was written." >&2; exit 1; fi
  printf -v "$2" '%s' "$value"
}

# Values are single-quoted in the env files: Docker Compose then reads them literally (no $ interpolation).
check_value() {
  case "$2" in *"'"* | *$'\n'* | *$'\r'*) echo "$1 contains a quote or a line break: stopped." >&2; exit 1 ;; esac
}

# The suggested host name: <public IPv4 with dashes>.sslip.io (no domain needed; see the runbook).
ip=$(curl -fsS --max-time 5 https://checkip.amazonaws.com 2>/dev/null | tr -d '[:space:]' || true)
suggested=""
if [[ "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then suggested="${ip//./-}.sslip.io"; fi

IFS= read -r -p "Public host name [${suggested:-type a domain or <ip-with-dashes>.sslip.io}]: " host </dev/tty
host="${host:-$suggested}"
host="${host,,}"
if ! [[ "$host" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$ ]]; then
  echo "\"$host\" is not a host name (no scheme, no path, no port)." >&2
  exit 1
fi

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
for name in api_password anon_key service_key s3_key_id s3_secret; do check_value "$name" "${!name}"; done

# Keep the invite secret of the current file, so open invites stay valid; else 32 new random bytes.
invite_secret=""
if [ "$new_invite_secret" = false ]; then
  invite_secret=$(sudo sed -n "s/^Challenges__InviteSecret='\(.*\)'$/\1/p" "$ENV_DIR/api.env" 2>/dev/null || true)
fi
if [ -z "$invite_secret" ]; then invite_secret="$(openssl rand -base64 32)"; fi

# write <file>: stdin to <file>, root:root 0600, replaced in one step (no partial file, never world-readable).
write() {
  sudo install -d -o root -g root -m 0700 "$ENV_DIR"
  sudo sh -c 'umask 077 && cat > "$1.new" && chown root:root "$1.new" && chmod 600 "$1.new" && mv "$1.new" "$1"' sh "$1"
}

origin="https://$host"
echo "Writing $ENV_DIR/api.env ..." >&2
{
  printf "ConnectionStrings__Vybe='Host=%s;Port=6543;Database=postgres;Username=vybe_api.%s;Password=%s;SSL Mode=Require;Max Auto Prepare=0;Maximum Pool Size=20;No Reset On Close=true'\n" \
    "$POOLER" "$REF" "$api_password"
  printf "Auth__Supabase__Authority='https://%s.supabase.co/auth/v1'\n" "$REF"
  printf "Auth__Supabase__ApiKey='%s'\n" "$anon_key"
  printf "Auth__Supabase__ServiceRoleKey='%s'\n" "$service_key"
  printf "Challenges__InviteSecret='%s'\n" "$invite_secret"
  printf "Challenges__ShareBaseUrl='%s'\n" "$origin"
  printf "Content__AssetBaseUrl='%s/assets'\n" "$origin"
  printf "Storage__Exports__Bucket='%s'\n" "$BUCKET"
  printf "Storage__Exports__Region='eu-central-1'\n"
  printf "Storage__Exports__ServiceUrl='https://%s.storage.supabase.co/storage/v1/s3'\n" "$REF"
  printf "Storage__Exports__AccessKeyId='%s'\n" "$s3_key_id"
  printf "Storage__Exports__SecretAccessKey='%s'\n" "$s3_secret"
} | write "$ENV_DIR/api.env"

echo "Writing $ENV_DIR/web.env ..." >&2
{
  printf "VYBE_PUBLIC_HOST='%s'\n" "$host"
  printf "VYBE_SUPABASE_URL='https://%s.supabase.co'\n" "$REF"
  printf "VYBE_SUPABASE_ANON_KEY='%s'\n" "$anon_key"
} | write "$ENV_DIR/web.env"

unset api_password anon_key service_key s3_key_id s3_secret invite_secret
echo "Done. Names only (never values):" >&2
sudo sed -n 's/=.*//p' "$ENV_DIR/api.env" "$ENV_DIR/web.env" >&2
sudo stat -c '%U:%G %a %n' "$ENV_DIR/api.env" "$ENV_DIR/web.env" >&2
echo "Public origin: $origin. Set it in Supabase Auth (Site URL and redirect URL) and in the GitHub variable" >&2
echo "VYBE_PUBLIC_ORIGIN; a running stack reads the new files at the next deploy (or: runbook, \"Restart\")." >&2
