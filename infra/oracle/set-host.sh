#!/usr/bin/env bash
# Changes the public host name of VYBE on the VM (for example from <ip>.sslip.io to an own domain), without the
# secrets. Runbook: docs/runbooks/oracle-vm.md, "Move to an own domain". Run it on the VM as the admin user:
#
#   bash ~/vybe-oracle/set-host.sh play.example.com
#
# It changes the three values that contain the host: VYBE_PUBLIC_HOST (web.env), Challenges__ShareBaseUrl and
# Content__AssetBaseUrl (api.env). It keeps a copy of each file (<file>.before-host) and prints names only.
# The running containers read the new values at the next deploy.
set -euo pipefail

ENV_DIR=/etc/vybe
host="${1:-}"

# A DNS name in lowercase: labels of letters, digits and hyphens, at least one dot, no scheme, port or path.
if ! [[ "$host" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$ ]]; then
  echo "usage: set-host.sh <host name in lowercase, for example play.example.com>" >&2
  exit 2
fi

for file in "$ENV_DIR/api.env" "$ENV_DIR/web.env"; do
  sudo test -f "$file" || { echo "$file is missing: run stage-env.sh first." >&2; exit 1; }
done

origin="https://$host"

# set_value <file> <name> <value>: replaces the line "<name>=..." (the value single-quoted, as stage-env.sh writes it).
set_value() {
  sudo grep -q "^$2=" "$1" || { echo "$2 is missing in $1: stopped." >&2; exit 1; }
  sudo sed -i "s|^$2=.*|$2='$3'|" "$1"
}

for file in "$ENV_DIR/api.env" "$ENV_DIR/web.env"; do
  sudo cp -p "$file" "$file.before-host"
done

set_value "$ENV_DIR/web.env" VYBE_PUBLIC_HOST "$host"
set_value "$ENV_DIR/api.env" Challenges__ShareBaseUrl "$origin"
set_value "$ENV_DIR/api.env" Content__AssetBaseUrl "$origin/assets"

echo "Changed (names only): VYBE_PUBLIC_HOST, Challenges__ShareBaseUrl, Content__AssetBaseUrl. Copies: *.before-host" >&2
sudo stat -c '%U:%G %a %n' "$ENV_DIR/api.env" "$ENV_DIR/web.env" >&2
echo "Next: set the GitHub variables VYBE_PUBLIC_ORIGIN and VYBE_UPTIME_ORIGIN to $origin, the Supabase Auth URLs," >&2
echo "then deploy (runbook, \"Move to an own domain\")." >&2
