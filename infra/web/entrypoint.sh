#!/bin/sh
# Writes /runtime-config.json of the web app (apps/web/README.md) from the environment, then starts Caddy.
# Both values are public (the Supabase origin and the publishable key). Strict formats, so the JSON file and the
# CSP header (Caddyfile, connect-src) cannot get other content.
set -eu

url="${VYBE_SUPABASE_URL:-}"
key="${VYBE_SUPABASE_ANON_KEY:-}"
dir=/tmp/vybe-runtime

mkdir -p "$dir"
rm -f "$dir/runtime-config.json"

if [ -z "$url" ] && [ -z "$key" ]; then
  echo "VYBE_SUPABASE_URL and VYBE_SUPABASE_ANON_KEY are not set: sign-in is off (guest-only)." >&2
else
  # "case" checks the whole value (grep checks each line, so a second line could pass unchecked).
  case "$url" in
    *[!a-z0-9.:/-]*) url="" ;;
  esac
  if ! printf '%s' "$url" | grep -Eq '^https://[a-z0-9]([a-z0-9.-]*[a-z0-9])?(:[0-9]{1,5})?$'; then
    echo "VYBE_SUPABASE_URL must be an https origin without a path, for example https://<project ref>.supabase.co" >&2
    exit 1
  fi
  case "$key" in
    *[!A-Za-z0-9._-]*) key="" ;;
  esac
  if [ "${#key}" -lt 20 ] || [ "${#key}" -gt 4096 ]; then
    echo "VYBE_SUPABASE_ANON_KEY must be the Supabase publishable (anon) key" >&2
    exit 1
  fi
  printf '{"supabaseUrl":"%s","supabaseAnonKey":"%s"}\n' "$url" "$key" > "$dir/runtime-config.json"
fi

exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
